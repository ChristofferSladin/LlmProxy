using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Declarative heuristic router (T4): an ordered <see cref="ProxyOptions.RoutingRules"/> set biases
/// candidate prefer-ordering by request shape (<c>hasTools</c> / <c>minChars</c> / <c>contentMatches</c>).
/// SOFT reorder only — it never excludes a candidate (that is the hard tool/cooldown filters' job) and
/// never multiplies upstream calls. Empty <c>RoutingRules</c> reproduces today's ordering exactly.
///
/// Each test pins the candidate chain to <c>["A","B"]</c> via <c>ForceModels</c>, so the DEFAULT order
/// tries A first. A rule promoting <c>B</c> must make B the FIRST captured upstream request. Single-letter,
/// non-overlapping ids keep FakeUpstream's substring matching from cross-matching. Acceptance check filters
/// on <c>RoutingRulesTests</c>.
/// </summary>
public sealed class RoutingRulesTests
{
    private static string Completion(string model, string content) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}";

    // A tools request (non-empty tools array) with a short prompt.
    private const string ToolsRequest =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," +
        "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"parameters\":{}}}]}";

    // A plain request (no tools) with a short prompt.
    private const string PlainRequest =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    // A request whose user content is `chars` characters long (drives the minChars rule).
    private static string SizedRequest(int chars) =>
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', chars) + "\"}]}";

    // A code-ish request whose content contains a fenced code block (drives the contentMatches rule).
    private const string CodeRequest =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"fix this:\\n```\\ndef f(): pass\\n```\"}]}";

    // Chain pinned to [A, B]; long cooldown + single attempt keep the attempt chain clean/deterministic.
    private static TestHost NewHost(Action<ProxyOptions>? rules = null) =>
        TestHost.Create(new[] { "A", "B" }, configureOptions: o =>
        {
            o.MaxAttemptsPerModel = 1;
            o.CooldownSeconds = 300;
            rules?.Invoke(o);
        });

    // (a) hasTools rule promotes B → a tools request tries B first; a non-tools request is unaffected (A first).
    [Fact]
    public async Task HasTools_rule_promotes_model_for_tools_request_only()
    {
        var host = NewHost(o => o.RoutingRules.Add(new RoutingRule
        {
            When = new RoutingWhen { HasTools = true },
            Prefer = new List<string> { "B" },
        }));

        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "tools by B"));
        var r1 = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r1.Status);
        Assert.Equal("B", host.Upstream.TriedModels[0]); // rule fired: B first

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "plain by A"));
        var r2 = await host.ForwardAsync(PlainRequest);
        Assert.Equal(200, r2.Status);
        Assert.Equal("A", host.Upstream.TriedModels[callsBefore]); // no rule match → default A first
    }

    // (b) minChars rule promotes B → a large prompt tries B first; a small prompt does not (A first).
    [Fact]
    public async Task MinChars_rule_promotes_model_for_large_prompt_only()
    {
        var host = NewHost(o => o.RoutingRules.Add(new RoutingRule
        {
            When = new RoutingWhen { MinChars = 100 },
            Prefer = new List<string> { "B" },
        }));

        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "big by B"));
        var r1 = await host.ForwardAsync(SizedRequest(200)); // > 100 chars → rule fires
        Assert.Equal(200, r1.Status);
        Assert.Equal("B", host.Upstream.TriedModels[0]);

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "small by A"));
        var r2 = await host.ForwardAsync(SizedRequest(10)); // < 100 chars → no match → A first
        Assert.Equal(200, r2.Status);
        Assert.Equal("A", host.Upstream.TriedModels[callsBefore]);
    }

    // (c) contentMatches rule promotes B → a code-ish prompt tries B first; a plain prompt does not.
    [Fact]
    public async Task ContentMatches_rule_promotes_model_for_code_prompt_only()
    {
        var host = NewHost(o => o.RoutingRules.Add(new RoutingRule
        {
            When = new RoutingWhen { ContentMatches = new List<string> { "```" } },
            Prefer = new List<string> { "B" },
        }));

        host.Upstream.Enqueue(modelMatch: "B", status: 200, body: Completion("B", "code by B"));
        var r1 = await host.ForwardAsync(CodeRequest); // contains ``` → rule fires
        Assert.Equal(200, r1.Status);
        Assert.Equal("B", host.Upstream.TriedModels[0]);

        var callsBefore = host.Upstream.Requests.Count;
        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "plain by A"));
        var r2 = await host.ForwardAsync(PlainRequest); // no ``` → no match → A first
        Assert.Equal(200, r2.Status);
        Assert.Equal("A", host.Upstream.TriedModels[callsBefore]);
    }

    // (d) EMPTY RoutingRules → ordering identical to today: A (the default chain head) is tried first.
    [Fact]
    public async Task Empty_routing_rules_reproduces_default_ordering()
    {
        var host = NewHost(); // no rules
        Assert.Empty(host.Options.RoutingRules);

        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "default A"));
        var r = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r.Status);
        Assert.Equal("A", host.Upstream.TriedModels[0]); // unbiased: A first, exactly as before
    }

    // (compose) a rule promoting B, but B is tool-incapable and the request hasTools → the hard filter
    // still excludes B (filter wins over soft promote); A answers and B is never tried.
    [Fact]
    public async Task Hard_tool_filter_wins_over_soft_promote()
    {
        var host = NewHost(o => o.RoutingRules.Add(new RoutingRule
        {
            When = new RoutingWhen { HasTools = true },
            Prefer = new List<string> { "B" }, // rule wants to promote B...
        }));
        host.Routing.MarkToolIncapable("B"); // ...but B can't do tools.

        host.Upstream.Enqueue(modelMatch: "A", status: 200, body: Completion("A", "A did tools"));
        var r = await host.ForwardAsync(ToolsRequest);
        Assert.Equal(200, r.Status);
        Assert.Contains("A did tools", r.Body);
        Assert.Equal("A", host.Upstream.TriedModels[0]);
        Assert.DoesNotContain("B", host.Upstream.TriedModels); // hard filter excluded B despite the promote
    }
}
