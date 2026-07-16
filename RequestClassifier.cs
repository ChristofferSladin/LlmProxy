using System.Text;
using System.Text.Json.Nodes;

namespace LlmProxy;

/// <summary>
/// Immutable, per-request shape summary produced by <see cref="RequestClassifier.Classify"/> ONCE per
/// request. Holds the cheap facts the declarative router keys on — whether the request carries tools,
/// the total character count of its concatenated message content, and that content itself (so pattern
/// matches don't re-traverse the JSON). Consumed by <see cref="RoutingRuleSet"/>.
/// </summary>
public readonly record struct RequestClassification(bool HasTools, string Content)
{
    /// <summary>Total characters of the concatenated message content.</summary>
    public int CharCount => Content.Length;

    /// <summary>Whether the concatenated content contains any of <paramref name="patterns"/> (case-insensitive substring).</summary>
    public bool Matches(IEnumerable<string> patterns)
    {
        var content = Content; // copy out of 'this' — a struct member can't be captured by the lambda.
        return patterns.Any(p => !string.IsNullOrEmpty(p) && content.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Pure, upstream-free inspection of an incoming OpenAI-compatible request body — request *shape*,
/// never a network call. T2 exposes <see cref="HasTools"/>; T4 adds <see cref="CharCount"/> and
/// <see cref="Matches"/>, plus <see cref="Classify"/> which bundles them into a single
/// <see cref="RequestClassification"/> computed once (concatenating message content only one time).
/// </summary>
public static class RequestClassifier
{
    /// <summary>
    /// True iff <paramref name="body"/> carries a non-empty <c>tools</c> array — i.e. the request is
    /// asking the model to be able to call tools/functions. A missing, null, non-array, or empty
    /// <c>tools</c> value is not a tools request.
    /// </summary>
    public static bool HasTools(JsonObject? body) =>
        body?["tools"] is JsonArray tools && tools.Count > 0;

    /// <summary>Total characters of the concatenated message content in <paramref name="body"/>.</summary>
    public static int CharCount(JsonObject? body) => ConcatContent(body).Length;

    /// <summary>
    /// Whether the concatenated message content of <paramref name="body"/> contains any of
    /// <paramref name="patterns"/> (case-insensitive substring — cheap, no regex).
    /// </summary>
    public static bool Matches(JsonObject? body, IEnumerable<string> patterns)
    {
        var content = ConcatContent(body);
        return patterns.Any(p => !string.IsNullOrEmpty(p) && content.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Classify the request ONCE: traverse the messages a single time and bundle the shape facts.</summary>
    public static RequestClassification Classify(JsonObject? body) =>
        new(HasTools(body), ConcatContent(body));

    // Concatenate the text of every message's content. Content is usually a string; the OpenAI schema
    // also allows an array of content parts (multimodal) where text parts carry a "text" field — those
    // are included, non-text parts ignored. Cheap: one pass, newline-joined.
    private static string ConcatContent(JsonObject? body)
    {
        if (body?["messages"] is not JsonArray messages) return "";

        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg is not JsonObject m) continue;
            switch (m["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s);
                    break;
                case JsonArray parts:
                    foreach (var part in parts)
                        if (part is JsonObject po && po["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(t);
                        }
                    break;
            }
        }
        return sb.ToString();
    }
}
