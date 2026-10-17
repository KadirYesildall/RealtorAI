using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Libs.Ai.Tasks;

namespace Modules.Content;

/// <summary>A fact of the listing as grounding judges it.</summary>
/// <param name="Sourced">
/// Entered by the agent or taken from a public record: the only provenances a measurement or
/// legal claim may rest on (FR-007).
/// </param>
/// <param name="NeedsConfirmation">AI-derived and not yet confirmed by the agent (FR-006, FR-014).</param>
public sealed record GroundingFact(Guid FieldId, string Key, JsonNode? Value, bool Sourced, bool NeedsConfirmation);

/// <summary>A span of a body and the facts it cites. Spans are <c>[SpanStart, SpanEnd)</c> in UTF-16 units.</summary>
public sealed record Claim(int SpanStart, int SpanEnd, IReadOnlyList<Guid> FieldIds);

/// <summary>A span resting on an AI-derived fact nobody has confirmed (FR-014).</summary>
public sealed record UnconfirmedClaim(int SpanStart, int SpanEnd, Guid FieldId);

/// <summary>A variant after grounding: the surviving sentences, joined, and what each cites.</summary>
/// <param name="RemovedSentences">Sentences removed for an unsupported claim or an unknown citation.</param>
public sealed record GroundedText(string Body, IReadOnlyList<Claim> Claims, int RemovedSentences);

/// <summary>
/// T111 — grounding: which generated sentences may stand, what each rests on, and which spans
/// rest on unconfirmed AI facts (FR-007, FR-014).
///
/// <para>
/// <b>A citation is checked, not trusted.</b> A sentence survives only if every id it cites is
/// a fact of this listing. A sentence making a measurement, tax, school, association or zoning
/// claim also has to cite a fact of that same kind, entered by the agent or taken from a public
/// record. Every figure in it has to be the value of a fact it cites, or one of the listing's
/// basic details. Recognising the claim is deliberately broad: a sentence that only looks like
/// a claim and is removed costs the agent a sentence, and an unsupported one that stays costs a
/// buyer a fact.
/// </para>
/// <para>
/// <b>Spans are computed when read.</b> What is stored is each sentence's span and its
/// citations. Whether a citation is unconfirmed is decided against the facts as they are now, so
/// confirming a fact clears the highlight without regenerating anything.
/// </para>
/// </summary>
public static partial class GroundingService
{
    /// <summary>The claim kinds of FR-007, each with the field keys that can support it.</summary>
    private static readonly (Regex Pattern, HashSet<string> Keys)[] ClaimKinds =
    [
        (MeasurementPattern(), new(StringComparer.Ordinal) { "LivingArea", "LivingAreaSquareFeet", "LotSizeSquareFeet", "LotSizeAcres", "YearBuilt" }),
        (TaxPattern(), new(StringComparer.Ordinal) { "TaxAnnualAmount", "TaxYear" }),
        (SchoolPattern(), new(StringComparer.Ordinal) { "HighSchoolDistrict", "ElementarySchoolDistrict", "MiddleOrJuniorSchoolDistrict" }),
        (AssociationPattern(), new(StringComparer.Ordinal) { "AssociationFee", "AssociationFeeFrequency" }),
        (ZoningPattern(), new(StringComparer.Ordinal) { "Zoning" }),
    ];

    /// <summary>
    /// Keeps the sentences that are grounded and joins them with single spaces.
    /// </summary>
    /// <param name="basicNumbers">Figures from the listing's basic details, which a sentence may state.</param>
    public static GroundedText Ground(
        IEnumerable<GeneratedSentence> sentences,
        IReadOnlyDictionary<Guid, GroundingFact> facts,
        IReadOnlyCollection<decimal> basicNumbers)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(basicNumbers);

        var body = new System.Text.StringBuilder();
        var claims = new List<Claim>();
        var removed = 0;

        foreach (var sentence in sentences)
        {
            if (sentence.Text.Length == 0)
            {
                continue;
            }

            if (!IsGrounded(sentence, facts, basicNumbers))
            {
                removed++;
                continue;
            }

            if (body.Length > 0)
            {
                body.Append(' ');
            }

            var start = body.Length;
            body.Append(sentence.Text);

            if (sentence.SupportingFieldIds.Count > 0)
            {
                claims.Add(new Claim(start, body.Length, sentence.SupportingFieldIds));
            }
        }

