namespace LlmProxy;

/// <summary>
/// Root config bound from the "Proxy" section of appsettings.json.
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>Provider whose catalog/endpoints are used when a requested model isn't in <see cref="ModelAliases"/>.</summary>
    public string DefaultProvider { get; set; } = "nvidia";

    /// <summary>How long a provider's /v1/models response is cached.</summary>
    public int ModelsCacheMinutes { get; set; } = 30;

    /// <summary>Log each request line (method, model, provider, status, elapsed).</summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>
    /// Max time to wait for an upstream response before giving up on that attempt. For streaming
    /// requests this bounds time-to-first-response, so a hung model fails over quickly.
    /// </summary>
    public int AttemptTimeoutSeconds { get; set; } = 30;

    /// <summary>Attempts per candidate model for retryable failures (5xx/429/network) before moving to the next model.</summary>
    public int MaxAttemptsPerModel { get; set; } = 2;

    /// <summary>Cap on how many dynamic candidates to try per request (prevents looping a whole catalog).</summary>
    public int MaxDynamicCandidates { get; set; } = 10;

    /// <summary>Prepend a line naming the answering model to each chat response (so LM Studio shows which model replied).</summary>
    public bool AnnounceModel { get; set; }

    /// <summary>Format of the announce line; "{model}" is substituted. Markdown is rendered by LM Studio.</summary>
    public string ModelAnnounceFormat { get; set; } = "_[{model}]_\n\n";

    /// <summary>Configured upstream providers, keyed by the name used in routing.</summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional client-facing model id -> routing target. Lets you expose a stable name
    /// (e.g. "fast") that maps to a specific provider + upstream model id.
    /// </summary>
    public Dictionary<string, ModelAlias> ModelAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderOptions
{
    /// <summary>OpenAI-compatible base URL, including the /v1 segment. e.g. https://integrate.api.nvidia.com/v1</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Literal API key. Prefer <see cref="ApiKeyEnv"/> so secrets stay out of config files.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of an environment variable holding the API key. Takes precedence over <see cref="ApiKey"/> when set and present.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>When true, this provider's models are excluded from the merged /v1/models list.</summary>
    public bool HideFromModels { get; set; }

    /// <summary>
    /// When set, every request routed to this provider is forced to this upstream model id,
    /// regardless of what the client (LM Studio) sent. Lets the proxy own the model choice.
    /// Takes precedence over model aliases and the client's model field.
    /// Ignored when <see cref="ForceModels"/> is non-empty.
    /// </summary>
    public string? ForceModel { get; set; }

    /// <summary>
    /// Ordered fallback chain of upstream chat models. The proxy tries each in order until one
    /// responds, then streams it. Takes precedence over <see cref="ForceModel"/> and aliases.
    /// Use known chat models only — not embedding/vision/guard/reward models.
    /// </summary>
    public List<string> ForceModels { get; set; } = new();

    /// <summary>
    /// When true (and no <see cref="ForceModels"/>), candidate models are pulled live from the
    /// provider's /v1/models, filtered to chat-capable, and tried until one responds. No static list.
    /// </summary>
    public bool DynamicModels { get; set; }

    /// <summary>Soft ordering bias for dynamic mode: ids matching these substrings are tried first, in order. Never dead-ends.</summary>
    public List<string> ModelPrefer { get; set; } = new();

    /// <summary>Override the built-in non-chat exclude keywords for dynamic mode. Empty = use built-in defaults.</summary>
    public List<string> ModelExclude { get; set; } = new();

    /// <summary>
    /// When set, the proxy owns the system prompt for chat requests: it strips any system
    /// message the client sent and injects this one as the first message. A true "global"
    /// system prompt, independent of the client (LM Studio) UI.
    /// For long prompts prefer <see cref="SystemPromptFile"/>.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Path to a file (e.g. Markdown) whose contents become the system prompt. Resolved at
    /// startup relative to the content root. Takes precedence over <see cref="SystemPrompt"/>.
    /// </summary>
    public string? SystemPromptFile { get; set; }

    public string? ResolveApiKey()
    {
        if (!string.IsNullOrEmpty(ApiKeyEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnv);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        }
        return ApiKey;
    }
}

public sealed class ModelAlias
{
    /// <summary>Provider key to route to.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Upstream model id substituted into the request body. If null, the client's model id is passed through.</summary>
    public string? UpstreamModel { get; set; }
}
