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
    private readonly ILogger<ProxyService> _logger;

    // Remember the last model that worked per provider so we try it first (avoids re-probing every request).
    private readonly ConcurrentDictionary<string, string> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    public ProxyService(IHttpClientFactory httpFactory, ProviderRegistry registry, ModelCatalog catalog, ILogger<ProxyService> logger)
    {
        _httpFactory = httpFactory;
        _registry = registry;
        _catalog = catalog;
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

        // Proxy-owned global system prompt: replace any client system message with ours.
        if (!string.IsNullOrWhiteSpace(route.Provider.SystemPrompt) && body["messages"] is JsonArray messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
                if (messages[i] is JsonObject m && m["role"]?.GetValue<string>() == "system")
                    messages.RemoveAt(i);
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = route.Provider.SystemPrompt });
        }

        var isStream = body["stream"]?.GetValue<bool>() ?? false;
        var url = $"{route.Provider.BaseUrl.TrimEnd('/')}/{upstreamPath.TrimStart('/')}";
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(5, _registry.Options.AttemptTimeoutSeconds));
        var maxAttempts = Math.Max(1, _registry.Options.MaxAttemptsPerModel);
        var client = _httpFactory.CreateClient("upstream");

        IReadOnlyList<string> candidates;
        try
        {
            candidates = await BuildCandidatesAsync(route, ct);
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
                    _lastGood[route.ProviderName] = model; // sticky: try this first next time
                    await RelayAsync(http, resp, isStream, upstreamPath, model, ct);
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
    private async Task<IReadOnlyList<string>> BuildCandidatesAsync(RouteTarget route, CancellationToken ct)
    {
        if (route.Provider.ForceModels.Count > 0 || !route.Provider.DynamicModels)
            return route.Models;

        var dynamic = await _catalog.GetChatCandidatesAsync(route.ProviderName, route.Provider, ct);
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

    private async Task RelayAsync(HttpContext http, HttpResponseMessage upstream, bool isStream, string upstreamPath, string model, CancellationToken ct)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString()
                                    ?? (isStream ? "text/event-stream" : "application/json");
        if (isStream) http.Response.Headers["Cache-Control"] = "no-cache";

        // Only annotate chat responses; other endpoints (embeddings, etc.) pass through untouched.
        var announce = _registry.Options.AnnounceModel && upstreamPath == "chat/completions";
        var header = announce ? _registry.Options.ModelAnnounceFormat.Replace("{model}", model) : null;

        if (announce && isStream)
        {
            // Emit a synthetic first SSE chunk carrying the model name as assistant content.
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
        else if (announce)
        {
            // Non-streaming: buffer, prepend the model line to the message content, rewrite.
            var text = await upstream.Content.ReadAsStringAsync(ct);
            try
            {
                var obj = JsonNode.Parse(text)!.AsObject();
                foreach (var choice in obj["choices"]?.AsArray() ?? new JsonArray())
                    if (choice is JsonObject co && co["message"] is JsonObject msg)
                        msg["content"] = header + (msg["content"]?.GetValue<string>() ?? "");
                await http.Response.WriteAsync(obj.ToJsonString(), ct);
            }
            catch
            {
                await http.Response.WriteAsync(text, ct); // fall back to passthrough on any shape surprise
            }
            return;
        }

        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        await upstreamStream.CopyToAsync(http.Response.Body, ct);
        await http.Response.Body.FlushAsync(ct);
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
