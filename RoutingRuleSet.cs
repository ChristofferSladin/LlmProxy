namespace LlmProxy;

/// <summary>
/// Evaluates the ordered declarative <see cref="ProxyOptions.RoutingRules"/> against a request's
/// <see cref="RequestClassification"/> to produce a SOFT prefer-override — a list of substring patterns
/// that bias candidate prefer-ordering for this request. Purely a reordering hint: it never excludes a
/// candidate (that is the hard tool/cooldown filters' job) and never multiplies upstream calls.
///
/// FIRST-MATCH-WINS: rules are evaluated top-to-bottom; the first rule whose <see cref="RoutingWhen"/>
/// matches contributes its <see cref="RoutingRule.Prefer"/> list and evaluation stops. Predictable and
/// order-explicit — put more specific rules first. No rule matches → empty override → today's ordering.
/// </summary>
public sealed class RoutingRuleSet
{
    private readonly IReadOnlyList<RoutingRule> _rules;

    public RoutingRuleSet(IReadOnlyList<RoutingRule> rules) => _rules = rules;

    /// <summary>
    /// The prefer-pattern list of the first rule that matches <paramref name="c"/>, or an empty list if
    /// none match (empty = leave ordering unchanged).
    /// </summary>
    public IReadOnlyList<string> PreferOverride(RequestClassification c)
    {
        foreach (var rule in _rules)
            if (Matches(rule.When, c))
                return rule.Prefer;
        return Array.Empty<string>();
    }

    // A rule's `when` matches iff every SPECIFIED sub-condition holds; unset (null/empty) ones are ignored:
    //  - HasTools (if set) must equal the request's hasTools;
    //  - MinChars (if set) must be <= the request's char count;
    //  - ContentMatches (if non-empty) — any pattern present in the concatenated content.
    private static bool Matches(RoutingWhen when, RequestClassification c)
    {
        if (when.HasTools is { } wantTools && wantTools != c.HasTools) return false;
        if (when.MinChars is { } min && c.CharCount < min) return false;
        if (when.ContentMatches.Count > 0 && !c.Matches(when.ContentMatches)) return false;
        return true;
    }
}
