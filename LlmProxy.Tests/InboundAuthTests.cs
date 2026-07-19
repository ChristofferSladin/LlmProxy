using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// T1's gate: extends T0c's happy-path-only <see cref="InboundAuth"/> with the full rejection-shape
/// contract from the PRD's "Authentication and authorization" acceptance criteria. Pure rules
/// (<see cref="InboundAuth.TryResolve"/>, <see cref="InboundAuth.CheckAliasGrant"/>) are unit-tested
/// directly; wire shapes (401/400 bodies, health reachability, log/response no-leak) are exercised
/// through the real pipeline via <see cref="IntegrationHost"/>.
/// </summary>
public sealed class InboundAuthTests
{
    // ================= Unit tests: InboundAuth.TryResolve =================

    private static ProxyOptions OptionsWithOneKey(string key = "secret-key-1", string app = "acme", params string[] aliases) =>
        new()
        {
            InboundKeys = new Dictionary<string, InboundKey>
            {
                [key] = new InboundKey { App = app, Aliases = aliases.ToList() },
            },
        };

    [Fact]
    public void No_keys_configured_resolves_open_with_no_caller()
    {
        var options = new ProxyOptions();

        var result = InboundAuth.TryResolve(null, options, out var caller);

        Assert.Equal(InboundAuthResult.Open, result);
        Assert.Null(caller);
    }

    [Fact]
    public void Keys_configured_missing_header_is_NoKeyProvided()
    {
        var options = OptionsWithOneKey(aliases: "fast");

        var result = InboundAuth.TryResolve(null, options, out var caller);

        Assert.Equal(InboundAuthResult.NoKeyProvided, result);
        Assert.Null(caller);
    }

    [Fact]
    public void Keys_configured_blank_header_is_NoKeyProvided()
    {
        var options = OptionsWithOneKey(aliases: "fast");

        var result = InboundAuth.TryResolve("   ", options, out var caller);

        Assert.Equal(InboundAuthResult.NoKeyProvided, result);
        Assert.Null(caller);
    }

