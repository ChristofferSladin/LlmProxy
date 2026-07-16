using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Deterministic proof of the cooldown *skip* in isolation (T5, a QA follow-up). <see cref="CooldownTests"/>
/// benches via the real 200-err/429 trigger and then asserts the skip end-to-end; here we pre-bench a model
/// directly through the public <see cref="TestHost.Routing"/> and assert the candidate order omits it — the
/// same thing the live smoke test could not reproduce because NVIDIA exhaustion was intermittent.
/// Acceptance check filters on <c>CooldownSkipTests</c>.
/// </summary>
public sealed class CooldownSkipTests
{
    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    private const string Request =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private static TestHost NewHost() =>
        TestHost.Create(new[] { "A", "B" }, configureOptions: o =>
        {
            o.MaxAttemptsPerModel = 1;
            o.CooldownSeconds = 300;
        });

    [Fact]
    public async Task Pre_benched_model_is_omitted_from_candidate_order()
    {
        var host = NewHost();
        // Bench A directly — no trigger round-trip. A is first in the chain, so if it weren't skipped
        // it would be tried first.
        host.Routing.RegisterCooldown("A", TimeSpan.FromMinutes(5));
        host.Upstream
            .Enqueue(modelMatch: "A", status: 200, body: Completion("A", "should never be reached"))
            .Enqueue(modelMatch: "B", status: 200, body: Completion("B", "hello from B"));

        var r = await host.ForwardAsync(Request);

        Assert.Equal(200, r.Status);
        Assert.Contains("hello from B", r.Body);
        // The decisive assertion: A was never contacted; only B was tried.
        Assert.DoesNotContain("A", host.Upstream.TriedModels);
        Assert.Equal(new[] { "B" }, host.Upstream.TriedModels);
    }

    [Fact]
    public async Task All_candidates_benched_still_attempts_never_dead_ends()
    {
        var host = NewHost();
        // Bench BOTH — the never-dead-end fallback must ignore cooldowns and try the full order (A first).
        host.Routing.RegisterCooldown("A", TimeSpan.FromMinutes(5));
        host.Routing.RegisterCooldown("B", TimeSpan.FromMinutes(5));
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "hello from A"));

        var r = await host.ForwardAsync(Request);

        Assert.Equal(200, r.Status);
        Assert.Contains("hello from A", r.Body);
        Assert.Equal(new[] { "A" }, host.Upstream.TriedModels); // full order restored, A tried first
    }
}
