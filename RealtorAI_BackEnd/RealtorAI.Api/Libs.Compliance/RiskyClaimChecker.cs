using System.Globalization;
using System.Text.RegularExpressions;

namespace Libs.Compliance;

/// <summary>
/// T131 — risky claims: superlatives, guarantees, school claims, and measurements with no verified
/// source (FR-018; Story 4 scenario 3).
///
/// <para>
/// Warnings, each with a substitute. These claims are not unlawful in themselves; they are the ones
/// a buyer relies on and an agent may have to stand behind. A measurement is not risky when the
/// figure is the value of a field the agent entered or a public record supplied (FR-007).
/// </para>
/// </summary>
public static partial class RiskyClaimChecker
{
    private static readonly (string RuleId, Func<Regex> Pattern, string Reason, Func<string, string> Fix)[] Rules =
    [
        ("risky.school_claim", SchoolQuality,
            "A claim about school quality is not something the listing can verify, and school assignments change.",
            _ => "near local schools"),
        ("risky.school_assignment", SchoolAssignment,
            "School assignments are set by the district and can change; buyers should verify them.",
            _ => "Buyers should verify school assignment with the district"),
        ("risky.guarantee", Guarantee,
            "A guarantee about value or condition is a promise the listing cannot make.",
            _ => "well maintained"),
        ("risky.superlative", Superlative,
            "A superlative is a claim nobody can verify; describe the feature instead.",
            phrase => phrase.StartsWith("best", StringComparison.OrdinalIgnoreCase) ? "great value" : "well-appointed"),
    ];

    /// <param name="sourcedFigures">Values of fields entered by the agent or from a public record.</param>
    public static IReadOnlyList<Finding> Check(string text, IReadOnlyCollection<decimal> sourcedFigures)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourcedFigures);

        var findings = Rules.SelectMany(rule => rule.Pattern().Matches(text).Select(match => new Finding(
            rule.RuleId,
            CheckType.RiskyClaim,
            Severity.Warning,
            FindingSource.Deterministic,
            rule.Reason,
            match.Index,
            match.Index + match.Length,
            match.Value,
            Spans.MatchCase(match.Value, rule.Fix(match.Value)))));

        var measurements = Measurement().Matches(text)
            .Where(match => !sourcedFigures.Contains(decimal.Parse(
                match.Groups["figure"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Number,
                CultureInfo.InvariantCulture)))
            .Select(match => new Finding(
                "risky.measurement_unsourced",
                CheckType.RiskyClaim,
                Severity.Warning,
                FindingSource.Deterministic,
                "This figure is not from a field you entered or a public record, and measurements are claims you are liable for (FR-007).",
                match.Index,
                match.Index + match.Length,
                match.Value,
                "generous space"));

        return Spans.WithoutOverlaps(findings.Concat(measurements));
    }

    [GeneratedRegex(@"\b(?:best|top[- ]rated|top|award[- ]winning|excellent|great|blue[- ]ribbon|highly[- ]rated) schools?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchoolQuality();

    [GeneratedRegex(@"\b(?i:zoned for|assigned to|attends?) (?:the )?(?:[A-Z][\w'.-]* )+(?:Elementary|Middle|High|School|Academy)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SchoolAssignment();

    [GeneratedRegex(@"\b(?:will (?:appreciate|increase in value|only go up)|guaranteed?|flood[- ]free|never floods|no flooding|risk[- ]free|sure investment|can'?t lose)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Guarantee();

    [GeneratedRegex(@"\b(?:best (?:value|deal|buy|price|home|house|location)|finest|greatest|number one|most beautiful)\b|#1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Superlative();

    [GeneratedRegex(@"\b(?<figure>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*(?:square\s*(?:feet|foot|ft)|sq\.?\s*ft|sqft|acres?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Measurement();
}
