using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Smoke tests for <see cref="IntegrationHost"/> itself: proves the real application (Program.cs,
/// real middleware, real minimal-API routing) can be booted in-process and driven with genuine
/// HTTP requests, with the upstream substituted for a scripted <see cref="FakeUpstream"/>. This is
/// the walking skeleton for every later auth/rate-limit-shaped ticket (T1, T2, T6) — it proves
/// today's open (no inbound auth) behaviour survives unchanged through a
/// WebApplicationFactory-driven pipeline; no service-mode feature exists yet.
/// </summary>
public sealed class IntegrationSmokeTests
{
    [Fact]
    public async Task Health_endpoint_answers_200_without_a_credential()
    {
        using var host = new IntegrationHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_chat_request_reaches_the_scripted_upstream_and_returns_200()
    {
        using var host = new IntegrationHost();
        host.Upstream.Enqueue(
            modelMatch: null,
            status: 200,
            body: "{\"id\":\"cmpl-1\",\"object\":\"chat.completion\",\"model\":\"deepseek\"," +
                  "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi there\"}," +
                  "\"finish_reason\":\"stop\"}]}");
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "auto",
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hi there", body);

        // Reached the REAL pipeline all the way to the scripted upstream (not a direct
        // ProxyService construction, as TestHost exercises) — this is the fact this ticket exists
        // to prove.
        Assert.Single(host.Upstream.Requests);
    }
}
