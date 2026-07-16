using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Learned tool-capability map (T2): when a request carrying <c>tools</c> draws an explicit
/// tool/function-calling error from a model, that model is remembered as tool-incapable and future
/// <c>tools</c> requests skip it. Unknown = optimistically capable; a model merely answering a tools
/// request in prose (no error) is never demoted. The hard filter never dead-ends. Acceptance check
/// filters on <c>CapabilityTests</c>.
///
/// Demotion-path choice: the tool error is delivered as an HTTP **400** whose body mentions tools.
/// 400 is NOT a cooldown trigger (only 200-err / 429 are), so these tests isolate capability behavior
/// from cooldown behavior. Per current control flow a 400 ("other 4xx") surfaces to the client and the
/// request ends without failover — so request 1 demotes-and-ends, and a SUBSEQUENT tools request proves
/// A is now skipped. That matches the real flow and keeps the two learned signals independent.
/// </summary>
public sealed class CapabilityTests
{
    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    // A 400 rejection whose body references tools — the demotion signal. 400 does not bench (cooldown).
    private const string ToolRejectBody =
        "{\"error\":{\"message\":\"This model does not support tools.\"}}";

    // Request WITH a minimal function tool.
    private const string ToolsRequest =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," +
        "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"parameters\":{}}}]}";

    // Request WITHOUT tools.
    private const string PlainRequest =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // Single-letter, non-overlapping models (FakeUpstream substring match can't cross-match); long
    // cooldown + one attempt per model keep the attempt chain clean and deterministic.
    private static TestHost NewHost() =>
        TestHost.Create(new[] { "A", "B" }, configureOptions: o =>
        {
            o.MaxAttemptsPerModel = 1;
            o.CooldownSeconds = 300;
        });

    // (a) tools request → A tool-errors → A demoted → a subsequent tools request sends ZERO calls to A.
    [Fact]
    public async Task Tool_error_demotes_model_and_subsequent_tools_request_skips_it()
    {
        var host = NewHost();
        host.Upstream.Enqueue(modelMatch: "A", status: 400, body: ToolRejectBody);

        // Request 1: A rejects the tools request (400 surfaces, request ends) — but A is now demoted.
        var r1 = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(400, r1.Status);
        Assert.Equal(new[] { "A" }, host.Upstream.TriedModels);
        Assert.False(host.Routing.IsToolCapable("A"));

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "hello from B"));

        // Request 2 (tools): A is filtered out — B answers with zero calls to A.
        var r2 = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r2.Status);
        Assert.Contains("hello from B", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.DoesNotContain("A", request2Models);
        Assert.Equal(new[] { "B" }, request2Models);
    }

    // (b) after (a), a NON-tools request may still route to A (not filtered when hasTools is false).
    [Fact]
    public async Task Demoted_model_still_used_for_non_tools_request()
    {
        var host = NewHost();
        host.Upstream.Enqueue(modelMatch: "A", status: 400, body: ToolRejectBody);

        var r1 = await host.ForwardAsync(ToolsRequest); // demote A
        Assert.Equal(400, r1.Status);
        Assert.False(host.Routing.IsToolCapable("A"));

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "plain from A"));

        // A non-tools request must still try A first (capability filter only applies to tools requests).
        var r2 = await host.ForwardAsync(PlainRequest);
        Assert.Equal(200, r2.Status);
        Assert.Contains("plain from A", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.Equal("A", request2Models[0]);
    }

    // (c) a good completion (prose, no tool_calls, no error) on a tools request must NOT demote.
    [Fact]
    public async Task Good_completion_on_tools_request_does_not_demote()
    {
        var host = NewHost();
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "prose from A"));

        var r1 = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r1.Status);
        Assert.Contains("prose from A", r1.Body);
        Assert.True(host.Routing.IsToolCapable("A")); // silence never demotes

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "again from A"));

        // A subsequent tools request still tries A first.
        var r2 = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r2.Status);
        Assert.Contains("again from A", r2.Body);

        var request2Models = host.Upstream.TriedModels.Skip(callsBefore).ToList();
        Assert.Equal("A", request2Models[0]);
    }

    // (d) all candidates demoted incapable + a tools request → still attempts (never dead-end).
    [Fact]
    public async Task All_incapable_tools_request_still_attempts()
    {
        var host = NewHost();
        host.Routing.MarkToolIncapable("A");
        host.Routing.MarkToolIncapable("B");

        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "fallback from A"));

        // Every candidate is known-incapable, but the filter must not empty the pool: a request is
        // still attempted against the full ordered list.
        var r = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r.Status);
        Assert.Contains("fallback from A", r.Body);
        Assert.NotEmpty(host.Upstream.TriedModels);
        Assert.Contains("A", host.Upstream.TriedModels);
    }
}
