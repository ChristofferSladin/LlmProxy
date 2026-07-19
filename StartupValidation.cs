namespace LlmProxy;

/// <summary>
/// T0c seam: the call site for startup validation lives in <c>Program.cs</c>, right after the
/// existing <c>SystemPromptFile</c> resolution block. This routine owns every cross-field
/// consistency rule for <see cref="ProxyOptions"/>: Production-requires-keys, a key granting a
/// nonexistent alias, and an alias naming an unknown provider.
///
/// Judgement call: violations are collected and reported together (one <see cref="InvalidOperationException"/>
/// with one message per line) rather than failing fast on the first one found. A human fixing a
/// broken config file benefits from seeing every problem in one run instead of playing
/// whack-a-mole across repeated startup attempts; the rules are cheap to evaluate exhaustively.
/// </summary>
public static class StartupValidation
{
    /// <summary>
    /// Validates <paramref name="options"/> against <paramref name="environment"/>. Throws
    /// <see cref="InvalidOperationException"/> with an actionable, newline-joined message
    /// listing every violation found; does nothing when the config is valid.
    /// </summary>
    public static void Validate(ProxyOptions options, IHostEnvironment environment)
    {
        var violations = new List<string>();

        // Rule 1: Production must not be reachable with zero configured inbound keys.
        if (environment.EnvironmentName == Environments.Production && (options.InboundKeys is null || options.InboundKeys.Count == 0))
        {
            violations.Add("Production requires at least one InboundKeys entry; refusing to start open.");
        }

        // Rule 2: every alias an inbound key grants must exist in ModelAliases. Name the key's
        // App in the message — never the dictionary key, which is the secret key material itself.
        if (options.InboundKeys is not null)
        {
            foreach (var key in options.InboundKeys.Values)
            {
                foreach (var aliasName in key.Aliases)
                {
                    if (!options.ModelAliases.ContainsKey(aliasName))
                    {
                        violations.Add(
                            $"InboundKeys entry for app '{key.App}' grants unknown alias '{aliasName}' (check ModelAliases).");
                    }
                }
            }
        }

        // Rule 3: every ModelAliases entry must route to a configured provider.
        foreach (var (aliasName, alias) in options.ModelAliases)
        {
            if (!options.Providers.ContainsKey(alias.Provider))
            {
                violations.Add(
                    $"ModelAliases entry '{aliasName}' names unknown provider '{alias.Provider}' (check Providers).");
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup validation failed with {violations.Count} problem(s):\n- " + string.Join("\n- ", violations));
        }
    }
}
