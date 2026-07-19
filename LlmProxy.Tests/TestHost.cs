using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmProxy.Tests;

/// <summary>
/// Constructs a REAL <see cref="ProxyService"/> over fakes — no WebApplicationFactory. The upstream
/// is the scripted <see cref="FakeUpstream"/>; the candidate chain is pinned via <c>ForceModels</c>
/// so <c>BuildCandidatesAsync</c> returns a fixed list offline (no live /v1/models fetch). Tests
/// drive <see cref="ForwardAsync"/> and read the answer from the response body + the captured
/// upstream requests.
/// </summary>
public sealed class TestHost
{
    public FakeUpstream Upstream { get; }
    public ProxyService Proxy { get; }
    public RoutingState Routing { get; }
    public ProxyOptions Options { get; }

    private TestHost(FakeUpstream upstream, ProxyService proxy, RoutingState routing, ProxyOptions options)
    {
        Upstream = upstream;
        Proxy = proxy;
        Routing = routing;
        Options = options;
    }

    /// <summary>
    /// Build a host with a single "nvidia" provider whose candidate chain is <paramref name="forceModels"/>.
    /// A literal ApiKey keeps ResolveApiKey() non-empty; <paramref name="configure"/> can tweak the
    /// provider (e.g. add a SystemPrompt) before construction.
    ///
    /// Pass <paramref name="dynamicCatalog"/> instead of <paramref name="forceModels"/> to build a
    /// DYNAMIC provider (<see cref="ProviderOptions.DynamicModels"/> = true) whose live /v1/models
    /// catalog is faked via <see cref="FakeUpstream.CatalogModels"/> — used for alias pin-first /
    /// per-alias-prefer tests (T4) where the static ForceModels chain would bypass the candidate-seeding
    /// code path entirely.
    /// </summary>
    public static TestHost Create(IEnumerable<string>? forceModels = null, Action<ProviderOptions>? configure = null, Action<ProxyOptions>? configureOptions = null, IEnumerable<string>? dynamicCatalog = null)
    {
        var upstream = new FakeUpstream();

        var provider = new ProviderOptions
        {
            BaseUrl = "https://fake.upstream/v1",
            ApiKey = "test-key",
        };

        if (dynamicCatalog is not null)
        {
            provider.DynamicModels = true;
            upstream.CatalogModels = dynamicCatalog.ToList();
        }
        else
        {
            provider.ForceModels = (forceModels ?? new[] { "deepseek", "llama" }).ToList();
        }
        configure?.Invoke(provider);

        var options = new ProxyOptions
        {
            DefaultProvider = "nvidia",
            AnnounceModel = false,
            LogRequests = false,
            Providers = { ["nvidia"] = provider },
        };
        configureOptions?.Invoke(options);

        var iopts = Microsoft.Extensions.Options.Options.Create(options);
        var registry = new ProviderRegistry(iopts);
        var factory = new FakeHttpClientFactory(upstream);
        var catalog = new ModelCatalog(factory, registry, NullLogger<ModelCatalog>.Instance);
        var routing = new RoutingState();
        var proxy = new ProxyService(factory, registry, catalog, routing, NullLogger<ProxyService>.Instance);

        return new TestHost(upstream, proxy, routing, options);
    }

    /// <summary>Response of a driven request: HTTP status and the response body as text.</summary>
    public sealed record Result(int Status, string Body);

    /// <summary>
    /// Drive <see cref="ProxyService.ForwardJsonAsync"/> with the given request JSON: set up a
    /// <see cref="DefaultHttpContext"/> with the body as the request stream and a MemoryStream for
    /// the response, then return the response status + body text.
    /// </summary>
    public async Task<Result> ForwardAsync(string requestJson, string upstreamPath = "chat/completions")
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var responseBody = new MemoryStream();
        http.Response.Body = responseBody;

        await Proxy.ForwardJsonAsync(http, upstreamPath, CancellationToken.None);

        responseBody.Position = 0;
        var text = Encoding.UTF8.GetString(responseBody.ToArray());
        return new Result(http.Response.StatusCode, text);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly FakeUpstream _handler;
        public FakeHttpClientFactory(FakeUpstream handler) => _handler = handler;

        // Do not dispose the shared handler between clients — the scripted queue/log must persist.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
