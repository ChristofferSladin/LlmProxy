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

    /// <summary>
    /// Bench <paramref name="model"/> until now + <paramref name="window"/>. The window is passed in
    /// (sourced from <c>ProxyOptions.CooldownSeconds</c> at the call site) so this component stays free
    /// of any options dependency. Thread-safe; a later bench simply overwrites the expiry.
    /// </summary>
    public void RegisterCooldown(string model, TimeSpan window)
    {
        if (string.IsNullOrEmpty(model) || window <= TimeSpan.Zero) return;
        _cooldownUntil[model] = DateTimeOffset.UtcNow + window;
    }

    /// <summary>Whether a non-expired bench exists for <paramref name="model"/>. Expired entries are dropped.</summary>
    public bool IsCoolingDown(string model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        if (!_cooldownUntil.TryGetValue(model, out var until)) return false;
        if (until > DateTimeOffset.UtcNow) return true;
        // Expired: prune so the map doesn't accumulate stale entries.
        _cooldownUntil.TryRemove(model, out _);
        return false;
    }

    /// <summary>
    /// Remember <paramref name="model"/> as tool-incapable after an explicit tool/function error on a
    /// request that carried <c>tools</c>. Thread-safe; idempotent (re-marking just overwrites). Silence
    /// (a model answering a tools request in prose) must NOT reach here — only explicit tool errors do.
    /// </summary>
    public void MarkToolIncapable(string model)
    {
        if (string.IsNullOrEmpty(model)) return;
        _toolIncapable[model] = true;
    }

    /// <summary>
    /// Whether <paramref name="model"/> is believed tool-capable. Absent from the map = optimistically
    /// capable (true); only an explicit demotion via <see cref="MarkToolIncapable"/> returns false.
    /// </summary>
    public bool IsToolCapable(string model)
    {
        if (string.IsNullOrEmpty(model)) return true;
        return !_toolIncapable.ContainsKey(model);
    }
}
