using System.Collections.Concurrent;

namespace LlmProxy;

/// <summary>
/// T2: given a resolved caller, the partition key the rate limiter buckets on. Partitioned by
/// <see cref="ResolvedCaller.App"/> — not by the key string — so two live keys rotated for the same
/// application share one budget (PRD rotation requirement), and so the partition key is never
/// derived from secret material.
/// </summary>
public static class RateLimitPartition
{
    /// <summary>The partition key a resolved caller's requests should be bucketed under.</summary>
    public static string KeyFor(ResolvedCaller caller) => caller.App;
}

/// <summary>
/// Per-partition fixed-window request counter. Hand-rolled rather than
/// <c>Microsoft.AspNetCore.RateLimiting</c>'s <c>AddRateLimiter</c>: that framework's partitioned
/// limiter wants one policy shape resolved per request via a partitioner callback, and this
/// feature needs a per-partition budget that varies by caller (each <see cref="ResolvedCaller"/>
/// carries its own <c>RequestsPerMinute</c>, and it can be null — meaning unlimited — for some
/// callers and set for others). Expressing "sometimes limited, sometimes not, with a
/// caller-supplied limit" against the framework's API means either a bespoke
/// <c>PartitionedRateLimiter</c> implementation or a no-op limiter per unlimited partition — both
/// more machinery than this: a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by partition,
/// each entry a fixed window that resets when it elapses. This is the "minimum code that works"
/// call from the ticket brief.
///
/// Thread-safe. A caller with <c>requestsPerMinute == null</c> is never counted or throttled
/// (unconfigured-means-unlimited).
/// </summary>
public sealed class RateLimitCounter
{
    private sealed class Window
    {
        public int Count;
        public DateTimeOffset StartedAt;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeSpan _windowLength;

    /// <param name="windowLength">
    /// Length of one budget window. Production uses 60 seconds (the "per minute" in
    /// <c>RequestsPerMinute</c>); tests compress this via
    /// <c>Proxy:RateLimitWindowSeconds</c> so bursts don't need real 60-second sleeps.
    /// </param>
    public RateLimitCounter(TimeSpan windowLength) => _windowLength = windowLength;

    /// <summary>
    /// Attempt to record one request against <paramref name="partitionKey"/>'s budget.
    /// <paramref name="requestsPerMinute"/> is the caller's configured budget for this window;
    /// null means unlimited, and the call always succeeds without being counted.
    /// </summary>
    public RateLimitResult TryAcquire(string partitionKey, int? requestsPerMinute)
    {
        if (requestsPerMinute is null) return RateLimitResult.Allow();

        var now = DateTimeOffset.UtcNow;
        var window = _windows.GetOrAdd(partitionKey, _ => new Window { Count = 0, StartedAt = now });

        lock (window)
        {
            if (now - window.StartedAt >= _windowLength)
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            if (window.Count >= requestsPerMinute.Value)
            {
                var retryAfter = window.StartedAt + _windowLength - now;
                var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                return RateLimitResult.Reject(retryAfterSeconds);
            }

            window.Count++;
            return RateLimitResult.Allow();
        }
    }
}

/// <summary>Outcome of a <see cref="RateLimitCounter.TryAcquire"/> call.</summary>
public readonly struct RateLimitResult
{
    public bool Allowed { get; }
    public int RetryAfterSeconds { get; }

    private RateLimitResult(bool allowed, int retryAfterSeconds)
    {
        Allowed = allowed;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public static RateLimitResult Allow() => new(true, 0);
    public static RateLimitResult Reject(int retryAfterSeconds) => new(false, retryAfterSeconds);
}
