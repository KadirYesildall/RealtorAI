using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Libs.Ai;

namespace Libs.Compliance;

/// <summary>A judge result and the call that produced it (VIII.5).</summary>
public sealed record JudgeOutcome(IReadOnlyList<Finding> Findings, AiResult Call);

/// <summary>
/// T129 — the context-sensitive Fair Housing layer: the <c>compliance.judge</c> task (FR-017,
/// FR-024).
///
/// <para>
/// <b>Deterministic.</b> The judge is dispatched at the temperature its registry entry records, 0,
/// and <see cref="IAiService"/> passes it to the provider. Identical text therefore yields identical
/// findings (FR-024), and the response cache answers a repeat without a second call.
/// </para>
/// <para>
/// <b>Located, not trusted.</b> The judge names phrases; this finds them in the text. A phrase that
/// is not in the text is dropped, because a finding nobody can locate cannot be fixed or
/// reviewed. Each occurrence is placed once, in text order.
/// </para>
/// </summary>
public static partial class JudgeChecker
{
    /// <exception cref="ModelUnavailableException">No model is enabled for <c>compliance.judge</c>.</exception>
    /// <exception cref="AiBudgetExceededException">The call would exceed the listing's budget.</exception>
    /// <exception cref="StructuredOutputException">The answer failed validation twice.</exception>
    public static async Task<JudgeOutcome> JudgeAsync(
        this IAiService ai,
        Guid listingId,
        Guid actorId,
        string kind,
        string text,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(text);

        var result = await ai.ExecuteAsync(
            new AiRequest(AiTaskKey.ComplianceJudge, listingId, actorId, BuildInput(kind, text), ImageCount: 0, estimatedTokens),
            cancellationToken);

        return new JudgeOutcome(ReadFindings(text, result.Document), result);
    }

    /// <summary>
    /// The user message of one judge call: which review, the <paramref name="kind"/> of copy (its
    /// <c>content_kind</c> label, e.g. <c>public_remarks</c>) and the <paramref name="text"/>.
    ///
    /// <para>
    /// Public, with <see cref="ReadFindings"/>, because two callers must send and read exactly
    /// the same thing: this checker, and the evaluation harness measuring a judge model before it
    /// is enabled (T157). A model measured on a differently worded request, or scored by a
    /// different reading of its answer, would have been measured on a check production never runs.
    /// </para>
    /// </summary>
    public static string BuildInput(string kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new JsonObject { ["review"] = "fair_housing", ["kind"] = kind, ["text"] = text }.ToJsonString();
    }

    /// <summary>
    /// Reads a schema-valid judge answer into findings located in <paramref name="text"/>: each
    /// reported phrase is placed at its first unplaced occurrence, and one that is not in the text
    /// is dropped.
    /// </summary>
    /// <param name="document">An answer that has passed the <c>compliance.judge</c> schema.</param>
    public static IReadOnlyList<Finding> ReadFindings(string text, JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<Finding>();

        foreach (var node in document["findings"]?.AsArray() ?? [])
        {
            var phrase = node!["phrase"]!.GetValue<string>();
            var start = Locate(text, phrase, findings);

            if (start < 0)
            {
                continue;
            }

            var protectedClass = ClassName().Replace(node["protectedClass"]!.GetValue<string>().ToLowerInvariant(), "_");

            findings.Add(new Finding(
                $"fair_housing.judge.{protectedClass}",
                CheckType.FairHousing,
                node["severity"]!.GetValue<string>() == "blocking" ? Severity.Blocking : Severity.Warning,
                FindingSource.Judge,
                node["reason"]!.GetValue<string>(),
                start,
                start + phrase.Length,
                text.Substring(start, phrase.Length),
                Spans.MatchCase(text.Substring(start, phrase.Length), node["rewrite"]!.GetValue<string>())));
        }

        return findings;
    }

    /// <summary>The first occurrence not already placed, ignoring case; -1 when there is none.</summary>
    private static int Locate(string text, string phrase, List<Finding> placed)
    {
        if (phrase.Length == 0)
        {
            return -1;
        }

        for (var from = 0; from <= text.Length - phrase.Length;)
        {
            var found = text.IndexOf(phrase, from, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return -1;
            }

            if (!placed.Any(finding => finding.SpanStart < found + phrase.Length && found < finding.SpanEnd))
            {
                return found;
            }

            from = found + 1;
        }

        return -1;
    }

    [GeneratedRegex("[^a-z_]+", RegexOptions.CultureInvariant)]
    private static partial Regex ClassName();
}

/// <summary>
/// T130 — one set of Fair Housing findings from two layers (FR-017).
///
/// <para>
/// Where the judge flags text the lexicon already flagged, the lexicon's finding stands: it is
/// deterministic, versioned and reviewable, and one phrase should be one finding to fix. What the
/// judge alone saw is kept as the judge's, so each finding records which layer produced it.
/// </para>
/// </summary>
public static class FindingMerger
{
    public static IReadOnlyList<Finding> Merge(IEnumerable<Finding> lexicon, IEnumerable<Finding> judge)
    {
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(judge);

        var deterministic = lexicon.ToList();
        var judged = Spans.WithoutOverlaps(judge.Where(finding => !deterministic.Any(finding.Overlaps)));

        return deterministic.Concat(judged).OrderBy(finding => finding.SpanStart).ThenBy(finding => finding.RuleId, StringComparer.Ordinal).ToList();
    }
}
