namespace LlmProxy;

/// <summary>
/// Pure, deterministic composition of the proxy-owned system message: the provider's base system
/// prompt with the model-agnostic identity/continuity anchor appended after a blank line. Hides the
/// empty-base / empty-anchor cases so callers get a single string (or null = inject nothing).
/// </summary>
public static class PromptComposer
{
    /// <summary>
    /// Combine the provider's base system prompt with the identity anchor.
    /// <list type="bullet">
    /// <item>both present → <paramref name="providerSystemPrompt"/> + a blank line + <paramref name="identityAnchor"/></item>
    /// <item>only the anchor present → the anchor</item>
    /// <item>only the base present → the base</item>
    /// <item>neither present (null / blank / whitespace) → <c>null</c> (no system message injected)</item>
    /// </list>
    /// </summary>
    public static string? Compose(string? providerSystemPrompt, string? identityAnchor)
    {
        var hasBase = !string.IsNullOrWhiteSpace(providerSystemPrompt);
        var hasAnchor = !string.IsNullOrWhiteSpace(identityAnchor);

        if (hasBase && hasAnchor) return providerSystemPrompt + "\n\n" + identityAnchor;
        if (hasAnchor) return identityAnchor;
        if (hasBase) return providerSystemPrompt;
        return null;
    }
}
