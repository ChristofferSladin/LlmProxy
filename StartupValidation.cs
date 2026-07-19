namespace LlmProxy;

/// <summary>
/// T0c seam: the call site for startup validation lives in <c>Program.cs</c>, right after the
/// existing <c>SystemPromptFile</c> resolution block. This routine is intentionally a no-op today
/// — T6 fills in the real cross-field consistency rules (Production-requires-keys, a key granting
/// a nonexistent alias, an alias naming an unknown provider) without needing to touch
/// <c>Program.cs</c>'s composition again.
/// </summary>
public static class StartupValidation
{
    /// <summary>No-op in T0c. T6 replaces this body; the signature is the seam.</summary>
    public static void Validate(ProxyOptions options, IHostEnvironment environment)
    {
    }
}
