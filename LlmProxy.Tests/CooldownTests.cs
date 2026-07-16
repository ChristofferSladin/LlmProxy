using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Cooldown registry (T1): a model that returns a 200-err body or HTTP 429 is benched for
/// <c>CooldownSeconds</c>; candidate building skips benched models so failover doesn't walk back
/// into the same wall — but never dead-ends: if everything is benched, cooldowns are ignored and the
/// full ordered list is tried anyway. Acceptance check filters on <c>CooldownTests</c>.
/// </summary>
public sealed class CooldownTests
{
    private const string ExhaustedBody =
        "{\"error\":{\"message\":\"ResourceExhausted: Worker local total request limit reached (48/48)\"}}";

    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    private const string Request =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // Comfortably long so the bench never expires mid-test; one attempt per model for a clean chain.
    private static TestHost NewHost() =>
        TestHost.Create(new[] { "A", "B" }, configureOptions: o =>
        {
            o.MaxAttemptsPerModel = 1;
            o.CooldownSeconds = 300;
        });

    [Fact]
    public async Task Model_that_returns_200_error_body_is_benched_and_skipped_next_request()
    {
        var host = NewHost();
        host.Upstream
            .Enqueue(modelMatch: "A", status: 200, body: ExhaustedBody)
            .Enqueue(modelMatch: "B", status: 200, body: Completion("B", "hello from B"));

        // Request 1: A body-errors, B answers — and A gets benched.
        var r1 = await host.ForwardAsync(Request);
        Assert.Equal(200, r1.Status);
        Assert.Contains("hello from B", r1.Body);
        Assert.Equal(new[] { "A", "B" }, host.Upstream.TriedModels);

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "second from B"));

        // Request 2: A is still cooling down, so it is skipped entirely — B answers directly.
        var r2 = await host.ForwardAsync(Request);
        Assert.Equal(200, r2.Status);
        Assert.Contains("second from B", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.DoesNotContain("A", request2Models);
        Assert.Equal(new[] { "B" }, request2Models);
    }

    [Fact]
    public async Task Model_that_returns_429_is_benched_and_skipped_next_request()
    {
        var host = NewHost();
        host.Upstream
            .Enqueue(modelMatch: "A", status: 429, body: "{\"error\":{\"message\":\"Too Many Requests\"}}")
            .Enqueue(modelMatch: "B", status: 200, body: Completion("B", "hello from B"));

        var r1 = await host.ForwardAsync(Request);
        Assert.Equal(200, r1.Status);
        Assert.Contains("hello from B", r1.Body);
        Assert.Equal(new[] { "A", "B" }, host.Upstream.TriedModels);

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "second from B"));

        var r2 = await host.ForwardAsync(Request);
        Assert.Equal(200, r2.Status);
        Assert.Contains("second from B", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.DoesNotContain("A", request2Models);
        Assert.Equal(new[] { "B" }, request2Models);
    }

    [Fact]
    public async Task All_candidates_cooled_down_still_attempts_and_answers()
    {
        var host = NewHost();
        // Request 1: both A and B body-error → both benched, request fails (502).
        host.Upstream
            .Enqueue(modelMatch: "A", status: 200, body: ExhaustedBody)
            .Enqueue(modelMatch: "B", status: 200, body: ExhaustedBody);

        var r1 = await host.ForwardAsync(Request);
        Assert.Equal(502, r1.Status);
        Assert.Equal(new[] { "A", "B" }, host.Upstream.TriedModels);

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "recovered A"));

        // Request 2: every candidate is cooling down, but the proxy must ignore cooldowns and STILL try.
        var r2 = await host.ForwardAsync(Request);
        Assert.Equal(200, r2.Status);
        Assert.Contains("recovered A", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.NotEmpty(request2Models);
        Assert.Contains("A", request2Models);
    }
}
