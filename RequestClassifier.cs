using System.Text.Json.Nodes;

namespace LlmProxy;

/// <summary>
/// Pure, upstream-free inspection of an incoming OpenAI-compatible request body — request *shape*,
/// never a network call. T2 exposes <see cref="HasTools"/> (does this request carry tools?). Kept
/// open for extension: T4 will add char-count and content-pattern matching to the same classifier.
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
}
