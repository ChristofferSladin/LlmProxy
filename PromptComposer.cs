using System.Text.Json.Nodes;

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

    /// <summary>
    /// Apply an alias's effective prompt mode to the client's message list, mutating
    /// <paramref name="body"/>'s "messages" array in place.
    /// <list type="bullet">
    /// <item><see cref="PromptMode.Own"/> (today's unset-alias default) — client system message(s) are
    /// stripped and replaced with the composed provider-base + identity-anchor system message, exactly
    /// as the forwarding path did inline before this seam existed. Nothing is injected when the
    /// composition is empty (see <see cref="Compose"/>).</item>
    /// <item><see cref="PromptMode.Passthrough"/> — no mutation: the client's messages are relayed
    /// byte-identical.</item>
    /// <item><see cref="PromptMode.Anchor"/> — not implemented here; wired in ticket T3.</item>
    /// </list>
    /// </summary>
    public static void Apply(EffectivePolicy policy, string? providerSystemPrompt, string? identityAnchor, JsonObject body)
    {
        switch (policy.PromptMode)
        {
            case PromptMode.Passthrough:
                return;

            case PromptMode.Anchor:
                throw new NotSupportedException(
                    "PromptMode.Anchor is not implemented yet — it lands in ticket T3. " +
                    "No alias should resolve to this mode before then.");

            case PromptMode.Own:
            default:
                ApplyOwn(providerSystemPrompt, identityAnchor, body);
                return;
        }
    }

    private static void ApplyOwn(string? providerSystemPrompt, string? identityAnchor, JsonObject body)
    {
        var systemContent = Compose(providerSystemPrompt, identityAnchor);
        if (string.IsNullOrEmpty(systemContent) || body["messages"] is not JsonArray messages) return;

        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i] is JsonObject m && m["role"]?.GetValue<string>() == "system")
                messages.RemoveAt(i);
        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemContent });
    }
}
