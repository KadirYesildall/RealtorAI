using Modules.Media;
using Pipeline;
using Pipeline.Steps;

namespace Tests.Unit;

/// <summary>T093/T097/T098 — the pipeline's deterministic rules.</summary>
public sealed class PipelineRulesTests
{
    private static readonly QualityThresholds Thresholds = new(100, 0.25, 0.85, 0.05, 3.0, 0.97);

    private static QualityMeasurements Measured(double blur = 250, double luminance = 0.5, double clipped = 0.01, double? tilt = null) =>
        new(1280, 960, blur, luminance, clipped, 0.01, tilt);

    [Fact]
    public void Quality_AGoodPhoto_HasNoFlags()
    {
        Assert.Empty(QualityRules.Flags(Measured(), 4000, 3000, 1024, 768, Thresholds));
    }

    [Theory]
    [InlineData(50, 0.5, 0.01, null, "blurry")]
    [InlineData(250, 0.1, 0.01, null, "too_dark")]
    [InlineData(250, 0.9, 0.01, null, "overexposed")]
    [InlineData(250, 0.5, 0.2, null, "overexposed")]
    [InlineData(250, 0.5, 0.01, 5.0, "tilted_horizon")]
    [InlineData(250, 0.5, 0.01, -5.0, "tilted_horizon")]
    public void Quality_EachThreshold_RaisesItsFlag(double blur, double luminance, double clipped, double? tilt, string flag)
    {
        Assert.Contains(flag, QualityRules.Flags(Measured(blur, luminance, clipped, tilt), 4000, 3000, null, null, Thresholds));
    }

    /// <summary>Resolution is the original's, not the ~1280px normalized copy the sidecar measured.</summary>
    [Fact]
    public void Quality_Resolution_IsJudgedOnTheOriginal()
    {
        Assert.DoesNotContain("below_min_resolution", QualityRules.Flags(Measured(), 4000, 3000, 2048, 1536, Thresholds));
        Assert.Contains("below_min_resolution", QualityRules.Flags(Measured(), 1600, 1200, 2048, 1536, Thresholds));
    }

    /// <summary>An unverified rule profile has no minimum, so nothing is flagged against a guess.</summary>
    [Fact]
    public void Quality_WithNoVerifiedMinimum_DoesNotFlagResolution()
    {
        Assert.DoesNotContain("below_min_resolution", QualityRules.Flags(Measured(), 200, 100, null, null, Thresholds));
    }

    [Fact]
    public void NearDuplicates_FlagTheLaterPhotoOfEachPair()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        var order = new Dictionary<Guid, int> { [first] = 0, [second] = 1, [third] = 2 };

        var flagged = NearDuplicateDetector.PhotosToFlag(
            [new SimilarPair(second, first, 0.99), new SimilarPair(first, third, 0.5)],
            order,
            0.97);

        Assert.Equal([second], flagged);
    }

    private static ClassifiedPhoto Photo(string room, int order, bool recommended = false, string[]? quality = null, string[]? content = null) =>
        new(Guid.NewGuid(), order, room, 0.8m, recommended, quality ?? [], content ?? []);

    [Fact]
    public void Order_IsAWalkThroughFromTheFront()
    {
        var bath = Photo("bathroom", 0);
        var front = Photo("exterior_front", 1);
        var kitchen = Photo("kitchen", 2);
        var living = Photo("living_room", 3);

        var suggestions = CoverAndOrderStep.Suggest([bath, front, kitchen, living]);

        Assert.Equal([front.PhotoId, living.PhotoId, kitchen.PhotoId, bath.PhotoId],
            suggestions.OrderBy(suggestion => suggestion.OrderRecommendation).Select(suggestion => suggestion.PhotoId));
        Assert.Equal(front.PhotoId, suggestions.Single(suggestion => suggestion.CoverRecommended).PhotoId);
        Assert.Equal("rule:cover:exterior_front", suggestions.Single(suggestion => suggestion.CoverRecommended).Reason);
        Assert.Equal("rule:order:kitchen", suggestions.Single(suggestion => suggestion.PhotoId == kitchen.PhotoId).Reason);
    }

    /// <summary>A blurry front, or one showing a house number, is not the first impression to suggest.</summary>
    [Fact]
    public void Cover_SkipsAFrontWithAnIssue()
    {
        var blurryFront = Photo("exterior_front", 0, quality: ["blurry"]);
        var flaggedFront = Photo("exterior_front", 1, content: ["house_number"]);
        var recommendedKitchen = Photo("kitchen", 2, recommended: true);

        var cover = CoverAndOrderStep.Suggest([blurryFront, flaggedFront, recommendedKitchen]).Single(suggestion => suggestion.CoverRecommended);

        Assert.Equal(recommendedKitchen.PhotoId, cover.PhotoId);
        Assert.Equal("rule:cover:model_recommended", cover.Reason);
    }

    [Fact]
    public void Cover_WithEveryPhotoFlagged_IsNotSuggested()
    {
        var suggestions = CoverAndOrderStep.Suggest([Photo("kitchen", 0, quality: ["too_dark"])]);

        Assert.DoesNotContain(suggestions, suggestion => suggestion.CoverRecommended);
    }
}
