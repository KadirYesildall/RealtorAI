using Libs.Compliance;

namespace Tests.Unit;

/// <summary>
/// T121 — risky claims: superlatives, guarantees, school claims, and measurements with no
/// verified source (FR-018; Story 4 scenario 3).
/// </summary>
public sealed class RiskyClaimTests
{
    [Theory]
    [InlineData("Enjoy the best schools in the district.", "best schools")]
    [InlineData("Zoned for Lincoln Elementary.", "Zoned for Lincoln Elementary")]
    [InlineData("Top-rated schools nearby.", "Top-rated schools")]
    [InlineData("This home will appreciate quickly.", "will appreciate")]
    [InlineData("A guaranteed investment.", "guaranteed")]
    [InlineData("The lot is flood-free.", "flood-free")]
    [InlineData("The finest home on the street.", "finest")]
    [InlineData("The best value in Irvine.", "best value")]
    public void Check_FindsTheClaim_WithASuggestedSubstitute(string text, string phrase)
    {
        var finding = Assert.Single(RiskyClaimChecker.Check(text, []));

        Assert.Equal(CheckType.RiskyClaim, finding.CheckType);
        Assert.Equal(FindingSource.Deterministic, finding.Source);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(phrase, text[finding.SpanStart!.Value..finding.SpanEnd!.Value]);
        Assert.NotNull(finding.SuggestedFix);
    }

    /// <summary>A figure is a claim the agent is liable for; one that matches a sourced field is not risky.</summary>
    [Fact]
    public void Check_AMeasurementWithoutASource_IsRisky_AndOneMatchingASourcedFieldIsNot()
    {
        const string text = "It offers 2,840 square feet of living space.";

        var finding = Assert.Single(RiskyClaimChecker.Check(text, []));
        Assert.Equal("2,840 square feet", text[finding.SpanStart!.Value..finding.SpanEnd!.Value]);
        Assert.Contains("measurement", finding.RuleId, StringComparison.Ordinal);

        Assert.Empty(RiskyClaimChecker.Check(text, [2840m]));
        Assert.Single(RiskyClaimChecker.Check("It offers 3,100 sq ft of living space.", [2840m]));
    }

    [Theory]
    [InlineData("A bright kitchen with a breakfast bar.")]
    [InlineData("Close to the best restaurants in town is a matter of taste.")]
    [InlineData("Four bedrooms and three baths.")]
    public void Check_LeavesOrdinaryDescriptionAlone(string text)
    {
        // "best restaurants" is about the area, not a claim about the property or its schools.
        Assert.DoesNotContain(RiskyClaimChecker.Check(text, []), finding => finding.RuleId.Contains("school", StringComparison.Ordinal));
        Assert.DoesNotContain(RiskyClaimChecker.Check(text, []), finding => finding.RuleId.Contains("measurement", StringComparison.Ordinal));
    }
}
