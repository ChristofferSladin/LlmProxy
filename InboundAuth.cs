namespace LlmProxy;

/// <summary>
/// Result of successfully resolving an inbound bearer key: which application it belongs to, the
/// aliases it may request, and its optional per-minute budget. Stashed on
/// <see cref="HttpContext.Items"/> under <see cref="InboundAuth.CallerItemKey"/> so the forwarding
/// path — and later, T1's alias-grant enforcement and T2's per-application rate-limit partitioning
/// (see <see cref="RateLimitPartition"/>) — can read it without re-parsing the header.
/// </summary>
public sealed record ResolvedCaller(string App, IReadOnlyList<string> Aliases, int? RequestsPerMinute);

/// <summary>
/// "Who is this caller" — happy path only (T0c). <see cref="TryResolve"/> covers exactly the two
/// cases T0c's pipeline needs:
/// <list type="bullet">
/// <item><see cref="ProxyOptions.InboundKeys"/> empty/unconfigured → authenticated, unrestricted
/// (today's open local behavior — the critical backward-compat case; no <see cref="ResolvedCaller"/>
/// is produced, there is nothing to restrict).</item>
/// <item>a well-formed <c>Authorization: Bearer &lt;key&gt;</c> header whose key matches a
/// configured entry → resolves to that key's <see cref="InboundKey.App"/>,
/// <see cref="InboundKey.Aliases"/> and <see cref="InboundKey.RequestsPerMinute"/>.</item>
/// </list>
/// Every other input shape — keys ARE configured but the header is missing/malformed, or the key
/// doesn't match any entry — returns <c>false</c> with no rejection-message construction. That is
/// deliberately left for T1 (401/400 envelope shapes, rotation, no-leak guarantees). This method is
/// the seam T1 extends: it only needs to replace the generic <c>false</c> path with a specific
/// rejection reason, not restructure this type or its call sites.
/// </summary>
public static class InboundAuth
{
    /// <summary>Key under which a resolved caller is stashed on <see cref="HttpContext.Items"/>.</summary>
    public const string CallerItemKey = "LlmProxy.InboundAuth.ResolvedCaller";

    /// <summary>
    /// Resolve an inbound request's caller. Returns <c>true</c> when the request may proceed
    /// (either because no keys are configured, or because the header carries a valid key);
    /// <paramref name="caller"/> is non-null only in the latter case. Returns <c>false</c> for
    /// every other shape — T0c's callers only need to 401 generically on <c>false</c>; T1 owns the
    /// real rejection shapes.
    /// </summary>
    public static bool TryResolve(string? authHeader, ProxyOptions options, out ResolvedCaller? caller)
    {
        caller = null;

        if (options.InboundKeys.Count == 0)
            return true; // no keys configured — open, local behavior.

        var key = ExtractBearerKey(authHeader);
        if (key is null) return false;

        if (!options.InboundKeys.TryGetValue(key, out var inbound)) return false;

        caller = new ResolvedCaller(inbound.App, inbound.Aliases, inbound.RequestsPerMinute);
        return true;
    }

    private static string? ExtractBearerKey(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return null;
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var key = authHeader[prefix.Length..].Trim();
        return key.Length == 0 ? null : key;
    }
}
