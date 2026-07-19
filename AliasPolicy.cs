namespace LlmProxy;

/// <summary>
/// One resolved policy for a request: prompt mode, pinned upstream model, candidate-ordering
/// preference, and per-attempt timeout. The forwarding path consumes this instead of reading
/// alias/provider/global options directly at each decision point.
/// </summary>
public sealed record EffectivePolicy(
    PromptMode PromptMode,
    string? UpstreamModel,
    IReadOnlyList<string> ModelPrefer,
    int AttemptTimeoutSeconds);

/// <summary>
/// Resolves the alias → provider → global precedence chain into one <see cref="EffectivePolicy"/> so no
/// call site re-implements the fallback. Pure/static, like <see cref="RoutingRuleSet"/> — constructed
/// inline, no DI registration.
///
/// T0a note: <see cref="EffectivePolicy.UpstreamModel"/> always resolves to <c>null</c> here regardless
/// of <see cref="ModelAlias.UpstreamModel"/>. That field already has a distinct, wired meaning for
/// STATIC providers (forced single candidate, via <see cref="ProviderRegistry"/> — untouched by this
/// ticket); repurposing it as a dynamic-provider pin-first-then-failover seed is T4's job. Until T4
/// wires that candidate-seeding behavior, resolving a non-null pin here would be a seam with no
/// consumer and risks silently changing routing the moment something reads it — so it is hardcoded
/// absent, and a dedicated test in AliasPolicyTests pins the decision.
/// </summary>
public static class AliasPolicy
{
    public static EffectivePolicy Resolve(ModelAlias? alias, ProviderOptions? provider, ProxyOptions global)
    {
        var promptMode = alias?.PromptMode ?? LlmProxy.PromptMode.Own;
        var modelPrefer = alias?.ModelPrefer ?? (IReadOnlyList<string>?)provider?.ModelPrefer ?? Array.Empty<string>();
        var attemptTimeoutSeconds = alias?.AttemptTimeoutSeconds ?? global.AttemptTimeoutSeconds;

        return new EffectivePolicy(
            PromptMode: promptMode,
            UpstreamModel: null, // T4 seam — see class remarks.
            ModelPrefer: modelPrefer,
            AttemptTimeoutSeconds: attemptTimeoutSeconds);
    }
}
