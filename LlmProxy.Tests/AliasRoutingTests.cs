using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// T4: alias model targeting on a DYNAMIC provider. An alias's <see cref="ModelAlias.UpstreamModel"/> is
/// tried FIRST when it's a live candidate (pin-first), but failure still fails over into the rest of the
/// dynamic candidates (never dead-ends) — the pin is a seed at the front of the candidate list, never a
/// filter. An alias's <see cref="ModelAlias.ModelPrefer"/> reorders candidates for that alias's requests
/// only; non-aliased requests keep the provider's own dynamic ordering byte-identical.
///
/// Un-inerts the shape of the existing "fast" alias in appsettings.json
/// (<c>{"Provider":"nvidia","UpstreamModel":"meta/llama-3.1-8b-instruct"}</c> on the DYNAMIC nvidia
/// provider) — the pin test below is a test-local equivalent of that exact alias/provider shape.
///
/// Uses <see cref="TestHost.Create"/>'s <c>dynamicCatalog</c> seam (added by this ticket) to fake a
/// DYNAMIC provider's live /v1/models catalog, since <c>ForceModels</c> (used by the other test files)
/// bypasses the candidate-seeding code path this ticket wires. Acceptance check filters on
/// <c>AliasRoutingTests</c>.
/// </summary>
public sealed class AliasRoutingTests
{
    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    private const string ExhaustedBody =
        "{\"error\":{\"message\":\"ResourceExhausted: Worker local total request limit reached (48/48)\"}}";

    private static string AliasRequest(string alias) =>
        "{\"model\":\"" + alias + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // [Fact 1] Pin test: alias with UpstreamModel set, on a dynamic provider whose live catalog
    // includes that exact id -> it is attempted FIRST (this is the "fast" alias's real shape:
    // {"Provider":"nvidia","UpstreamModel":"meta/llama-3.1-8b-instruct"} against a DynamicModels
    // provider, un-inerted).
    [Fact]
    public async Task Alias_pin_is_tried_first_when_it_is_a_live_dynamic_candidate()
    {
        var host = TestHost.Create(
            dynamicCatalog: new[] { "meta/llama-3.1-8b-instruct", "other-model" },
            configureOptions: o =>
            {
                o.MaxAttemptsPerModel = 1;
                o.ModelAliases["fast"] = new ModelAlias { Provider = "nvidia", UpstreamModel = "meta/llama-3.1-8b-instruct" };
            });

        host.Upstream.Enqueue(modelMatch: "meta/llama-3.1-8b-instruct", status: 200,
            body: Completion("meta/llama-3.1-8b-instruct", "hello from the pin"));

        var r = await host.ForwardAsync(AliasRequest("fast"));

        Assert.Equal(200, r.Status);
        Assert.Contains("hello from the pin", r.Body);
        Assert.Equal(new[] { "meta/llama-3.1-8b-instruct" }, host.Upstream.TriedModels);
    }

    // [Fact 2] Pin-fails-then-failover: the pinned model errors out -> failover proceeds into the rest
    // of the dynamic candidates and the request still succeeds. Never dead-ends.
    [Fact]
    public async Task Alias_pin_failure_fails_over_into_remaining_dynamic_candidates()
    {
        var host = TestHost.Create(
            dynamicCatalog: new[] { "meta/llama-3.1-8b-instruct", "other-model" },
            configureOptions: o =>
            {
                o.MaxAttemptsPerModel = 1;
                o.ModelAliases["fast"] = new ModelAlias { Provider = "nvidia", UpstreamModel = "meta/llama-3.1-8b-instruct" };
            });

        host.Upstream
            .Enqueue(modelMatch: "meta/llama-3.1-8b-instruct", status: 500, body: ExhaustedBody)
            .Enqueue(modelMatch: "other-model", status: 200, body: Completion("other-model", "hello from failover"));

        var r = await host.ForwardAsync(AliasRequest("fast"));

        Assert.Equal(200, r.Status);
        Assert.Contains("hello from failover", r.Body);
        // Pin tried first, then failover proceeded into the next dynamic candidate — never dead-ended.
        Assert.Equal(new[] { "meta/llama-3.1-8b-instruct", "other-model" }, host.Upstream.TriedModels);
    }

    // [Fact 3] A pin absent from the live catalog is never forced in (would waste an attempt on every
    // request against a stale/renamed model) — ordering falls back to the normal dynamic ranking.
    [Fact]
    public async Task Alias_pin_absent_from_live_catalog_is_not_forced_in()
    {
        var host = TestHost.Create(
            dynamicCatalog: new[] { "m-a", "m-b" },
            configureOptions: o =>
            {
                o.MaxAttemptsPerModel = 1;
                o.ModelAliases["fast"] = new ModelAlias { Provider = "nvidia", UpstreamModel = "renamed-or-missing-model" };
            });

        host.Upstream.Enqueue(modelMatch: "m-a", status: 200, body: Completion("m-a", "default order"));

        var r = await host.ForwardAsync(AliasRequest("fast"));

        Assert.Equal(200, r.Status);
        Assert.DoesNotContain("renamed-or-missing-model", host.Upstream.TriedModels);
        Assert.Equal(new[] { "m-a" }, host.Upstream.TriedModels);
    }

    // [Fact 4] Prefer test: alias ModelPrefer reorders candidates for THAT alias's requests only. A
    // non-aliased request against the same dynamic provider keeps the provider's own ordering unchanged
    // (byte-identical): m-a first (alphabetical baseline — no provider-level ModelPrefer configured).
    // Run the non-aliased request FIRST so the assertion isn't confounded by last-good stickiness (a
    // separate, pre-existing mechanism keyed per-provider) — the alias's ModelPrefer still outranks
    // last-good per BuildCandidatesAsync's documented order, proven by the second half of this test.
    [Fact]
    public async Task Alias_model_prefer_reorders_only_that_aliass_requests()
    {
        var host = TestHost.Create(
            dynamicCatalog: new[] { "m-a-instruct", "m-b-instruct", "m-c-instruct" },
            configureOptions: o =>
            {
                o.MaxAttemptsPerModel = 1;
                o.ModelAliases["quality"] = new ModelAlias
                {
                    Provider = "nvidia",
                    ModelPrefer = new List<string> { "m-c-instruct" },
                };
            });

        // Non-aliased request first: default baseline ordering (alphabetical) untouched by any alias.
        host.Upstream.Enqueue(modelMatch: "m-a-instruct", status: 200, body: Completion("m-a-instruct", "default order"));
        var r1 = await host.ForwardAsync("{\"model\":\"m-a-instruct\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        Assert.Equal(200, r1.Status);
        Assert.Equal("m-a-instruct", host.Upstream.TriedModels[0]);

        // Aliased request second: alias ModelPrefer promotes m-c-instruct to the front, outranking both
        // the alphabetical baseline AND the last-good stickiness the first request just set (m-a-instruct).
        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "m-c-instruct", status: 200, body: Completion("m-c-instruct", "preferred"));
        var r2 = await host.ForwardAsync(AliasRequest("quality"));
        Assert.Equal(200, r2.Status);
        Assert.Equal("m-c-instruct", host.Upstream.TriedModels[callsBefore]);
    }
}
