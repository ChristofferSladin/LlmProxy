using System.Text.Json.Nodes;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Verifies the per-alias <see cref="PromptMode"/> seam (T3): Passthrough is a true no-op,
/// Anchor preserves every client message and inserts exactly one anchor system message, and
/// unset alias mode falls back to Own (today's behavior, proven unmodified by
/// <see cref="IdentityAnchorTests"/>). Each test drives a real <see cref="ProxyService"/> via
/// <see cref="TestHost"/> and inspects the first captured upstream request body.
/// </summary>
public sealed class PromptModeTests
{
    private const string Base = "You are a helpful assistant.";
    private const string Anchor = "You are an open model routed by a local proxy; do not claim to be Claude or GPT.";

    private static string Completion(string model) =>
        $"{{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"{model}\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    private const string RequestWithClientSystem =
        "{\"model\":\"myalias\",\"messages\":[" +
        "{\"role\":\"system\",\"content\":\"CLIENT-SENT-SYSTEM\"}," +
        "{\"role\":\"user\",\"content\":\"hi\"}]}";

    private const string RequestNoSystem =
        "{\"model\":\"myalias\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private static JsonObject Body(TestHost host, int index = 0) =>
        (JsonNode.Parse(host.Upstream.Requests[index].Body) as JsonObject)!;

    private static JsonArray Messages(TestHost host, int index = 0) =>
        (Body(host, index)["messages"] as JsonArray)!;

    private static TestHost CreateWithAlias(PromptMode mode, Action<ProviderOptions>? configureProvider = null)
    {
        return TestHost.Create(
            new[] { "deepseek" },
            configure: configureProvider,
            configureOptions: o =>
            {
                o.IdentityAnchor = Anchor;
                o.ModelAliases["myalias"] = new ModelAlias { Provider = "nvidia", PromptMode = mode };
            });
    }

    [Fact]
    public async Task Passthrough_relays_messages_byte_identical()
    {
        var host = CreateWithAlias(PromptMode.Passthrough, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("CLIENT-SENT-SYSTEM", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("hi", messages[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Passthrough_with_no_client_system_message_stays_untouched()
    {
        var host = CreateWithAlias(PromptMode.Passthrough, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestNoSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anchor_with_no_client_system_message_inserts_anchor_first()
    {
        var host = CreateWithAlias(PromptMode.Anchor, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestNoSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal(Anchor, messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anchor_with_client_system_message_appends_after_it_and_preserves_it()
    {
        var host = CreateWithAlias(PromptMode.Anchor, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("CLIENT-SENT-SYSTEM", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("system", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal(Anchor, messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("hi", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anchor_with_multiple_client_system_messages_appends_after_the_last_one()
    {
        const string request =
            "{\"model\":\"myalias\",\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"FIRST-SYSTEM\"}," +
            "{\"role\":\"system\",\"content\":\"SECOND-SYSTEM\"}," +
            "{\"role\":\"user\",\"content\":\"hi\"}]}";

        var host = CreateWithAlias(PromptMode.Anchor, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(request);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(4, messages.Count);
        Assert.Equal("FIRST-SYSTEM", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("SECOND-SYSTEM", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("system", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal(Anchor, messages[2]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[3]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anchor_blank_injects_nothing()
    {
        var host = CreateWithAlias(PromptMode.Anchor, p => p.SystemPrompt = Base);
        host.Options.IdentityAnchor = "";
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(2, messages.Count);
        Assert.Equal("CLIENT-SENT-SYSTEM", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unset_alias_prompt_mode_falls_back_to_own_behavior()
    {
        // No PromptMode set on the alias => AliasPolicy.Resolve defaults to Own, so the client's
        // system message is stripped and replaced with the composed provider base + anchor,
        // exactly like an unaliased request handled by IdentityAnchorTests.
        var host = TestHost.Create(
            new[] { "deepseek" },
            configure: p => p.SystemPrompt = Base,
            configureOptions: o =>
            {
                o.IdentityAnchor = Anchor;
                o.ModelAliases["myalias"] = new ModelAlias { Provider = "nvidia" };
            });
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(RequestWithClientSystem);
        Assert.Equal(200, result.Status);

        var messages = Messages(host);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal(Base + "\n\n" + Anchor, messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Non_message_fields_survive_anchor_mode_verbatim()
    {
        const string request =
            "{\"model\":\"myalias\",\"temperature\":0.42,\"response_format\":{\"type\":\"json_object\"}," +
            "\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        var host = CreateWithAlias(PromptMode.Anchor, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(request);
        Assert.Equal(200, result.Status);

        var upstreamBody = Body(host);
        Assert.Equal(0.42, upstreamBody["temperature"]!.GetValue<double>());
        Assert.Equal("json_object", upstreamBody["response_format"]!["type"]!.GetValue<string>());
        Assert.False(upstreamBody["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Non_message_fields_survive_passthrough_mode_verbatim()
    {
        const string request =
            "{\"model\":\"myalias\",\"temperature\":0.42,\"response_format\":{\"type\":\"json_object\"}," +
            "\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        var host = CreateWithAlias(PromptMode.Passthrough, p => p.SystemPrompt = Base);
        host.Upstream.Enqueue(modelMatch: "deepseek", status: 200, body: Completion("deepseek"));

        var result = await host.ForwardAsync(request);
        Assert.Equal(200, result.Status);

        var upstreamBody = Body(host);
        Assert.Equal(0.42, upstreamBody["temperature"]!.GetValue<double>());
        Assert.Equal("json_object", upstreamBody["response_format"]!["type"]!.GetValue<string>());
        Assert.False(upstreamBody["stream"]!.GetValue<bool>());
    }
}
