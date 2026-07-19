namespace LlmProxy;

/// <summary>
/// Result of successfully resolving an inbound bearer key: which application it belongs to, the
/// aliases it may request, and its optional per-minute budget. Stashed on
/// <see cref="HttpContext.Items"/> under <see cref="InboundAuth.CallerItemKey"/> so the forwarding
/// path — and T1's alias-grant enforcement and T2's per-application rate-limit partitioning
/// (see <see cref="RateLimitPartition"/>) — can read it without re-parsing the header.
/// </summary>
public sealed record ResolvedCaller(string App, IReadOnlyList<string> Aliases, int? RequestsPerMinute);

/// <summary>
/// Outcome of <see cref="InboundAuth.TryResolve"/>. Distinguishes WHY a request was not resolved to
/// a caller, so <c>Program.cs</c>'s pipeline middleware can build the right rejection shape without
/// re-deriving the reason (T1 — see the type's remarks for the acceptance criteria this exists to
/// satisfy). <see cref="Open"/> and <see cref="Ok"/> both mean "let the request through"; every other
/// value means 401. All 401-worthy reasons share one generic response message deliberately — the
/// caller is never told which of "missing" / "malformed" / "unknown" applied, so a bad guess can't be
/// used to probe for valid key shapes.
/// </summary>
public enum InboundAuthResult
{
    /// <summary>No keys configured at all — today's open, local behavior. <c>caller</c> is null.</summary>
    Open,

    /// <summary>Header carried a key that matched a configured entry. <c>caller</c> is non-null.</summary>
    Ok,

    /// <summary>Keys are configured but no <c>Authorization</c> header was sent (or it was blank).</summary>
    NoKeyProvided,

    /// <summary>An <c>Authorization</c> header was sent but isn't a well-formed <c>Bearer &lt;token&gt;</c>.</summary>
    MalformedHeader,

    /// <summary>A well-formed bearer token was sent but doesn't match any configured key.</summary>
    UnknownKey,
}

/// <summary>
/// Outcome of <see cref="InboundAuth.CheckAliasGrant"/> — whether a resolved caller's key may use the
/// model/alias named on the request.
/// </summary>
public enum AliasGrantResult
{
    /// <summary>The request may proceed; <c>effectiveModel</c> names the alias to route on.</summary>
    Granted,

    /// <summary>The request named a model this key's <see cref="ResolvedCaller.Aliases"/> doesn't include.</summary>
    AliasNotGranted,

    /// <summary>The key grants more than one alias and the request didn't say which one it wants.</summary>
    ModelRequiredAmbiguous,
}

/// <summary>
/// "Who is this caller, may they use this alias" — inbound authority (T1, extending T0c's happy
/// path). Two responsibilities, kept narrow and separate:
/// <list type="bullet">
/// <item><see cref="TryResolve"/> — bearer parsing + key lookup + rotation (N keys → one app).
/// Header-only; needs nothing but the raw header string and config, so it stays in the <c>/v1/*</c>
/// pipeline middleware in <c>Program.cs</c>, same call site T0c wired.</item>
/// <item><see cref="CheckAliasGrant"/> — alias-grant enforcement + single-grant model omission. This
/// needs the request's parsed JSON body (the <c>model</c> field), which the header-only middleware
/// never sees — <c>Program.cs</c> parses the body once, deep inside <see cref="ProxyService"/>. Rather
/// than parse the body twice (once in middleware, once in the service) or thread a pre-parsed body
/// through the pipeline, this check is invoked from <c>ProxyService.ForwardJsonAsync</c> right after
/// it parses the body and reads the client's requested model, using the <see cref="ResolvedCaller"/>
/// already stashed on <c>HttpContext.Items</c> by the middleware. <see cref="InboundAuth"/> still owns
/// the *rule* (pure, unit-testable here); <see cref="ProxyService"/> only wires it to the one place
/// that already holds the parsed body.</item>
/// </list>
/// Neither method ever echoes submitted key material into a message, log, or response body.
/// </summary>
public static class InboundAuth
{
    /// <summary>Key under which a resolved caller is stashed on <see cref="HttpContext.Items"/>.</summary>
    public const string CallerItemKey = "LlmProxy.InboundAuth.ResolvedCaller";

    /// <summary>
    /// Resolve an inbound request's caller. <see cref="InboundAuthResult.Open"/> and
    /// <see cref="InboundAuthResult.Ok"/> mean "let the request through" — <paramref name="caller"/>
    /// is non-null only for <c>Ok</c>. Every other value is a 401-worthy rejection reason; callers
    /// (i.e. the <c>/v1/*</c> pipeline middleware) should treat all of them identically for the
    /// response body — never reveal to the client which specific reason applied.
    /// </summary>
    public static InboundAuthResult TryResolve(string? authHeader, ProxyOptions options, out ResolvedCaller? caller)
    {
        caller = null;

        if (options.InboundKeys.Count == 0)
            return InboundAuthResult.Open; // no keys configured — open, local behavior.

        if (string.IsNullOrWhiteSpace(authHeader))
            return InboundAuthResult.NoKeyProvided;

        var key = ExtractBearerKey(authHeader);
        if (key is null) return InboundAuthResult.MalformedHeader;

        if (!options.InboundKeys.TryGetValue(key, out var inbound)) return InboundAuthResult.UnknownKey;

        caller = new ResolvedCaller(inbound.App, inbound.Aliases, inbound.RequestsPerMinute);
        return InboundAuthResult.Ok;
    }

    /// <summary>
    /// Check whether <paramref name="caller"/>'s key may use <paramref name="requestedModel"/>.
    /// A null/empty <paramref name="requestedModel"/> is only accepted when the key grants exactly
    /// one alias (that alias becomes <paramref name="effectiveModel"/>); with zero or several grants
    /// it's ambiguous and rejected. A non-empty <paramref name="requestedModel"/> must be one of
    /// <see cref="ResolvedCaller.Aliases"/> (case-insensitive) or the request is rejected.
    /// </summary>
    public static AliasGrantResult CheckAliasGrant(ResolvedCaller caller, string? requestedModel, out string? effectiveModel)
    {
        if (string.IsNullOrEmpty(requestedModel))
        {
            if (caller.Aliases.Count == 1)
            {
                effectiveModel = caller.Aliases[0];
                return AliasGrantResult.Granted;
            }

            effectiveModel = null;
            return AliasGrantResult.ModelRequiredAmbiguous;
        }

        if (!caller.Aliases.Any(a => string.Equals(a, requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            effectiveModel = null;
            return AliasGrantResult.AliasNotGranted;
        }

        effectiveModel = requestedModel;
        return AliasGrantResult.Granted;
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
