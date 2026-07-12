using Microsoft.Extensions.Options;

namespace LlmProxy;

public sealed record RouteTarget(string ProviderName, ProviderOptions Provider, IReadOnlyList<string> Models);

/// <summary>
/// Resolves a client-facing model id to the provider and upstream model that should serve it.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ProxyOptions _options;

    public ProviderRegistry(IOptions<ProxyOptions> options) => _options = options.Value;

    public ProxyOptions Options => _options;

    public IReadOnlyDictionary<string, ProviderOptions> Providers => _options.Providers;

    public bool TryGetProvider(string name, out ProviderOptions provider) =>
        _options.Providers.TryGetValue(name, out provider!);

    /// <summary>
    /// Route a request. If the model matches a configured alias, use it. Otherwise the model id is
    /// passed through unchanged to the default provider (which is how the full upstream catalog works
    /// without enumerating every model).
    /// </summary>
    public RouteTarget Resolve(string? clientModel)
    {
        clientModel ??= "";

        string providerName;
        ProviderOptions provider;
        string? aliasUpstream = null;

        if (_options.ModelAliases.TryGetValue(clientModel, out var alias))
        {
            providerName = alias.Provider;
            if (!_options.Providers.TryGetValue(providerName, out provider!))
                throw new InvalidOperationException($"Model alias '{clientModel}' points to unknown provider '{providerName}'.");
            aliasUpstream = alias.UpstreamModel;
        }
        else
        {
            providerName = _options.DefaultProvider;
            if (!_options.Providers.TryGetValue(providerName, out provider!))
                throw new InvalidOperationException($"Default provider '{providerName}' is not configured.");
        }

        var models = ResolveModels(provider, aliasUpstream, clientModel);
        if (models.Count == 0)
            throw new InvalidOperationException("No upstream model could be resolved. Set ForceModels/ForceModel or send a model.");

        return new RouteTarget(providerName, provider, models);
    }

    // Priority: ForceModels chain > ForceModel > alias upstream > client's model.
    private static IReadOnlyList<string> ResolveModels(ProviderOptions provider, string? aliasUpstream, string clientModel)
    {
        if (provider.ForceModels.Count > 0)
        {
            var chain = provider.ForceModels.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            if (chain.Count > 0) return chain;
        }

        var single = FirstNonEmpty(provider.ForceModel, aliasUpstream, clientModel);
        return string.IsNullOrEmpty(single) ? Array.Empty<string>() : new[] { single };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return "";
    }
}