    [Theory]
    [InlineData("NotBearer whatever")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    public void Keys_configured_malformed_header_is_MalformedHeader(string header)
    {
        var options = OptionsWithOneKey(aliases: "fast");

        var result = InboundAuth.TryResolve(header, options, out var caller);

        Assert.Equal(InboundAuthResult.MalformedHeader, result);
        Assert.Null(caller);
    }

    [Fact]
    public void Keys_configured_unrecognized_key_is_UnknownKey()
    {
        var options = OptionsWithOneKey(aliases: "fast");

        var result = InboundAuth.TryResolve("Bearer wrong-secret-value", options, out var caller);

        Assert.Equal(InboundAuthResult.UnknownKey, result);
        Assert.Null(caller);
    }

    [Fact]
    public void Matching_key_resolves_Ok_with_caller()
    {
        var options = OptionsWithOneKey(key: "secret-key-1", app: "acme", aliases: new[] { "fast", "smart" });

        var result = InboundAuth.TryResolve("Bearer secret-key-1", options, out var caller);

        Assert.Equal(InboundAuthResult.Ok, result);
        Assert.NotNull(caller);
        Assert.Equal("acme", caller!.App);
        Assert.Equal(new[] { "fast", "smart" }, caller.Aliases);
    }

    [Fact]
    public void Two_distinct_keys_mapping_to_the_same_app_both_resolve()
    {
        var options = new ProxyOptions
        {
            InboundKeys = new Dictionary<string, InboundKey>
            {
                ["secret-key-1"] = new InboundKey { App = "acme", Aliases = { "fast" } },
                ["secret-key-2"] = new InboundKey { App = "acme", Aliases = { "fast" } },
            },
        };

        var result1 = InboundAuth.TryResolve("Bearer secret-key-1", options, out var caller1);
        var result2 = InboundAuth.TryResolve("Bearer secret-key-2", options, out var caller2);

        Assert.Equal(InboundAuthResult.Ok, result1);
        Assert.Equal(InboundAuthResult.Ok, result2);
        Assert.Equal("acme", caller1!.App);
        Assert.Equal("acme", caller2!.App);
    }

    // ================= Unit tests: InboundAuth.CheckAliasGrant =================

    [Fact]
    public void Single_grant_key_omitting_model_resolves_to_that_alias()
    {
        var caller = new ResolvedCaller("acme", new[] { "fast" }, null);

        var grant = InboundAuth.CheckAliasGrant(caller, null, out var effectiveModel);

        Assert.Equal(AliasGrantResult.Granted, grant);
        Assert.Equal("fast", effectiveModel);
    }

    [Fact]
    public void Single_grant_key_omitting_model_via_empty_string_resolves_to_that_alias()
    {
        var caller = new ResolvedCaller("acme", new[] { "fast" }, null);

        var grant = InboundAuth.CheckAliasGrant(caller, "", out var effectiveModel);

        Assert.Equal(AliasGrantResult.Granted, grant);
        Assert.Equal("fast", effectiveModel);
    }

    [Fact]
    public void Multi_grant_key_omitting_model_is_ambiguous()
    {
        var caller = new ResolvedCaller("acme", new[] { "fast", "smart" }, null);

        var grant = InboundAuth.CheckAliasGrant(caller, null, out var effectiveModel);

        Assert.Equal(AliasGrantResult.ModelRequiredAmbiguous, grant);
        Assert.Null(effectiveModel);
    }

    [Fact]
    public void Zero_grant_key_omitting_model_is_ambiguous()
    {
        var caller = new ResolvedCaller("acme", Array.Empty<string>(), null);

        var grant = InboundAuth.CheckAliasGrant(caller, null, out var effectiveModel);

        Assert.Equal(AliasGrantResult.ModelRequiredAmbiguous, grant);
        Assert.Null(effectiveModel);
    }

    [Fact]
    public void Requesting_a_granted_alias_succeeds()
    {
        var caller = new ResolvedCaller("acme", new[] { "fast", "smart" }, null);

        var grant = InboundAuth.CheckAliasGrant(caller, "smart", out var effectiveModel);

        Assert.Equal(AliasGrantResult.Granted, grant);
        Assert.Equal("smart", effectiveModel);
    }

    [Fact]
    public void Requesting_an_out_of_grant_alias_is_rejected()
    {
        var caller = new ResolvedCaller("acme", new[] { "fast", "smart" }, null);

        var grant = InboundAuth.CheckAliasGrant(caller, "reasoning", out var effectiveModel);

        Assert.Equal(AliasGrantResult.AliasNotGranted, grant);
        Assert.Null(effectiveModel);
    }

    // ================= Integration tests: wire shapes over the real pipeline =================

    private const string ChatCompletionBody =
        "{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"deepseek\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi there\"}," +
        "\"finish_reason\":\"stop\"}]}";

    private static Dictionary<string, string?> ConfigWithOneKey(string key = "secret-key-1", string app = "acme", params string[] aliases)
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            [$"Proxy:InboundKeys:{key}:App"] = app,
        };
        for (var i = 0; i < aliases.Length; i++)
            config[$"Proxy:InboundKeys:{key}:Aliases:{i}"] = aliases[i];
        return config;
    }

    [Fact]
    public async Task Health_stays_reachable_unkeyed_when_keys_are_configured()
    {
        using var host = new IntegrationHost(ConfigWithOneKey(aliases: "fast"));
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");
        var rootResponse = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
    }

    [Fact]
    public async Task Missing_header_is_rejected_401_in_the_existing_error_envelope()
    {
        using var host = new IntegrationHost(ConfigWithOneKey(aliases: "fast"));
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("\"type\":\"authentication_error\"", body);
        Assert.Contains("\"code\":401", body);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_401_and_never_leaked_in_body_or_logs()
    {
        const string wrongKey = "wrong-secret-value";
        using var host = new IntegrationHost(ConfigWithOneKey(aliases: "fast"));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wrongKey);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(wrongKey, body);
        Assert.DoesNotContain(host.LogLines, l => l.Contains(wrongKey));
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Malformed_header_is_rejected_401_and_never_leaked_in_body_or_logs()
    {
        const string suppliedValue = "NotBearer some-value-here";
        using var host = new IntegrationHost(ConfigWithOneKey(aliases: "fast"));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", suppliedValue);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("some-value-here", body);
        Assert.DoesNotContain(host.LogLines, l => l.Contains("some-value-here"));
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Valid_key_requesting_an_out_of_grant_alias_is_rejected_400_naming_allowed_aliases()
    {
        var config = ConfigWithOneKey(aliases: new[] { "fast", "smart" });
        config["Proxy:ModelAliases:fast:Provider"] = "nvidia";
        config["Proxy:ModelAliases:smart:Provider"] = "nvidia";
        using var host = new IntegrationHost(config);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "reasoning",
            messages = new[] { new { role = "user", content = "hello" } },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fast", body);
        Assert.Contains("smart", body);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Single_grant_key_omitting_model_succeeds_and_routes_to_that_alias()
    {
        var config = ConfigWithOneKey(aliases: "fast");
        config["Proxy:ModelAliases:fast:Provider"] = "nvidia";
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Upstream.Requests);
    }

    [Fact]
    public async Task Multi_grant_key_omitting_model_is_rejected_400()
    {
        var config = ConfigWithOneKey(aliases: new[] { "fast", "smart" });
        config["Proxy:ModelAliases:fast:Provider"] = "nvidia";
        config["Proxy:ModelAliases:smart:Provider"] = "nvidia";
        using var host = new IntegrationHost(config);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Two_live_keys_same_app_both_accepted_over_the_real_pipeline()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:secret-key-2:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-2:Aliases:0"] = "fast",
            ["Proxy:ModelAliases:fast:Provider"] = "nvidia",
        };
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-1");
        var response1 = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-key-2");
        var response2 = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, host.Upstream.Requests.Count);
    }
}
