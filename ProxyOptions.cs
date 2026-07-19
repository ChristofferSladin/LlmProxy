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

    /// <summary>How long a model is benched after a 200-err/429 before it is a candidate again. Consumed by T1.</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Proxy-owned, model-agnostic instruction appended after the provider's system prompt. Tells the
    /// model it is one of several open models behind a failover proxy, to maintain continuity, and not
    /// to claim to be a specific commercial model. Blank disables. Consumed by T3.
    /// </summary>
    public string IdentityAnchor { get; set; } = "You are one of several open models served via NVIDIA NIM behind a failover proxy. Earlier turns in this conversation may have been answered by a different model — maintain continuity and do not break character. Do not claim to be a specific commercial model (e.g. Claude, GPT); if asked which model you are, say you are an open model routed by a local proxy.";

    /// <summary>Ordered declarative routing rules that bias candidate prefer-ordering by request shape. Empty = today's ordering. Consumed by T4.</summary>
    public List<RoutingRule> RoutingRules { get; set; } = new();

    /// <summary>
    /// Per-application inbound bearer keys, keyed by the (secret) key string itself. Absent/empty ⇒
    /// authentication disabled — today's open local behavior. Inert in T0a; consumed by T1 (auth), T2
    /// (rate limiting) and T6 (startup validation).
    /// </summary>
    public Dictionary<string, InboundKey> InboundKeys { get; set; } = new();

    /// <summary>
    /// Length, in seconds, of one rate-limit window (see <see cref="RateLimitCounter"/>). Defaults
    /// to 60 to match <see cref="InboundKey.RequestsPerMinute"/>'s "per minute" semantics.
    /// Overridable so integration tests can compress the window and burst without a real 60-second
    /// sleep; production should leave this at the default. Consumed by T2.
    /// </summary>
    public int RateLimitWindowSeconds { get; set; } = 60;
}

/// <summary>
/// A single inbound bearer key record: which application it belongs to, which aliases it may request,
/// and its optional per-minute request budget. Two live keys may map to the same <see cref="App"/> to
/// make rotation a deploy rather than an outage. Inert in T0a — no code path reads this dictionary yet.
/// </summary>
public sealed class InboundKey
{
    /// <summary>Attribution name for logs and the rate-limit partition (never the key material itself).</summary>
    public string App { get; set; } = "";

    /// <summary>The only alias names this key may request. A single-alias key may omit "model" on the request.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Per-minute request budget for this key's application. Null = unlimited.</summary>
    public int? RequestsPerMinute { get; set; }
}

/// <summary>
/// Per-alias prompt ownership. Unset (null on <see cref="ModelAlias.PromptMode"/>) resolves to
/// <see cref="Own"/> via <see cref="AliasPolicy.Resolve"/> — today's provider-level behavior.
/// </summary>
public enum PromptMode
{
    /// <summary>Relay the client's messages unmodified: no message removed, none inserted. Consumed by T3.</summary>
    Passthrough,

    /// <summary>Preserve every client message and add exactly one system message carrying the identity anchor. Consumed by T3.</summary>
    Anchor,

    /// <summary>Today's behavior: client system message(s) stripped and replaced by the composed provider prompt + anchor.</summary>
    Own,
}

/// <summary>A single declarative routing rule: when the request matches <see cref="When"/>, bias toward <see cref="Prefer"/>.</summary>
public sealed class RoutingRule
{
    public RoutingWhen When { get; set; } = new();
    public List<string> Prefer { get; set; } = new();
}

/// <summary>Match conditions for a <see cref="RoutingRule"/>. Null/empty fields are ignored (unconstrained).</summary>
public sealed class RoutingWhen
{
    public bool? HasTools { get; set; }
    public int? MinChars { get; set; }
    public List<string> ContentMatches { get; set; } = new();
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

    /// <summary>
    /// Upstream model id substituted into the request body. If null, the client's model id is passed
    /// through. On a STATIC provider (see <see cref="ProviderOptions.DynamicModels"/>) this is wired
    /// today via <see cref="ProviderRegistry"/> as the sole forced candidate (unchanged by T0a). On a
    /// DYNAMIC provider this field is reused as the future pin-first-then-failover candidate (see
    /// <see cref="EffectivePolicy.UpstreamModel"/>) — that seeding behavior is wired in T4, not here.
    /// </summary>
    public string? UpstreamModel { get; set; }

    /// <summary>Per-alias prompt ownership override. Null ⇒ provider-level behavior (today's Own semantics). Consumed by T3.</summary>
    public PromptMode? PromptMode { get; set; }

    /// <summary>Per-alias candidate ordering bias, overriding the provider's ModelPrefer for this alias's requests only. Null ⇒ provider's list. Consumed by T4.</summary>
    public List<string>? ModelPrefer { get; set; }

    /// <summary>Per-alias attempt timeout override in seconds. Null ⇒ the global <see cref="ProxyOptions.AttemptTimeoutSeconds"/>. Consumed by T5.</summary>
    public int? AttemptTimeoutSeconds { get; set; }
}
