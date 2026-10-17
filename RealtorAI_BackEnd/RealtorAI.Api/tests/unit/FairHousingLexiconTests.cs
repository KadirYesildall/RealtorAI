using Libs.Compliance;

namespace Tests.Unit;

/// <summary>
/// T120 — golden tests for the Fair Housing lexicon: the federal protected classes and
/// California's additions (FR-017; Story 4 scenarios 1, 2).
///
/// <para>
/// Each case is a phrase as it appears in real listing copy. The lexicon must find it, name the
/// protected class, give the exact span, and offer a rewrite that does not trip the lexicon
/// itself. And the negatives matter as much: describing the property — a family room, a
/// primary bedroom near a park — must not raise anything, or agents learn to ignore the check.
/// </para>
/// </summary>
public sealed class FairHousingLexiconTests
{
    private static readonly LexiconMatcher Lexicon = LexiconMatcher.FairHousing;

    [Theory]
    // ── Federal: familial status ──
    [InlineData("Perfect for young families, with a big yard.", "Perfect for young families", "familial_status", Severity.Blocking)]
    [InlineData("No children, please.", "No children", "familial_status", Severity.Blocking)]
    [InlineData("This is an adults only building.", "adults only", "familial_status", Severity.Blocking)]
    [InlineData("Ideal for a mature couple.", "Ideal for a mature couple", "familial_status", Severity.Blocking)]
    // ── Federal: religion ──
    [InlineData("A peaceful Christian community.", "Christian community", "religion", Severity.Blocking)]
    [InlineData("Walking distance to church and shops.", "Walking distance to church", "religion", Severity.Warning)]
    // ── Federal: race, colour, national origin ──
    [InlineData("A quiet, exclusive neighborhood.", "exclusive neighborhood", "race", Severity.Warning)]
    [InlineData("English speakers only.", "English speakers only", "national_origin", Severity.Blocking)]
    [InlineData("Located in a white neighborhood.", "white neighborhood", "race", Severity.Blocking)]
    // ── Federal: sex ──
    [InlineData("Great for a bachelor.", "Great for a bachelor", "sex", Severity.Blocking)]
    // ── Federal: disability ──
    [InlineData("Not suitable for wheelchairs.", "Not suitable for wheelchairs", "disability", Severity.Blocking)]
    [InlineData("Must be able-bodied to climb the stairs.", "able-bodied", "disability", Severity.Blocking)]
    // ── California: source of income ──
    [InlineData("Sorry, no Section 8.", "no Section 8", "source_of_income", Severity.Blocking)]
    [InlineData("No vouchers accepted.", "No vouchers", "source_of_income", Severity.Blocking)]
    // ── California: marital status, sexual orientation, military status ──
    [InlineData("Married couples only.", "Married couples only", "marital_status", Severity.Blocking)]
    [InlineData("Perfect for a straight couple.", "straight couple", "sexual_orientation", Severity.Blocking)]
    [InlineData("No military tenants.", "No military", "military_or_veteran_status", Severity.Blocking)]
    public void Lexicon_FindsThePhrase_WithItsClassSpanAndARewrite(string text, string phrase, string protectedClass, Severity severity)
    {
        var finding = Assert.Single(Lexicon.Match(text));

        Assert.Equal(CheckType.FairHousing, finding.CheckType);
        Assert.Equal(FindingSource.Lexicon, finding.Source);
        Assert.Equal(severity, finding.Severity);
        Assert.Equal(phrase, finding.Phrase);
        Assert.Equal(phrase, text[finding.SpanStart!.Value..finding.SpanEnd!.Value]);
        Assert.Contains(protectedClass, finding.RuleId, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(finding.Reason));
        Assert.False(string.IsNullOrWhiteSpace(finding.SuggestedFix));

        // The rewrite, applied, is clean.
        var rewritten = text[..finding.SpanStart.Value] + finding.SuggestedFix + text[finding.SpanEnd.Value..];
        Assert.Empty(Lexicon.Match(rewritten));
    }

    [Theory]
    [InlineData("A spacious family room opens to the kitchen.")]
    [InlineData("The primary bedroom has a walk-in closet.")]
    [InlineData("Across the street from a park, with a view of the hills.")]
    [InlineData("Freshly painted white cabinets and a white-oak floor.")]
    [InlineData("Near shops, restaurants and the freeway.")]
    [InlineData("The listing agent will hold an open house on Sunday.")]
    public void Lexicon_IgnoresDescriptionsOfTheProperty(string text)
    {
        Assert.Empty(Lexicon.Match(text));
    }

    [Fact]
    public void Lexicon_IsCaseInsensitive_AndFindsEveryOccurrence()
    {
        var findings = Lexicon.Match("ADULTS ONLY. Truly adults only.");

        Assert.Equal(2, findings.Count);
        Assert.Equal([0, 19], findings.Select(finding => finding.SpanStart!.Value));
    }

    [Fact]
    public void Lexicon_FindingsAreInTextOrder_AndDeterministic()
    {
        const string text = "No children. Walking distance to church. Married couples only.";

        var first = Lexicon.Match(text);

        Assert.Equal(first.OrderBy(finding => finding.SpanStart).ToList(), first);
        Assert.Equal(first, Lexicon.Match(text));
    }

    /// <summary>
    /// The lexicon is policy, authored without counsel's review, and says so until it has been
    /// reviewed (research R36). Flipping this is a reviewed change, not a refactor.
    /// </summary>
    [Fact]
    public void Lexicon_RecordsItsVersion_AndThatItIsUnreviewed()
    {
        Assert.Equal("fair-housing.v1", Lexicon.Version);
        Assert.False(Lexicon.Reviewed);
    }

    [Fact]
    public void EveryRule_HasAClassAJurisdictionAnExplanationAndARewrite()
    {
        Assert.All(Lexicon.Rules, rule =>
        {
            Assert.Matches("^[a-z_]+$", rule.ProtectedClass);
            Assert.True(rule.Jurisdiction is "federal" or "CA", rule.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(rule.Rewrite));
        });
        Assert.Equal(Lexicon.Rules.Count, Lexicon.Rules.Select(rule => rule.Id).Distinct().Count());
    }
}
