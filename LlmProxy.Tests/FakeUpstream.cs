using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace LlmProxy.Tests;

/// <summary>
/// A scripted upstream. Tests enqueue <c>(modelMatch, status, body)</c> responses matched on the
/// request body's <c>model</c> field; the handler returns them in order and records every outgoing
/// request so tests can assert call order, which models were tried, and inspect the request JSON
/// (e.g. the injected system prompt).
/// </summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    /// <summary>A captured outgoing upstream request.</summary>
    public sealed record CapturedRequest(string Method, string Url, string Model, string Body);

    private sealed record Scripted(string? ModelMatch, int Status, string Body, bool Streaming, int DelayMs);

    private readonly List<Scripted> _queue = new();
    private readonly object _lock = new();

    /// <summary>
    /// Fakes a dynamic provider's live /v1/models catalog. When non-empty, any GET request (the
    /// catalog fetch) is answered with this id list instead of going through the scripted POST queue —
    /// and is NOT recorded in <see cref="Requests"/>/<see cref="TriedModels"/>, since it's not a chat
    /// completion attempt.
    /// </summary>
    public List<string> CatalogModels { get; set; } = new();

    /// <summary>Every outgoing request, in order. The model id sent and the full request JSON body.</summary>
    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>The model id of each recorded request, in order (convenience for asserting the attempt chain).</summary>
    public IReadOnlyList<string> TriedModels
    {
        get { lock (_lock) return Requests.Select(r => r.Model).ToList(); }
    }

    /// <summary>
    /// Enqueue a scripted response. <paramref name="modelMatch"/> is a case-insensitive substring
    /// matched against the request body's <c>model</c> field; null is a catch-all. Responses are
    /// consumed in order among those whose match applies to the incoming request.
    ///
    /// <paramref name="delayMs"/> (T5): simulate a slow upstream by awaiting this many milliseconds,
    /// honoring the caller's cancellation token, before returning the response — lets a test prove
    /// attempt-timeout behavior (the delay outlasting the effective timeout throws
    /// <see cref="TaskCanceledException"/> exactly like a real hung upstream would) without a real wait.
    /// </summary>
    public FakeUpstream Enqueue(string? modelMatch, int status, string body, bool streaming = false, int delayMs = 0)
    {
        lock (_lock) _queue.Add(new Scripted(modelMatch, status, body, streaming, delayMs));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            var data = new JsonArray();
            foreach (var id in CatalogModels)
                data.Add(new JsonObject { ["id"] = id, ["object"] = "model" });
            var listBody = new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(listBody, Encoding.UTF8, "application/json"),
            };
        }

        var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var model = TryReadModel(bodyText);

        Scripted? match;
        lock (_lock)
        {
            Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri?.ToString() ?? "", model, bodyText));

            var idx = _queue.FindIndex(s => s.ModelMatch is null
                || model.Contains(s.ModelMatch, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                match = null;
            }
            else
            {
                match = _queue[idx];
                _queue.RemoveAt(idx);
            }
        }

        if (match is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent(
                    $"{{\"error\":{{\"message\":\"FakeUpstream: no scripted response for model '{model}'\"}}}}",
                    Encoding.UTF8, "application/json"),
            };
        }

        if (match.DelayMs > 0)
            await Task.Delay(match.DelayMs, cancellationToken); // throws if the caller's attempt timeout fires first

        var contentType = match.Streaming ? "text/event-stream" : "application/json";
        return new HttpResponseMessage((HttpStatusCode)match.Status)
        {
            Content = new StringContent(match.Body, Encoding.UTF8, contentType),
        };
    }

    private static string TryReadModel(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try { return (JsonNode.Parse(body) as JsonObject)?["model"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }
}
