using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// T2: per-application rate limiting, driven through the real pipeline via <see cref="IntegrationHost"/>
/// (the limiter only exists in <c>Program.cs</c>'s middleware — see <see cref="RateLimitCounter"/>). Every
/// test compresses the window via <c>Proxy:RateLimitWindowSeconds</c> so bursts run in well under a
/// second; none of these tests sleep for a real 60-second window.
/// </summary>
public sealed class RateLimitTests
{
    private const string ChatCompletionBody =
        "{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"deepseek\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi there\"}," +
        "\"finish_reason\":\"stop\"}]}";

    private static Task<HttpResponseMessage> PostChatAsync(HttpClient client, string? bearerKey = null)
    {
        if (bearerKey is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
        else
            client.DefaultRequestHeaders.Authorization = null;

        // "fast" matches every key's granted alias in this file — T1's alias-grant enforcement
        // (merged in after these tests were written against T0c) rejects an unrecognised model
        // with 400 before the request ever reaches the rate limiter.
        return client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fast",
            messages = new[] { new { role = "user", content = "hello" } },
        });
    }

    // --- Burst past budget -> 429 + Retry-After, and the throttled request never reaches upstream ---

    [Fact]
    public async Task Burst_past_RequestsPerMinute_budget_returns_429_with_Retry_After()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:RateLimitWindowSeconds"] = "2",
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:secret-key-1:RequestsPerMinute"] = "2",
        };
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        var r1 = await PostChatAsync(client, "secret-key-1");
        var r2 = await PostChatAsync(client, "secret-key-1");
        var r3 = await PostChatAsync(client, "secret-key-1");

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
        Assert.True(r3.Headers.Contains("Retry-After"), "429 response must carry a Retry-After header.");
        var retryAfter = int.Parse(r3.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 2);

        var body = await r3.Content.ReadAsStringAsync();
        Assert.Contains("rate_limit_error", body);

        // Never-benches-a-model guarantee: the rejected 3rd request never reached the upstream —
        // only the first two (scripted) requests were captured.
        Assert.Equal(2, host.Upstream.Requests.Count);

        // And the routing state (cooldown memory) is untouched — the rejection happened entirely
        // in Program.cs's middleware, before ProxyService/candidate logic ever ran.
        var routing = host.Services.GetRequiredService<RoutingState>();
        Assert.False(routing.IsCoolingDown("deepseek"));
        Assert.False(routing.IsCoolingDown("llama"));
    }

    // --- Two keys, one App -> one shared budget, not two independent ones ---

    [Fact]
    public async Task Two_keys_for_one_app_share_one_budget()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:RateLimitWindowSeconds"] = "2",
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:secret-key-1:RequestsPerMinute"] = "2",
            ["Proxy:InboundKeys:secret-key-2:App"] = "acme", // same App, different key string
            ["Proxy:InboundKeys:secret-key-2:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:secret-key-2:RequestsPerMinute"] = "2",
        };
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        // Alternate keys across the burst -- if partitioning were by key string instead of App,
        // each key would get its own 2-request budget and all 3 would succeed (4 total capacity).
        var r1 = await PostChatAsync(client, "secret-key-1");
        var r2 = await PostChatAsync(client, "secret-key-2");
        var r3 = await PostChatAsync(client, "secret-key-1");

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
        Assert.Equal(2, host.Upstream.Requests.Count);
    }

    // --- App A exhausted does not throttle App B ---

    [Fact]
    public async Task Exhausting_one_app_does_not_throttle_another_app()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:RateLimitWindowSeconds"] = "2",
            ["Proxy:InboundKeys:key-a:App"] = "acme",
            ["Proxy:InboundKeys:key-a:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:key-a:RequestsPerMinute"] = "1",
            ["Proxy:InboundKeys:key-b:App"] = "globex",
            ["Proxy:InboundKeys:key-b:Aliases:0"] = "fast",
            ["Proxy:InboundKeys:key-b:RequestsPerMinute"] = "1",
        };
        using var host = new IntegrationHost(config);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        var aFirst = await PostChatAsync(client, "key-a");
        var aSecond = await PostChatAsync(client, "key-a"); // exhausts app "acme"'s budget of 1
        var bFirst = await PostChatAsync(client, "key-b"); // different app -- own budget

        Assert.Equal(HttpStatusCode.OK, aFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, aSecond.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bFirst.StatusCode);
    }

    // --- Unconfigured RequestsPerMinute for a resolved key -> unlimited ---

    [Fact]
    public async Task Key_with_no_RequestsPerMinute_configured_is_never_throttled()
    {
        var config = new Dictionary<string, string?>(IntegrationHost.DefaultConfig())
        {
            ["Proxy:RateLimitWindowSeconds"] = "2",
            ["Proxy:InboundKeys:secret-key-1:App"] = "acme",
            ["Proxy:InboundKeys:secret-key-1:Aliases:0"] = "fast",
            // RequestsPerMinute intentionally omitted -> null -> unlimited.
        };
        using var host = new IntegrationHost(config);
        for (var i = 0; i < 10; i++)
            host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var response = await PostChatAsync(client, "secret-key-1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(10, host.Upstream.Requests.Count);
    }

    // --- No keys configured at all (today's fully-open local behavior) -> never throttled ---

    [Fact]
    public async Task No_keys_configured_at_all_burst_is_never_throttled()
    {
        using var host = new IntegrationHost(); // IntegrationHost.DefaultConfig() has no InboundKeys.
        for (var i = 0; i < 10; i++)
            host.Upstream.Enqueue(modelMatch: null, status: 200, body: ChatCompletionBody);
        using var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var response = await PostChatAsync(client, bearerKey: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(10, host.Upstream.Requests.Count);
    }
}