        return new GroundedText(body.ToString(), claims, removed);
    }

    /// <summary>The spans citing a fact that still needs the agent's confirmation, one per such fact.</summary>
    public static IReadOnlyList<UnconfirmedClaim> UnconfirmedClaims(IEnumerable<Claim> claims, IReadOnlyDictionary<Guid, GroundingFact> facts)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(facts);

        return claims
            .SelectMany(claim => claim.FieldIds
                .Where(id => facts.TryGetValue(id, out var fact) && fact.NeedsConfirmation)
                .Select(id => new UnconfirmedClaim(claim.SpanStart, claim.SpanEnd, id)))
            .ToList();
    }

    /// <summary>Every fact the body cites, each once.</summary>
    public static IReadOnlyList<Guid> SupportingFieldIds(IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        return claims.SelectMany(claim => claim.FieldIds).Distinct().ToList();
    }

    /// <summary>
    /// The claims of <paramref name="oldBody"/> that still stand in <paramref name="newBody"/>
    /// after an edit. A claim stands when its sentence is still there word for word, wherever it
    /// moved. A sentence the agent rewrote is now in the agent's own words, and its old citation
    /// no longer describes it.
    /// </summary>
    public static IReadOnlyList<Claim> Relocate(string oldBody, IEnumerable<Claim> oldClaims, string newBody)
    {
        ArgumentNullException.ThrowIfNull(oldBody);
        ArgumentNullException.ThrowIfNull(oldClaims);
        ArgumentNullException.ThrowIfNull(newBody);

        var relocated = new List<Claim>();
        var taken = new List<(int Start, int End)>();

        foreach (var claim in oldClaims.OrderBy(claim => claim.SpanStart))
        {
            if (claim.SpanStart < 0 || claim.SpanEnd > oldBody.Length || claim.SpanEnd <= claim.SpanStart)
            {
                continue;
            }

            var sentence = oldBody[claim.SpanStart..claim.SpanEnd];
            var from = 0;

            while (from <= newBody.Length - sentence.Length)
            {
                var found = newBody.IndexOf(sentence, from, StringComparison.Ordinal);

                if (found < 0)
                {
                    break;
                }

                var end = found + sentence.Length;

                if (!taken.Any(span => found < span.End && span.Start < end))
                {
                    taken.Add((found, end));
                    relocated.Add(claim with { SpanStart = found, SpanEnd = end });
                    break;
                }

                from = found + 1;
            }
        }

        return relocated.OrderBy(claim => claim.SpanStart).ToList();
    }

    private static bool IsGrounded(GeneratedSentence sentence, IReadOnlyDictionary<Guid, GroundingFact> facts, IReadOnlyCollection<decimal> basicNumbers)
    {
        if (sentence.CitesMalformedId || sentence.SupportingFieldIds.Any(id => !facts.ContainsKey(id)))
        {
            return false;
        }

        var cited = sentence.SupportingFieldIds.Select(id => facts[id]).ToList();
        var isClaim = false;

        foreach (var (pattern, keys) in ClaimKinds)
        {
            if (!pattern.IsMatch(sentence.Text))
            {
                continue;
            }

            isClaim = true;

            if (!cited.Any(fact => fact.Sourced && keys.Contains(fact.Key)))
            {
                return false;
            }
        }

        if (!isClaim)
        {
            return true;
        }

        var stated = cited.Where(fact => fact.Sourced).SelectMany(fact => Numbers(fact.Value)).Concat(basicNumbers).ToHashSet();

        return Figures(sentence.Text).All(stated.Contains);
    }

    /// <summary>The figures written in a sentence: <c>2,840</c>, <c>$14,250</c>, <c>0.25</c>.</summary>
    private static IEnumerable<decimal> Figures(string text) =>
        FigurePattern().Matches(text)
            .Select(match => decimal.Parse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture));

    private static IEnumerable<decimal> Numbers(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue scalar when scalar.TryGetValue<decimal>(out var number):
                yield return number;
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                foreach (var figure in Figures(text))
                {
                    yield return figure;
                }

                break;
            case JsonArray items:
                foreach (var item in items.SelectMany(Numbers))
                {
                    yield return item;
                }

                break;
        }
    }

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex FigurePattern();

    [GeneratedRegex(
        @"\b(?:square\s*(?:feet|foot|footage|ft)|sq\.?\s*ft|sqft|acres?|acreage|lot\s+size|year\s+built|built\s+in\s+\d{4})\b|\b\d[\d,.]*\s*(?:feet|foot|ft)\b|\b\d+\s*[x×]\s*\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasurementPattern();

    [GeneratedRegex(@"\btax(?:es|ed)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaxPattern();

    [GeneratedRegex(@"\bschools?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchoolPattern();

    [GeneratedRegex(@"\bHOA\b|\bhomeowners'?\s+association\b|\bassociation\s+(?:fees?|dues)\b|\bdues\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssociationPattern();

    [GeneratedRegex(@"\bzon(?:ed|ing)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZoningPattern();
}
