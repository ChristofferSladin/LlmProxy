using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Verifies <see cref="AliasPolicy.Resolve"/>'s alias -> provider -> global precedence chain, and that
/// every field resolves to today's default behavior when unset. This is the seam T0a introduces: the
/// forwarding path will read timeout, prompt mode, and (later) pin/prefer from the returned
/// <see cref="EffectivePolicy"/> instead of reading globals directly.
/// </summary>
public sealed class AliasPolicyTests
{
    private static ProxyOptions Global(int attemptTimeoutSeconds = 30) =>
        new() { AttemptTimeoutSeconds = attemptTimeoutSeconds };

    [Fact]
    public void No_alias_no_provider_resolves_to_today_defaults()
    {
        var policy = AliasPolicy.Resolve(alias: null, provider: null, Global());

        Assert.Equal(PromptMode.Own, policy.PromptMode);
        Assert.Null(policy.UpstreamModel);
        Assert.Empty(policy.ModelPrefer);
        Assert.Equal(30, policy.AttemptTimeoutSeconds);
    }

    [Fact]
    public void Fully_unset_alias_resolves_to_provider_and_global_defaults()
    {
        var provider = new ProviderOptions { ModelPrefer = { "deepseek", "llama" } };
        var alias = new ModelAlias { Provider = "nvidia" }; // every new field left unset

        var policy = AliasPolicy.Resolve(alias, provider, Global(attemptTimeoutSeconds: 45));

        Assert.Equal(PromptMode.Own, policy.PromptMode);
        Assert.Null(policy.UpstreamModel);
        Assert.Equal(new[] { "deepseek", "llama" }, policy.ModelPrefer);
        Assert.Equal(45, policy.AttemptTimeoutSeconds);
    }

    [Fact]
    public void Alias_prompt_mode_overrides_default()
    {
        var alias = new ModelAlias { Provider = "nvidia", PromptMode = PromptMode.Passthrough };

        var policy = AliasPolicy.Resolve(alias, provider: null, Global());

        Assert.Equal(PromptMode.Passthrough, policy.PromptMode);
    }

    [Fact]
    public void Alias_model_prefer_overrides_provider_model_prefer()
    {
        var provider = new ProviderOptions { ModelPrefer = { "deepseek" } };
        var alias = new ModelAlias { Provider = "nvidia", ModelPrefer = new List<string> { "kimi", "nemotron" } };

        var policy = AliasPolicy.Resolve(alias, provider, Global());

        Assert.Equal(new[] { "kimi", "nemotron" }, policy.ModelPrefer);
    }

    [Fact]
    public void Alias_attempt_timeout_overrides_global()
    {
        var alias = new ModelAlias { Provider = "nvidia", AttemptTimeoutSeconds = 180 };

        var policy = AliasPolicy.Resolve(alias, provider: null, Global(attemptTimeoutSeconds: 30));

        Assert.Equal(180, policy.AttemptTimeoutSeconds);
    }

    /// <summary>
    /// T4: lifts the T0a restriction. <see cref="ModelAlias.UpstreamModel"/> already has a distinct,
    /// wired meaning for STATIC providers (forced single-candidate, via <see cref="ProviderRegistry"/> —
    /// unchanged by this ticket); on a DYNAMIC provider it is now resolved here as the pin-first seed
    /// that <see cref="ProxyService.BuildCandidatesAsync"/> reads. This replaces
    /// <c>Alias_upstream_model_pin_stays_absent_in_T0a_regardless_of_alias_value</c>, which pinned down
    /// T0a's deliberately-temporary "always null" restriction — T4 is explicitly tasked with lifting it,
    /// so the old assertion (null regardless of alias value) is now the wrong behavior to guard.
    /// </summary>
    [Fact]
    public void Alias_upstream_model_pin_resolves_from_alias_value()
    {
        var alias = new ModelAlias { Provider = "nvidia", UpstreamModel = "moonshotai/kimi-k2.6" };

        var policy = AliasPolicy.Resolve(alias, provider: null, Global());

        Assert.Equal("moonshotai/kimi-k2.6", policy.UpstreamModel);
    }

    [Fact]
    public void No_alias_resolves_upstream_model_to_null()
    {
        var policy = AliasPolicy.Resolve(alias: null, provider: null, Global());

        Assert.Null(policy.UpstreamModel);
    }

    [Fact]
    public void Alias_with_unset_upstream_model_resolves_to_null()
    {
        var alias = new ModelAlias { Provider = "nvidia" };

        var policy = AliasPolicy.Resolve(alias, provider: null, Global());

        Assert.Null(policy.UpstreamModel);
    }

    [Fact]
    public void Full_precedence_chain_alias_beats_provider_beats_global()
    {
        var provider = new ProviderOptions { ModelPrefer = { "provider-pref" } };
        var alias = new ModelAlias
        {
            Provider = "nvidia",
            PromptMode = PromptMode.Anchor,
            ModelPrefer = new List<string> { "alias-pref" },
            AttemptTimeoutSeconds = 180,
            UpstreamModel = "pinned-model", // T4: now resolves — see dedicated pin test above
        };

        var policy = AliasPolicy.Resolve(alias, provider, Global(attemptTimeoutSeconds: 30));

        Assert.Equal(PromptMode.Anchor, policy.PromptMode);
        Assert.Equal(new[] { "alias-pref" }, policy.ModelPrefer);
        Assert.Equal(180, policy.AttemptTimeoutSeconds);
        Assert.Equal("pinned-model", policy.UpstreamModel);
    }
}
