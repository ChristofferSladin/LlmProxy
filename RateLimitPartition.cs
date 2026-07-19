namespace LlmProxy;

/// <summary>
/// T0c seam: given a resolved caller, the partition key T2's real rate limiter will bucket on.
/// Partitioned by <see cref="ResolvedCaller.App"/> — not by the key string — so two live keys
/// rotated for the same application share one budget (PRD rotation requirement), and so the
/// partition key is never derived from secret material.
///
/// No-op stub in T0c: no rate-limiter middleware is registered in <c>Program.cs</c> yet (see the
/// comment at that call site for the reasoning), so nothing in this ticket calls
/// <see cref="KeyFor"/>. It exists purely so T2 can wire .NET's <c>AddRateLimiter</c> against a
/// stable, already-agreed partitioning rule without touching <c>Program.cs</c>'s composition
/// again.
/// </summary>
public static class RateLimitPartition
{
    /// <summary>The partition key a resolved caller's requests should be bucketed under.</summary>
    public static string KeyFor(ResolvedCaller caller) => caller.App;
}
