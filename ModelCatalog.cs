using System.Text.Json.Nodes;

namespace LlmProxy;

/// <summary>
/// Fetches and caches each provider's /v1/models. Serves a merged OpenAI-shaped list for the
/// /v1/models endpoint, and a filtered+ordered list of chat-capable model ids for dynamic routing.
/// </summary>
public sealed class ModelCatalog
{
    // Non-chat model families excluded from dynamic chat routing (case-insensitive substring on id).
    private static readonly string[] DefaultExcludes =
    {
        "embed", "embedqa", "embedcode", "bge-", "arctic-embed", "rerank", "retriever",
        "reward", "guard", "safety", "content-safety", "topic-control", "moderation",
        "clip", "ocr", "parse", "deplot", "translate", "detector", "calibration",
        "gliner", "pii", "kosmos", "neva", "fuyu", "vila", "nemoretriever",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ProviderRegistry _registry;
    private readonly ILogger<ModelCatalog> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, List<JsonObject> Models)> _byProvider = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalog(IHttpClientFactory httpFactory, ProviderRegistry registry, ILogger<ModelCatalog> logger)
    {
        _httpFactory = httpFactory;
        _registry = registry;
        _logger = logger;
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(Math.Max(0, _registry.Options.ModelsCacheMinutes));

    /// <summary>Merged OpenAI-shaped model list across providers (for GET /v1/models).</summary>
    public async Task<JsonObject> GetModelsAsync(CancellationToken ct)
    {
        var data = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, provider) in _registry.Providers)
        {
            if (provider.HideFromModels) continue;
            try
            {
                foreach (var model in await GetProviderModelsAsync(name, provider, ct))
                {
                    var id = model["id"]?.GetValue<string>();
                    if (id is null || !seen.Add(id)) continue;
                    data.Add(JsonNode.Parse(model.ToJsonString())!.AsObject());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch models from provider {Provider}", name);
            }
        }

        foreach (var aliasId in _registry.Options.ModelAliases.Keys)
            if (seen.Add(aliasId))
                data.Add(new JsonObject { ["id"] = aliasId, ["object"] = "model", ["owned_by"] = "alias" });

        return new JsonObject { ["object"] = "list", ["data"] = data };
    }

    /// <summary>
    /// Chat-capable model ids for a provider, non-chat families filtered out and ordered by the
    /// provider's ModelPrefer patterns (then instruct/chat models, then the rest).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChatCandidatesAsync(string name, ProviderOptions provider, CancellationToken ct)
    {
        IReadOnlyList<string> excludes = provider.ModelExclude.Count > 0 ? provider.ModelExclude : DefaultExcludes;
        var ids = (await GetProviderModelsAsync(name, provider, ct))
            .Select(m => m["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Where(id => !excludes.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return ids
            .OrderBy(id => Rank(id, provider.ModelPrefer))
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Lower rank = tried earlier. Explicit prefer patterns first (in order), then instruct/chat, then rest.
    private static int Rank(string id, IReadOnlyList<string> prefer)
    {
        for (var i = 0; i < prefer.Count; i++)
            if (id.Contains(prefer[i], StringComparison.OrdinalIgnoreCase))
                return i;

        var isChatty = id.Contains("instruct", StringComparison.OrdinalIgnoreCase)
                       || id.Contains("chat", StringComparison.OrdinalIgnoreCase);
        return prefer.Count + (isChatty ? 0 : 1);
    }

    public void Invalidate()
    {
        lock (_byProvider) _byProvider.Clear();
    }

    private async Task<IReadOnlyList<JsonObject>> GetProviderModelsAsync(string name, ProviderOptions provider, CancellationToken ct)
    {
        lock (_byProvider)
            if (_byProvider.TryGetValue(name, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
                return hit.Models;

        await _gate.WaitAsync(ct);
        try
        {
            lock (_byProvider)
                if (_byProvider.TryGetValue(name, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
                    return hit.Models;

            var models = await FetchProviderModelsAsync(name, provider, ct);
            lock (_byProvider) _byProvider[name] = (DateTimeOffset.UtcNow, models);
            return models;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<JsonObject>> FetchProviderModelsAsync(string name, ProviderOptions provider, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("upstream");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{provider.BaseUrl.TrimEnd('/')}/models");
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrEmpty(key))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        var list = node?["data"]?.AsArray();
        var result = new List<JsonObject>(list?.Count ?? 0);
        if (list is null) return result;

        foreach (var item in list)
        {
            if (item is not JsonObject obj) continue;
            var clone = JsonNode.Parse(obj.ToJsonString())!.AsObject();
            clone["owned_by"] ??= name;
            result.Add(clone);
        }
        return result;
    }
}
