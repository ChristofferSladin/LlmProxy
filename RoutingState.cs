using System.Collections.Concurrent;

namespace LlmProxy;

/// <summary>
/// In-memory, concurrency-safe per-model routing memory shared across requests (the proxy is a
/// singleton). Owns cooldown bookkeeping (T1) and the learned tool-capability map (T2).
///
/// T0 ships the full interface with inert skeleton behavior: <see cref="IsCoolingDown"/> always
/// returns false and <see cref="IsToolCapable"/> always returns true, so runtime behavior is
/// unchanged until T1/T2 implement the logic. The backing dictionaries are already in place.
/// </summary>
public sealed class RoutingState
{
    // model id -> UTC timestamp until which the model is benched. Populated by T1.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    // model id -> tool-incapable flag. Absent = optimistically capable. Populated by T2.
    private readonly ConcurrentDictionary<string, bool> _toolIncapable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bench a model after a 200-err/429. T1/T2 implement — no-op in T0.</summary>
    public void RegisterCooldown(string model)
    {
        // T1/T2 implement
    }

    /// <summary>Whether a model is currently benched. T1/T2 implement — always false in T0 so behavior is unchanged.</summary>
    public bool IsCoolingDown(string model)
    {
        // T1/T2 implement
        return false;
    }

    /// <summary>Remember a model as tool-incapable after an explicit tool/function error. T1/T2 implement — no-op in T0.</summary>
    public void MarkToolIncapable(string model)
    {
        // T1/T2 implement
    }

    /// <summary>Whether a model is believed tool-capable (absent = optimistically capable). T1/T2 implement — always true in T0.</summary>
    public bool IsToolCapable(string model)
    {
        // T1/T2 implement
        return true;
    }
}
