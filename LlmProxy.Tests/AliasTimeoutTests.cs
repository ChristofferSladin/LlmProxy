using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// T5: an alias's <see cref="ModelAlias.AttemptTimeoutSeconds"/> overrides the global
/// <see cref="ProxyOptions.AttemptTimeoutSeconds"/> for that request's per-attempt bound only — the
/// blockchain-agent use case (PRD story 16): a slow reasoning model's healthy-but-slow response must
/// not be abandoned and silently failed over to a fast wrong model.
///
/// <see cref="AliasPolicy.Resolve"/>'s resolution behavior is already exhaustively covered by
/// <c>AliasPolicyTests.Alias_attempt_timeout_overrides_global</c> and
/// <c>Full_precedence_chain_alias_beats_provider_beats_global</c> (both pre-existing, from T0a/T4) — the
/// resolution test below is this ticket's own copy of that proof, kept local so the acceptance-check
/// filter (<c>AliasTimeoutTests</c>) is self-contained. The behavioral pair is the new ground: it proves
/// <see cref="ProxyService.ForwardJsonAsync"/> actually reads <c>policy.AttemptTimeoutSeconds</c> at the
/// HTTP layer, not just that <see cref="AliasPolicy.Resolve"/> computes the right number in isolation.
///
/// Uses <see cref="FakeUpstream"/>'s delay support (added by this ticket) to simulate a slow-but-healthy
/// upstream without a real wait. Compressed values: global 1s, alias 3s, upstream delay 2s — the alias
/// request's budget comfortably covers the delay, the non-aliased request's does not. Total wall-clock
/// cost of this file is roughly the sum of one ~2s success and one ~1s timeout-per-candidate failure,
/// well under the ~5s target.
/// </summary>
public sealed class AliasTimeoutTests
{
    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    private static string Request(string model) =>
        "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // --- Pure resolution -----------------------------------------------------------------------

    [Fact]
    public void Alias_attempt_timeout_resolves_to_alias_value_not_global()
    {
        var alias = new ModelAlias { Provider = "nvidia", AttemptTimeoutSeconds = 180 };
        var global = new ProxyOptions { AttemptTimeoutSeconds = 30 };

        var policy = AliasPolicy.Resolve(alias, provider: null, global);

        Assert.Equal(180, policy.AttemptTimeoutSeconds);
    }

    [Fact]
    public void Alias_with_unset_attempt_timeout_falls_back_to_global()
    {
        // AliasPolicy's precedence chain for this field is alias -> global (there is no provider-level
        // AttemptTimeoutSeconds — see ProxyOptions.cs / AliasPolicy.cs remarks: the PRD's compatibility
        // table lists only "alias value, else global default" for this setting).
        var alias = new ModelAlias { Provider = "nvidia" }; // AttemptTimeoutSeconds left unset
        var global = new ProxyOptions { AttemptTimeoutSeconds = 45 };

        var policy = AliasPolicy.Resolve(alias, provider: null, global);

        Assert.Equal(45, policy.AttemptTimeoutSeconds);
    }

    // --- Behavioral proof -----------------------------------------------------------------------

    [Fact]
    public async Task Alias_timeout_lets_a_slow_response_succeed_within_its_larger_budget()
    {
        var host = TestHost.Create(
            forceModels: new[] { "m1" },
            configureOptions: o =>
            {
                o.AttemptTimeoutSeconds = 1; // global: too tight for the 2s delay below
                o.MaxAttemptsPerModel = 1;
                o.ModelAliases["slow-alias"] = new ModelAlias { Provider = "nvidia", AttemptTimeoutSeconds = 3 };
            });

        host.Upstream.Enqueue(modelMatch: "m1", status: 200, body: Completion("m1", "hello via alias"), delayMs: 2000);

        var result = await host.ForwardAsync(Request("slow-alias"));

        Assert.Equal(200, result.Status);
        Assert.Contains("hello via alias", result.Body);
        Assert.Equal(new[] { "m1" }, host.Upstream.TriedModels);
    }

    [Fact]
    public async Task Non_aliased_request_against_the_same_slow_upstream_times_out_and_fails_over()
    {
        var host = TestHost.Create(
            forceModels: new[] { "m1", "m2" },
            configureOptions: o =>
            {
                o.AttemptTimeoutSeconds = 1; // global-only path: no alias to widen the budget
                o.MaxAttemptsPerModel = 1;
            });

        host.Upstream
            .Enqueue(modelMatch: "m1", status: 200, body: Completion("m1", "too slow"), delayMs: 2000)
            .Enqueue(modelMatch: "m2", status: 200, body: Completion("m2", "also too slow"), delayMs: 2000);

        // Not an alias: routes through the default provider on the global 1s timeout.
        var result = await host.ForwardAsync(Request("m1"));

        // Every candidate exceeds the 1s budget: m1 times out, failover tries m2, which also times out,
        // and the request fails entirely (matches FailoverTests' all-candidates-exhausted pattern).
        Assert.Equal(502, result.Status);
        Assert.Equal(new[] { "m1", "m2" }, host.Upstream.TriedModels);
    }
}
