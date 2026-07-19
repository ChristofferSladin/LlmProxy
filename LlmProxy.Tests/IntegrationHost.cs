using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Tests;

/// <summary>
/// Pipeline-level harness: boots the REAL application (<c>Program.cs</c>, real middleware, real
/// routing, real minimal-API endpoints) in-process via <see cref="WebApplicationFactory{Program}"/>
/// and hands back an <see cref="HttpClient"/> that drives genuine HTTP requests at it.
///
/// Complements <see cref="TestHost"/>, which constructs <see cref="ProxyService"/> directly and
/// therefore cannot observe anything that lives in the pipeline (auth middleware, rate limiting,
/// routing, startup validation). Use <see cref="TestHost"/> for routing/prompt/failover behaviour;
/// use this harness for anything only true above the forwarding service.
///
/// Three seams a test can use:
/// - <b>Configuration injection</b>: pass a <c>configOverrides</c> dictionary (flattened config
///   keys, e.g. <c>"Proxy:Providers:nvidia:BaseUrl"</c>) to the constructor, or start from
///   <see cref="DefaultConfig"/> and layer more entries on top with <c>with</c>-style copying.
///   These are added as the last configuration source, so they win over
///   <c>appsettings.json</c>/<c>appsettings.Local.json</c>.
/// - <b>Upstream substitution</b>: the real "upstream" <see cref="IHttpClientFactory"/> registration
///   is replaced wholesale with one backed by <see cref="Upstream"/> (a <see cref="FakeUpstream"/>),
///   mirroring <see cref="TestHost"/>'s <c>FakeHttpClientFactory</c> but wired in via
///   <c>ConfigureServices</c> so it survives real DI resolution, not direct construction.
/// - <b>Log capture</b>: every log line written by the host (any category/level) is appended to
///   <see cref="LogLines"/> in order, as the fully-formatted message text. This is what later
///   tickets (e.g. the no-key-leak assertion) should assert against — check
///   <c>LogLines.Any(l => l.Contains(theSecret))</c> is false, rather than re-deriving log capture.
/// </summary>
public sealed class IntegrationHost : WebApplicationFactory<Program>
{
    /// <summary>The scripted upstream every outgoing "upstream" HttpClient request is routed to.</summary>
    public FakeUpstream Upstream { get; } = new();

    /// <summary>Every log message written by the host during this instance's lifetime, in order.</summary>
    public List<string> LogLines { get; } = new();

    private readonly IReadOnlyDictionary<string, string?> _configOverrides;

    public IntegrationHost(IReadOnlyDictionary<string, string?>? configOverrides = null)
    {
        _configOverrides = configOverrides ?? DefaultConfig();
    }

    /// <summary>
    /// A minimal config that reproduces today's local/open behaviour (no inbound keys — none exist
    /// yet — no rate limits) while avoiding this host's two real-world dependencies: the personal
    /// <c>system-prompt.md</c> file (cleared here) and a live upstream catalog fetch (sidestepped
    /// via a pinned <c>ForceModels</c> chain, same trick <see cref="TestHost"/> uses). A test can
    /// start from this and override individual keys.
    /// </summary>
    public static Dictionary<string, string?> DefaultConfig() => new()
    {
        ["Proxy:DefaultProvider"] = "nvidia",
        ["Proxy:AnnounceModel"] = "false",
        ["Proxy:LogRequests"] = "false",
        ["Proxy:Providers:nvidia:BaseUrl"] = "https://fake.upstream/v1",
        ["Proxy:Providers:nvidia:ApiKey"] = "test-key",
        ["Proxy:Providers:nvidia:ApiKeyEnv"] = "",
        ["Proxy:Providers:nvidia:SystemPromptFile"] = "",
        ["Proxy:Providers:nvidia:SystemPrompt"] = "",
        ["Proxy:Providers:nvidia:DynamicModels"] = "false",
        ["Proxy:Providers:nvidia:ForceModels:0"] = "deepseek",
        ["Proxy:Providers:nvidia:ForceModels:1"] = "llama",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(_configOverrides);
        });

        builder.ConfigureServices(services =>
        {
            // Replace the real IHttpClientFactory registration wholesale. Every named client
            // (including "upstream") resolves to a client backed by the scripted FakeUpstream.
            // Registered after Program.cs's own AddHttpClient("upstream", ...) call, and DI
            // resolves the LAST registration for a singly-injected service — this wins.
            services.AddSingleton<IHttpClientFactory>(new SingleHandlerHttpClientFactory(Upstream));
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new CapturingLoggerProvider(LogLines));
            logging.SetMinimumLevel(LogLevel.Trace);
        });
    }

    /// <summary>Mirrors <c>TestHost.FakeHttpClientFactory</c>: every client shares one handler instance.</summary>
    private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        // Do not dispose the shared handler between clients — the scripted queue/log must persist.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>An <see cref="ILoggerProvider"/> that appends every formatted log message to a shared list.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _sink;
        public CapturingLoggerProvider(List<string> sink) => _sink = sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _sink;
            public CapturingLogger(string category, List<string> sink)
            {
                _category = category;
                _sink = sink;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (_sink) _sink.Add($"[{logLevel}] {_category}: {message}");
            }
        }
    }
}
