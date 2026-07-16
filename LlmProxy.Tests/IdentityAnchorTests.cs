using System.Text.Json.Nodes;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Verifies the proxy-owned identity/continuity anchor is appended to the injected system prompt.
/// Each test inspects the FIRST captured upstream request body's <c>messages[0]</c> system message.
/// The acceptance check filters on <c>IdentityAnchorTests</c>.
/// </summary>
public sealed class IdentityAnchorTests
{
    private const string Base = "You are a helpful assistant.";
    private const string Anchor = "You are an open model routed by a local proxy; do not claim to be Claude or GPT.";

    private static string Completion(string model) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    // A request carrying a client-sent system message, so we can assert it is stripped/replaced.
    private const string RequestWithClientSystem =
        "{\"model\":\"auto\",\"messages\":[" +
        "{\"role\":\"system\",\"content\":\"CLIENT-SENT-SYSTEM\"}," +
        "{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string RequestNoSystem =
        "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    /// <summary>The role+content of messages[0] in the first captured upstream request.</summary>
    private static (string? Role, string? Content) FirstMessage(TestHost host)
    {
        var body = host.Upstream.Requests[0].Body;
        var messages = (JsonNode.Parse(body) as JsonObject)?["messages"] as JsonArray;
        var first = messages?[0] as JsonObject;
        return (first?["role"]?.GetValue<string>(), first?["content"]?.GetValue<string>());
    }

    private static bool HasClientSystem(TestHost host)
    {
        var body = host.Upstream.Requests[0].Body;
        var messages = (JsonNode.Parse(body) as JsonObject)?["messages"] as JsonArray ?? new JsonArray();
        return messages.Any(m => m is JsonObject o
            && o["content"]?.GetValue<string>() == "CLIENT-SENT-SYSTEM");
    }

    [Fact]
    public async Task Anchor_set_with_base_prompt_appends_anchor_after_base()
    {
        var host = TestHost.Create(
            new[] { "deepseek" },
            configure: p => p.SystemPrompt = Base,
            configureOptions: o => o.IdentityAnchor = Anchor);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var (role, content) = FirstMessage(host);
        Assert.Equal("system", role);
        Assert.Equal(Base + "\n\n" + Anchor, content);
        // Base is still present and the anchor comes after it.
        Assert.Contains(Base, content);
        Assert.True(content!.IndexOf(Base, StringComparison.Ordinal) < content.IndexOf(Anchor, StringComparison.Ordinal));
        // The client-sent system message was stripped/replaced.
        Assert.False(HasClientSystem(host));
    }

    [Fact]
    public async Task Anchor_blank_with_base_prompt_injects_base_only()
    {
        var host = TestHost.Create(
            new[] { "deepseek" },
            configure: p => p.SystemPrompt = Base,
            configureOptions: o => o.IdentityAnchor = "");
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var (role, content) = FirstMessage(host);
        Assert.Equal("system", role);
        Assert.Equal(Base, content);
        Assert.False(HasClientSystem(host));
    }

    [Fact]
    public async Task Anchor_set_with_no_base_prompt_injects_anchor_only()
    {
        var host = TestHost.Create(
            new[] { "deepseek" },
            configure: p => p.SystemPrompt = null,
            configureOptions: o => o.IdentityAnchor = Anchor);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var (role, content) = FirstMessage(host);
        Assert.Equal("system", role);
        Assert.Equal(Anchor, content);
        Assert.False(HasClientSystem(host));
    }

    [Fact]
    public async Task Both_blank_injects_no_system_message()
    {
        var host = TestHost.Create(
            new[] { "deepseek" },
            configure: p => p.SystemPrompt = null,
            configureOptions: o => o.IdentityAnchor = "");
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestNoSystem);
        Assert.Equal(200, result.Status);

        // No system message injected: messages[0] is the original user message.
        var (role, _) = FirstMessage(host);
        Assert.Equal("user", role);
    }
}
