using Microsoft.Extensions.Hosting;
using Xunit;

namespace LlmProxy.Tests;

/// <summary>
/// Pure unit tests over <see cref="StartupValidation.Validate"/> — constructs <see cref="ProxyOptions"/>
/// directly and a minimal <see cref="IHostEnvironment"/> fake; no web host needed, mirroring the
/// "pure config-binding test" style used elsewhere in this suite (e.g. <see cref="AliasPolicyTests"/>).
/// </summary>
public sealed class StartupValidationTests
{
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "LlmProxy.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IHostEnvironment Env(string name) => new FakeHostEnvironment { EnvironmentName = name };

    private static ProxyOptions ValidConfig() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvidia"] = new ProviderOptions { BaseUrl = "https://fake.upstream/v1" },
        },
        ModelAliases = new Dictionary<string, ModelAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = new ModelAlias { Provider = "nvidia" },
        },
        InboundKeys = new Dictionary<string, InboundKey>
        {
            ["secret-key-1"] = new InboundKey { App = "acme", Aliases = { "fast" } },
        },
    };

    [Fact]
    public void Development_with_empty_inbound_keys_does_not_throw()
    {
        var options = new ProxyOptions(); // no keys, no aliases, no providers

        var ex = Record.Exception(() => StartupValidation.Validate(options, Env(Environments.Development)));

        Assert.Null(ex);
    }

    [Fact]
    public void Production_with_empty_inbound_keys_throws()
    {
        var options = new ProxyOptions(); // InboundKeys defaults to empty dictionary

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupValidation.Validate(options, Env(Environments.Production)));

        Assert.Contains("InboundKeys", ex.Message);
    }

    [Fact]
    public void Production_with_at_least_one_inbound_key_does_not_throw_for_the_empty_keys_rule()
    {
        var options = ValidConfig();

        var ex = Record.Exception(() => StartupValidation.Validate(options, Env(Environments.Production)));

        Assert.Null(ex);
    }

    [Fact]
    public void Key_granting_unknown_alias_throws_naming_app_and_bad_alias_but_never_the_key_string()
    {
        var options = ValidConfig();
        options.InboundKeys["secret-key-1"].Aliases.Add("new-digest"); // typo'd/nonexistent alias
        const string keyString = "secret-key-1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupValidation.Validate(options, Env(Environments.Development)));

        Assert.Contains("acme", ex.Message); // the key's App
        Assert.Contains("new-digest", ex.Message); // the bad alias name
        Assert.DoesNotContain(keyString, ex.Message); // never the secret key material
    }

    [Fact]
    public void Alias_naming_unknown_provider_throws_naming_alias_and_bad_provider()
    {
        var options = ValidConfig();
        options.ModelAliases["fast"].Provider = "ghost-provider";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupValidation.Validate(options, Env(Environments.Development)));

        Assert.Contains("fast", ex.Message);
        Assert.Contains("ghost-provider", ex.Message);
    }

    [Fact]
    public void Fully_valid_config_does_not_throw()
    {
        var options = ValidConfig();

        var ex = Record.Exception(() => StartupValidation.Validate(options, Env(Environments.Production)));

        Assert.Null(ex);
    }

    [Fact]
    public void Fully_valid_development_config_with_no_keys_does_not_throw()
    {
        var options = new ProxyOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvidia"] = new ProviderOptions { BaseUrl = "https://fake.upstream/v1" },
            },
            ModelAliases = new Dictionary<string, ModelAlias>(StringComparer.OrdinalIgnoreCase)
            {
                ["fast"] = new ModelAlias { Provider = "nvidia" },
            },
        };

        var ex = Record.Exception(() => StartupValidation.Validate(options, Env(Environments.Development)));

        Assert.Null(ex);
    }
}
