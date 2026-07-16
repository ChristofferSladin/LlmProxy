using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace LlmProxy;

/// <summary>
/// Forwards an OpenAI-compatible request to the resolved provider, injecting auth and the
/// system prompt, then trying each candidate model in order (with retries) until one responds.
/// Failover is decided on the upstream response headers, so a stream is never half-sent then switched.
/// </summary>
public sealed class ProxyService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ProviderRegistry _registry;
    private readonly ModelCatalog _catalog;
    private readonly RoutingState _routing;
    private readonly ILogger<ProxyService> _logger;

    // Remember the last model that worked per provider so we try it first (avoids re-probing every request).
    private readonly ConcurrentDictionary<string, string> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    public ProxyService(IHttpClientFactory httpFactory, ProviderRegistry registry, ModelCatalog catalog, RoutingState routing, ILogger<ProxyService> logger)
    {
        _httpFactory = httpFactory;
        _registry = registry;
        _catalog = catalog;
        _routing = routing;
        _logger = logger;
    }

    public async Task ForwardJsonAsync(HttpContext http, string upstreamPath, CancellationToken ct)
    {
        JsonObject? body;
        try
        {
            body = (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct)) as JsonObject;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_request_error", $"Malformed JSON body: {ex.Message}", ct);
            return;
        }

        if (body is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_request_error", "Request body must be a JSON object.", ct);
            return;
        }

        var clientModel = body["model"]?.GetValue<string>();

        RouteTarget route;
        try { route = _registry.Resolve(clientModel); }
        catch (Exception ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message, ct);
            return;
        }

        var apiKey = route.Provider.ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            await WriteErrorAsync(http, StatusCodes.Status401Unauthorized, "authentication_error",
                $"No API key configured for provider '{route.ProviderName}'. Set its ApiKeyEnv/ApiKey.", ct);
            return;
        }

        // Proxy-owned global system prompt: replace any client system message with the composed one
        // (provider base + model-agnostic identity anchor). Composed even when the provider has no base
        // prompt, so the anchor is injected on its own. When the composition is null/empty (no base and
        // no anchor) nothing is injected and client messages are left untouched — today's behavior.
        var systemContent = PromptComposer.Compose(route.Provider.SystemPrompt, _registry.Options.IdentityAnchor);
        if (!string.IsNullOrEmpty(systemContent) && body["messages"] is JsonArray messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
                if (messages[i] is JsonObject m && m["role"]?.GetValue<string>() == "system")
                    messages.RemoveAt(i);
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemContent });
        }

        // Classify request shape once (T2: does it carry tools?). Drives the hard tool-capability
        // filter in candidate build and gates whether a tool-referencing upstream error demotes a model.
        var hasTools = RequestClassifier.HasTools(body);

        var isStream = body["stream"]?.GetValue<bool>() ?? false;
        var url = $"{route.Provider.BaseUrl.TrimEnd('/')}/{upstreamPath.TrimStart('/')}";
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(5, _registry.Options.AttemptTimeoutSeconds));
        var maxAttempts = Math.Max(1, _registry.Options.MaxAttemptsPerModel);
        var cooldownWindow = TimeSpan.FromSeconds(Math.Max(0, _registry.Options.CooldownSeconds));
        var client = _httpFactory.CreateClient("upstream");

        IReadOnlyList<string> candidates;
        try
        {
            candidates = await BuildCandidatesAsync(route, hasTools, ct);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "upstream_error",
                $"Could not list models for provider '{route.ProviderName}': {ex.Message}", ct);
            return;
        }

        if (candidates.Count == 0)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "upstream_error",
                $"No chat-capable models available for provider '{route.ProviderName}'.", ct);
            return;
        }

        var failures = new List<string>();

        foreach (var model in candidates)
        {
            body["model"] = model;
            var payload = body.ToJsonString();

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var sw = Stopwatch.StartNew();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(attemptTimeout);

                HttpResponseMessage resp;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                    if (isStream) req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                    resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // client (LM Studio) aborted — stop, nothing to send.
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    failures.Add($"{model}:timeout");
                    Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "timeout", sw);
                    break; // a hang won't improve on retry — go to the next model.
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    failures.Add($"{model}:neterr");
                    Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "neterr:" + ex.GetType().Name, sw);
                    if (attempt < maxAttempts) { await BackoffAsync(attempt, ct); continue; }
                    break;
                }

                var status = (int)resp.StatusCode;

                if (status is >= 200 and < 300)
                {
                    // Some providers (e.g. NVIDIA NIM) return HTTP 200 but carry an error payload in the
                    // body — a ResourceExhausted / rate-limit envelope instead of a completion. Peek the
                    // body before committing, so we can still fail over, and so the announced model name
                    // reflects whichever candidate actually answers rather than the first to return 200.
                    PeekResult peek;
                    try
                    {
                        peek = await PeekBodyAsync(resp, isStream, timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        resp.Dispose();
                        return; // client aborted.
                    }
                    catch (OperationCanceledException)
                    {
                        resp.Dispose();
                        sw.Stop();
                        failures.Add($"{model}:timeout");
                        Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "timeout", sw);
                        break; // slow time-to-first-byte won't improve on retry — next model.
                    }
                    catch (Exception ex)
                    {
                        resp.Dispose();
                        sw.Stop();
                        failures.Add($"{model}:neterr");
                        Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "neterr:" + ex.GetType().Name, sw);
                        if (attempt < maxAttempts) { await BackoffAsync(attempt, ct); continue; }
                        break;
                    }

                    if (peek.IsError)
                    {
                        resp.Dispose();
                        sw.Stop();
                        failures.Add($"{model}:{Truncate(peek.Detail ?? "200-error", 60)}");
                        Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "200-err", sw);
                        // Bench this model so failover doesn't walk straight back into the same wall.
                        _routing.RegisterCooldown(model, cooldownWindow);
                        // If this was a tools request and the body-error references tool/function calling,
                        // learn that this model can't do tools so future tools requests skip it (T2).
                        if (hasTools && (IsToolCapabilityError(peek.Detail) || IsToolCapabilityError(peek.Text)))
                            _routing.MarkToolIncapable(model);
                        // 200 with an error body is transient (busy worker / quota): retry, then fail over.
                        if (attempt < maxAttempts) { await BackoffAsync(attempt, ct); continue; }
                        break;
                    }

                    _lastGood[route.ProviderName] = model; // sticky: try this first next time
                    await RelayAsync(http, resp, peek, isStream, upstreamPath, model, ct);
                    resp.Dispose();
                    sw.Stop();
                    Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, "200", sw);
                    return;
                }

                var detail = await SafeReadAsync(resp);
                resp.Dispose();
                sw.Stop();
                failures.Add($"{model}:{status}");
                Log(upstreamPath, clientModel, model, route.ProviderName, isStream, attempt, status.ToString(), sw);

                // A 429 means the model's pool is saturated: bench it like a 200-err so failover skips it.
                if (status == 429) _routing.RegisterCooldown(model, cooldownWindow);

                // A tools request rejected with tool/function-calling phrasing means this model can't do
                // tools: learn it so future tools requests skip it (T2). Applies whatever the status
                // sub-branch below does with the response (retry / surface / next model).
                if (hasTools && IsToolCapabilityError(detail))
                    _routing.MarkToolIncapable(model);

                // 5xx / 429 are transient: retry this model, then fall through to the next.
                if (status is 429 or >= 500)
                {
                    if (attempt < maxAttempts) { await BackoffAsync(attempt, ct); continue; }
                    break;
                }

                // 404 = model unavailable → try the next model.
                if (status == 404) break;

                // Other 4xx (bad request, auth, quota) repeat identically for every model — surface it.
                await WriteErrorAsync(http, status, "upstream_error",
                    $"Provider '{route.ProviderName}' rejected model '{model}': {Truncate(detail, 300)}", ct);
                return;
            }
        }

        await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "upstream_error",
            $"All candidate models failed for provider '{route.ProviderName}': {string.Join(", ", failures)}", ct);
    }

    // Explicit config wins; otherwise pull chat-capable models live, last-good first, capped.
    // Benched (cooling-down) models are dropped from the ordered list before the cap so failover
    // doesn't re-probe a model that just hit its wall — but never dead-ends (see DropCoolingDown).
    private async Task<IReadOnlyList<string>> BuildCandidatesAsync(RouteTarget route, bool hasTools, CancellationToken ct)
    {
        if (route.Provider.ForceModels.Count > 0 || !route.Provider.DynamicModels)
            return DropToolIncapable(DropCoolingDown(route.Models), hasTools);

        var dynamic = DropToolIncapable(DropCoolingDown(await _catalog.GetChatCandidatesAsync(route.ProviderName, route.Provider, ct)), hasTools);
        var cap = Math.Max(1, _registry.Options.MaxDynamicCandidates);

        var ordered = new List<string>(cap);
        if (_lastGood.TryGetValue(route.ProviderName, out var last) && dynamic.Contains(last))
            ordered.Add(last);
        foreach (var id in dynamic)
        {
            if (ordered.Count >= cap) break;
            if (!ordered.Contains(id)) ordered.Add(id);
        }
        return ordered;
    }

    // Filter out models currently cooling down, preserving order. Never dead-ends: if every candidate
    // is benched, the cooldowns are ignored and the full ordered list is returned so a request is
    // always attempted against *something*.
    private IReadOnlyList<string> DropCoolingDown(IReadOnlyList<string> ordered)
    {
        var live = new List<string>(ordered.Count);
        foreach (var id in ordered)
            if (!_routing.IsCoolingDown(id)) live.Add(id);
        return live.Count > 0 ? live : ordered;
    }

    // Hard filter for tools requests: drop models learned to be tool-incapable, preserving order. A
    // no-op when the request carries no tools (non-tools requests may still use a tool-incapable model).
    // Never dead-ends: if filtering empties the list, the filter is ignored and the input is returned so
    // a request is always attempted against *something* (mirrors DropCoolingDown).
    private IReadOnlyList<string> DropToolIncapable(IReadOnlyList<string> ordered, bool hasTools)
    {
        if (!hasTools) return ordered;
        var capable = new List<string>(ordered.Count);
        foreach (var id in ordered)
            if (_routing.IsToolCapable(id)) capable.Add(id);
        return capable.Count > 0 ? capable : ordered;
    }

    // Narrow, case-insensitive markers for an upstream error that means "this model can't do tool/function
    // calls". Only consulted on a request that carried tools, so the optimistic default means a missed
    // match costs at most one wasted attempt — never a false demotion of a capable model.
    private static readonly string[] ToolErrorMarkers = { "tool", "function call", "function_call" };

    private static bool IsToolCapabilityError(string? text) =>
        !string.IsNullOrEmpty(text) &&
        ToolErrorMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private async Task RelayAsync(HttpContext http, HttpResponseMessage upstream, PeekResult peek, bool isStream, string upstreamPath, string model, CancellationToken ct)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString()
                                    ?? (isStream ? "text/event-stream" : "application/json");
        if (isStream) http.Response.Headers["Cache-Control"] = "no-cache";

        // Only annotate chat responses; other endpoints (embeddings, etc.) pass through untouched.
        var announce = _registry.Options.AnnounceModel && upstreamPath == "chat/completions";
        var header = announce ? _registry.Options.ModelAnnounceFormat.Replace("{model}", model) : null;

        if (!isStream)
        {
            // Non-streaming: the whole body was buffered during the peek.
            if (announce && peek.ReachedEnd)
            {
                try
                {
                    var obj = JsonNode.Parse(peek.Text)!.AsObject();
                    foreach (var choice in obj["choices"]?.AsArray() ?? new JsonArray())
                        if (choice is JsonObject co && co["message"] is JsonObject msg)
                            msg["content"] = header + (msg["content"]?.GetValue<string>() ?? "");
                    await http.Response.WriteAsync(obj.ToJsonString(), ct);
                    return;
                }
                catch { /* shape surprise — fall through to raw passthrough */ }
            }

            if (peek.Prefix.Length > 0) await http.Response.Body.WriteAsync(peek.Prefix, ct);
            if (!peek.ReachedEnd) await peek.Body.CopyToAsync(http.Response.Body, ct);
            await http.Response.Body.FlushAsync(ct);
            return;
        }

        // Streaming: emit the announce as a synthetic first SSE chunk. The peek has already confirmed
        // this model produced a real completion, so the name is the model that actually answered.
        if (announce)
        {
            var chunk = new JsonObject
            {
                ["id"] = "proxy-announce",
                ["object"] = "chat.completion.chunk",
                ["model"] = model,
                ["choices"] = new JsonArray
                {
                    new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = header } },
                },
            };
            await http.Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        // Replay the bytes consumed during the peek, then splice the rest of the upstream stream.
        if (peek.Prefix.Length > 0) await http.Response.Body.WriteAsync(peek.Prefix, ct);
        if (!peek.ReachedEnd) await peek.Body.CopyToAsync(http.Response.Body, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Buffered look at an upstream 200 response so we can detect body-carried errors (some providers
    /// return HTTP 200 with an error envelope) and then replay the consumed bytes. For non-streaming
    /// the whole body is buffered; for streaming only the first SSE event(s) are sampled and the
    /// remainder stays on <see cref="Body"/> for the relay to splice through.
    /// </summary>
    private sealed record PeekResult(bool IsError, string? Detail, byte[] Prefix, Stream Body, bool ReachedEnd)
    {
        public string Text => Encoding.UTF8.GetString(Prefix);
    }

    // How much to buffer while peeking. Streaming: enough for the first event(s). Non-streaming:
    // large enough to hold a whole chat completion so it can be rewritten for the announce.
    private const int StreamPeekBytes = 16 * 1024;
    private const int BufferPeekBytes = 4 * 1024 * 1024;

    private static async Task<PeekResult> PeekBodyAsync(HttpResponseMessage resp, bool isStream, CancellationToken ct)
    {
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var max = isStream ? StreamPeekBytes : BufferPeekBytes;
        var buf = new MemoryStream();
        var tmp = new byte[8192];
        var reachedEnd = false;

        while (buf.Length < max)
        {
            var n = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), ct);
            if (n == 0) { reachedEnd = true; break; }
            buf.Write(tmp, 0, n);
            if (isStream && HasSseEventBoundary(buf)) break; // first full event captured — enough to judge.
        }

        var prefix = buf.ToArray();
        var (isError, detail) = ClassifyBody(Encoding.UTF8.GetString(prefix));
        return new PeekResult(isError, detail, prefix, stream, reachedEnd);
    }

    private static bool HasSseEventBoundary(MemoryStream buf)
    {
        var s = Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
        return s.Contains("\n\n") || s.Contains("\r\n\r\n");
    }

    // Distinctive text markers for error bodies that aren't a parseable JSON error envelope (plain-text
    // upstream failures). Kept narrow so legitimate completion content is never mistaken for an error.
    private static readonly string[] ErrorTextMarkers = { "ResourceExhausted", "request limit reached" };

    /// <summary>
    /// Decide whether a 200 body is actually an error. Defensive across shapes so new providers are
    /// covered: an OpenAI-style <c>{"error":{...}}</c> envelope (raw JSON or inside an SSE <c>data:</c>
    /// line), an <c>{"object":"error"}</c> payload, or a distinctive plain-text marker. A payload that
    /// carries <c>choices</c> is treated as a genuine completion and always passes.
    /// </summary>
    private static (bool IsError, string? Detail) ClassifyBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, null);

        var sawChoices = false;
        foreach (var json in ExtractJsonCandidates(text))
        {
            JsonObject? obj;
            try { obj = JsonNode.Parse(json) as JsonObject; }
            catch { continue; }
            if (obj is null) continue;

            if (obj["error"] is JsonNode err) return (true, ErrText(err));
            if (obj["object"]?.GetValue<string>() == "error")
                return (true, obj["message"]?.GetValue<string>() ?? Truncate(json, 200));
            if (obj["choices"] is JsonArray) sawChoices = true;
        }

        if (sawChoices) return (false, null); // a real completion — trust it.

        foreach (var marker in ErrorTextMarkers)
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return (true, Truncate(text.Trim(), 200));

        return (false, null); // unrecognised, but not obviously an error — pass through unchanged.
    }

    // Candidate JSON strings to inspect: the whole body (raw JSON error object), plus each SSE `data:`
    // payload. Truncated/partial JSON simply fails to parse and is skipped by the caller.
    private static IEnumerable<string> ExtractJsonCandidates(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{')) yield return trimmed;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length > 0 && payload != "[DONE]" && payload.StartsWith('{'))
                yield return payload;
        }
    }

    private static string ErrText(JsonNode err)
    {
        if (err is JsonObject eo)
            return eo["message"]?.GetValue<string>() ?? Truncate(eo.ToJsonString(), 200);
        try { return err.GetValue<string>(); }
        catch { return Truncate(err.ToJsonString(), 200); }
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Log(string path, string? client, string model, string provider, bool stream, int attempt, string outcome, Stopwatch sw)
    {
        if (!_registry.Options.LogRequests) return;
        _logger.LogInformation("{Path} client={Client} model={Model} provider={Provider} stream={Stream} attempt={Attempt} outcome={Outcome} {Elapsed}ms",
            path, client ?? "(none)", model, provider, stream, attempt, outcome, sw.ElapsedMilliseconds);
    }

    private static async Task WriteErrorAsync(HttpContext http, int status, string type, string message, CancellationToken ct)
    {
        if (http.Response.HasStarted) return;
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        var payload = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = status },
        };
        await http.Response.WriteAsync(payload.ToJsonString(), ct);
    }
}
