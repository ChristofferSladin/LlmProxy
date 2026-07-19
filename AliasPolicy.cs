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
/// T4: <see cref="EffectivePolicy.UpstreamModel"/> now resolves from <see cref="ModelAlias.UpstreamModel"/>
/// when the alias sets one. On a STATIC provider this field already had a distinct, wired meaning
/// (forced single candidate, via <see cref="ProviderRegistry"/> — untouched by this ticket). On a
/// DYNAMIC provider, <see cref="ProxyService.BuildCandidatesAsync"/> reads this as a pin-first seed:
/// tried first if (and only if) it's already a live candidate today, then normal dynamic failover
/// proceeds — it is never forced in and never a filter, so it can't dead-end a request.
/// </summary>
public static class AliasPolicy
{
    public static EffectivePolicy Resolve(ModelAlias? alias, ProviderOptions? provider, ProxyOptions global)
    {
        var promptMode = alias?.PromptMode ?? LlmProxy.PromptMode.Own;
        var modelPrefer = alias?.ModelPrefer ?? (IReadOnlyList<string>?)provider?.ModelPrefer ?? Array.Empty<string>();
        var attemptTimeoutSeconds = alias?.AttemptTimeoutSeconds ?? global.AttemptTimeoutSeconds;
        var upstreamModel = string.IsNullOrWhiteSpace(alias?.UpstreamModel) ? null : alias.UpstreamModel;

        return new EffectivePolicy(
            PromptMode: promptMode,
            UpstreamModel: upstreamModel,
            ModelPrefer: modelPrefer,
            AttemptTimeoutSeconds: attemptTimeoutSeconds);
    }
}
