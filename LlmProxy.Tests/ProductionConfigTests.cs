using Microsoft.Extensions.Configuration;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Proves the Production configuration layer (appsettings.Production.json) does what T7 requires:
/// no announce banner, no personal system-prompt file reference, no Kestrel loopback pin. Pure
/// config-binding test — no web host, replicating the real layering order from Program.cs:
/// appsettings.json -> appsettings.{Env}.json (SDK-automatic) -> appsettings.Local.json (explicit,
/// absent here) -> env vars (not exercised here).
/// </summary>
public class ProductionConfigTests
{
    private const string BaseLoopbackUrl = "http://localhost:5001";

    /// <summary>
    /// Walk up from the test assembly's output directory to find the repo root (where
    /// appsettings.json lives), since the test project's bin output doesn't carry the web
    /// project's content files.
    /// </summary>
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "appsettings.json")))
        {
            current = current.Parent;
        }

        if (current == null)
            throw new InvalidOperationException("Could not locate repo root containing appsettings.json from " + AppContext.BaseDirectory);

        return current.FullName;
    }

    private static IConfigurationRoot BuildLayeredConfig()
    {
        var root = FindRepoRoot();
        return new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .Build();
    }

    [Fact]
    public void ProductionLayer_DisablesAnnounceModel()
    {
        var config = BuildLayeredConfig();
        var options = new ProxyOptions();
        config.GetSection("Proxy").Bind(options);

        Assert.False(options.AnnounceModel);
    }

    [Fact]
    public void ProductionLayer_ClearsNvidiaSystemPromptFile()
    {
        var config = BuildLayeredConfig();
        var options = new ProxyOptions();
        config.GetSection("Proxy").Bind(options);

        Assert.True(options.Providers.ContainsKey("nvidia"));
        // The base file sets this to "system-prompt.md"; the Production layer must override it to
        // an empty string (JSON config layering can't null out a scalar key, but an empty string
        // makes Program.cs's `string.IsNullOrWhiteSpace(provider.SystemPromptFile)` startup check
        // skip the file entirely, so SystemPrompt is never resolved/loaded in production).
        Assert.Equal(string.Empty, options.Providers["nvidia"].SystemPromptFile);
    }

    [Fact]
    public void ProductionLayer_NoProviderReferencesASystemPromptFile()
    {
        var config = BuildLayeredConfig();
        var options = new ProxyOptions();
        config.GetSection("Proxy").Bind(options);

        foreach (var (name, provider) in options.Providers)
        {
            Assert.True(
                string.IsNullOrWhiteSpace(provider.SystemPromptFile),
                $"Provider '{name}' has a non-blank SystemPromptFile ('{provider.SystemPromptFile}') after layering Production; " +
                "this would attempt to load the personal prompt file in the hosted environment.");
        }
    }

    [Fact]
    public void ProductionLayer_KestrelHttpEndpoint_IsNotTheLocalLoopbackPin()
    {
        var config = BuildLayeredConfig();

        var effectiveUrl = config["Kestrel:Endpoints:Http:Url"];

        // Base appsettings.json alone pins http://localhost:5001. After layering Production, the
        // effective value must not be that loopback pin — either because Production overrides it
        // to a non-loopback bind address, or (if a future revision removes the key entirely so the
        // platform's ASPNETCORE_URLS/PORT can take over) because the key is absent.
        Assert.NotEqual(BaseLoopbackUrl, effectiveUrl);
    }

    [Fact]
    public void BaseAlone_StillPinsLoopback_RegressionGuardForTheAboveAssertion()
    {
        // Sanity check that the "not loopback" assertion above is actually exercising the layer
        // (i.e. the base file really does set the loopback pin the Production layer must defeat).
        var root = FindRepoRoot();
        var baseOnly = new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Equal(BaseLoopbackUrl, baseOnly["Kestrel:Endpoints:Http:Url"]);
    }
}
