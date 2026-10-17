namespace Libs.Compliance;

/// <summary>Which check raised a finding — the contract's <c>checkType</c>.</summary>
public enum CheckType
{
    FairHousing,
    RiskyClaim,
    MlsRule,
    PhotoIntegrity,
    AdvertisingDisclosure,
}

/// <summary>
/// A blocking finding prevents approval and can never be dismissed (FR-022); a warning may be
/// acknowledged with a recorded reason (FR-023).
/// </summary>
public enum Severity
{
    Blocking,
    Warning,
}

/// <summary>Which layer produced a finding (FR-017).</summary>
public enum FindingSource
{
    Lexicon,
    Judge,
    Deterministic,
}

/// <summary>
/// One compliance finding, before it is stored against a target.
/// </summary>
/// <param name="RuleId">
/// Stable identifier of the rule, e.g. <c>mls.forbidden.phone_number</c>. Together with the
/// span, phrase and reason it is what makes a finding the same finding on a re-run, so an
/// acknowledgement survives a check of unchanged words.
/// </param>
/// <param name="Reason">English by design: analysis of U.S.-English listing copy (research R15).</param>
/// <param name="SpanStart">Start of the offending text, in UTF-16 units; null for a finding about the whole item.</param>
/// <param name="SuggestedFix">The text to put in place of the span; null when there is nothing to rewrite.</param>
public sealed record Finding(
    string RuleId,
    CheckType CheckType,
    Severity Severity,
    FindingSource Source,
    string Reason,
    int? SpanStart = null,
    int? SpanEnd = null,
    string? Phrase = null,
    string? SuggestedFix = null)
{
    internal bool Overlaps(Finding other) =>
        SpanStart is { } start && SpanEnd is { } end
        && other.SpanStart is { } otherStart && other.SpanEnd is { } otherEnd
        && start < otherEnd && otherStart < end;
}

/// <summary>Helpers shared by the text checks.</summary>
internal static class Spans
{
    /// <summary>
    /// The rewrite in the case the original began with, so a fix at the start of a sentence starts
    /// with a capital and one mid-sentence does not.
    /// </summary>
    internal static string MatchCase(string original, string rewrite)
    {
        if (original.Length == 0 || rewrite.Length == 0)
        {
            return rewrite;
        }

        var first = char.IsUpper(original[0]) ? char.ToUpperInvariant(rewrite[0]) : char.ToLowerInvariant(rewrite[0]);

        return first + rewrite[1..];
    }

    /// <summary>Keeps, of overlapping findings, the one starting first (the longer on a tie), in text order.</summary>
    internal static List<Finding> WithoutOverlaps(IEnumerable<Finding> findings)
    {
        var kept = new List<Finding>();

        foreach (var finding in findings.OrderBy(finding => finding.SpanStart).ThenByDescending(finding => finding.SpanEnd))
        {
            if (!kept.Any(finding.Overlaps))
            {
                kept.Add(finding);
            }
        }

        return kept;
    }
}
