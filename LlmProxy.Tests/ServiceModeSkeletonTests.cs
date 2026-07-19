using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// T0c's gate: proves the pipeline wiring (auth middleware scoped to /v1/*, the startup-validation
/// call site, and the rate-limiter no-op) works end to end over <see cref="IntegrationHost"/> — the
/// REAL <c>Program.cs</c> composition, not a direct <see cref="ProxyService"/> construction. Happy
/// path only: rejection shapes are T1, real rate-limiting is T2, real startup validation is T6.
/// </summary>
public sealed class ServiceModeSkeletonTests
{
    private const string ChatCompletionBody =
        "{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"deepseek\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi there\"}," +
        "\"finish_reason\":\"stop\"}]}";

    // --- Test B: no InboundKeys configured -> today's open behavior survives the full pipeline ---

    [Fact]
    public async Task No_inbound_keys_configured_and_no_header_still_returns_200()
    {
        using var host = new IntegrationHost(); // IntegrationHost.DefaultConfig() has no InboundKeys.
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "auto",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Upstream.Requests);
    }

    // --- Test C: /health stays outside the auth scope, with or without InboundKeys configured ---

    [Fact]
    public async Task Health_is_reachable_without_a_header_when_no_keys_are_configured()
    {
        using var host = new IntegrationHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_without_a_header_even_when_keys_are_configured()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
        };
        using var host = new IntegrationHost(config);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_with_a_header_present_too()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
        };
        using var host = new IntegrationHost(config);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Test A: one key granting one alias -> keyed request resolves and routes via the alias ---

    [Fact]
    public async Task One_key_granting_one_alias_resolves_and_routes_the_keyed_request()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
            // Alias routes to the same "nvidia" provider DefaultConfig already sets up (with its
            // ForceModels chain), proving alias resolution flows through the real ProviderRegistry.
            ["Proxy:ModelAliases:fast:Provider"] = "nvidia",
        };
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hi there", body);
        Assert.Single(host.Upstream.Requests);
    }
}
