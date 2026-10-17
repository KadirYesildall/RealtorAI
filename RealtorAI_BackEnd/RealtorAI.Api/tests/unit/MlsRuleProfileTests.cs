using Libs.Compliance;

namespace Tests.Unit;

/// <summary>
/// T122 — the MLS rule profile checks, and that an incomplete profile fails closed (FR-019;
/// Story 4 scenario 4; research R2).
/// </summary>
public sealed class MlsRuleProfileTests
{
    private static readonly RemarksDetector[] AllDetectors =
    [
        new("phone_number", Severity.Blocking),
        new("email_address", Severity.Blocking),
        new("url", Severity.Blocking),
        new("agent_name", Severity.Blocking),
        new("brokerage_name", Severity.Blocking),
    ];

    private static readonly RemarksNames Names = new(["Casey Agent"], ["Sunrise Realty"]);

    private static MlsRules Complete(
        int? publicMax = 500,
        int? privateMax = 300,
        int? minCount = 1,
        int? maxCount = 50,
        string[]? required = null) =>
        new(
            Verified: true,
            PublicRemarksMaxChars: publicMax,
            PrivateRemarksMaxChars: privateMax,
            PhotoMinWidth: 1024,
            PhotoMinHeight: 768,
            PhotoMinCount: minCount,
            PhotoMaxCount: maxCount,
            ForbiddenInPublicRemarks: AllDetectors,
            RequiredFields: required ?? ["LivingArea", "YearBuilt"],
            AllowedAlterationTypes: ["virtual_staging"],
            DisclosureLabels: ["Virtually Staged"]);

    // ═════════════ Fail closed ═════════════

    [Fact]
    public void NoProfile_IsOneBlockingIncompleteFinding()
    {
        var finding = Assert.Single(MlsRuleEngine.CheckListing(null, [], photoCount: 5));

        Assert.Equal("mls.profile_incomplete", finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Fact]
    public void AnUnverifiedProfile_FailsClosed_EvenWithValues()
    {
        var finding = Assert.Single(MlsRuleEngine.CheckListing(Complete() with { Verified = false }, [("LivingArea", true), ("YearBuilt", true)], 5));

        Assert.Equal("mls.profile_incomplete", finding.RuleId);
    }

    /// <summary>Each missing value is named, so whoever completes the profile knows what is left.</summary>
    [Fact]
    public void AVerifiedProfileMissingAValue_NamesWhatIsMissing()
    {
        var finding = Assert.Single(MlsRuleEngine.CheckListing(Complete(privateMax: null, maxCount: null), [("LivingArea", true), ("YearBuilt", true)], 5));

        Assert.Equal("mls.profile_incomplete", finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
        Assert.Contains("private_remarks_max_chars", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("photo_max_count", finding.Reason, StringComparison.Ordinal);
    }

    // ═════════════ Listing rules ═════════════

    [Fact]
    public void ACompleteListing_PassesTheListingRules()
    {
        Assert.Empty(MlsRuleEngine.CheckListing(Complete(), [("LivingArea", true), ("YearBuilt", true)], 5));
    }

    [Fact]
    public void ARequiredFieldMissingOrUnconfirmed_IsBlocking()
    {
        var findings = MlsRuleEngine.CheckListing(Complete(), [("LivingArea", false)], 5);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(Severity.Blocking, finding.Severity));
        Assert.Contains(findings, finding => finding.RuleId == "mls.required_field_missing" && finding.Reason.Contains("YearBuilt", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.RuleId == "mls.required_field_unconfirmed" && finding.Reason.Contains("LivingArea", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "mls.photo_count_below_minimum")]
    [InlineData(51, "mls.photo_count_above_maximum")]
    public void ThePhotoCountOutsideTheProfilesBounds_IsBlocking(int count, string rule)
    {
        var finding = Assert.Single(MlsRuleEngine.CheckListing(Complete(), [("LivingArea", true), ("YearBuilt", true)], count));

        Assert.Equal(rule, finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Theory]
    [InlineData(1023, 768)]
    [InlineData(1024, 700)]
    public void APhotoBelowTheMinimumResolution_IsBlocking(int width, int height)
    {
        var finding = Assert.Single(MlsRuleEngine.CheckPhoto(width, height, Complete()));

        Assert.Equal("mls.photo_below_min_resolution", finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Fact]
    public void APhotoAtTheMinimum_Passes_AndAnIncompleteProfileLeavesItToTheListingFinding()
    {
        Assert.Empty(MlsRuleEngine.CheckPhoto(1024, 768, Complete()));
        Assert.Empty(MlsRuleEngine.CheckPhoto(10, 10, null));
    }

    // ═════════════ Remarks ═════════════

    [Fact]
    public void PublicRemarksOverTheLimit_AreBlocking()
    {
        var finding = Assert.Single(MlsRuleEngine.CheckRemarks("public_remarks", new string('a', 501), Complete(), Names));

        Assert.Equal("mls.remarks_too_long", finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Theory]
    [InlineData("Call (949) 555-0123 today.", "(949) 555-0123", "mls.forbidden.phone_number")]
    [InlineData("Call 949.555.0123 today.", "949.555.0123", "mls.forbidden.phone_number")]
    [InlineData("Email casey@example.com for a tour.", "casey@example.com", "mls.forbidden.email_address")]
    [InlineData("See www.example.com for more.", "www.example.com", "mls.forbidden.url")]
    [InlineData("See https://example.com/tour for more.", "https://example.com/tour", "mls.forbidden.url")]
    [InlineData("Ask Casey Agent for details.", "Casey Agent", "mls.forbidden.agent_name")]
    [InlineData("Listed by Sunrise Realty.", "Sunrise Realty", "mls.forbidden.brokerage_name")]
    public void ContactDetailsAndNamesInPublicRemarks_AreFoundWithTheirSpan(string text, string phrase, string rule)
    {
        var finding = Assert.Single(MlsRuleEngine.CheckRemarks("public_remarks", text, Complete(), Names));

        Assert.Equal(rule, finding.RuleId);
        Assert.Equal(CheckType.MlsRule, finding.CheckType);
        Assert.Equal(phrase, text[finding.SpanStart!.Value..finding.SpanEnd!.Value]);
    }

    /// <summary>The detectors are on and off, and their severity set, by the profile — data, not code.</summary>
    [Fact]
    public void ADetectorTheProfileDoesNotList_DoesNotRun_AndItsSeverityIsTheProfiles()
    {
        var rules = Complete() with { ForbiddenInPublicRemarks = [new RemarksDetector("url", Severity.Warning)] };

        Assert.Empty(MlsRuleEngine.CheckRemarks("public_remarks", "Call (949) 555-0123 today.", rules, Names));
        Assert.Equal(Severity.Warning, Assert.Single(MlsRuleEngine.CheckRemarks("public_remarks", "See www.example.com.", rules, Names)).Severity);
    }

    /// <summary>Contact details belong in private remarks, which only agents see.</summary>
    [Fact]
    public void PrivateRemarks_MayCarryContactDetails()
    {
        Assert.Empty(MlsRuleEngine.CheckRemarks("private_remarks", "Call Casey Agent at (949) 555-0123.", Complete(), Names));
    }

    /// <summary>On an unverified profile the length is not checked, but contact details still are.</summary>
    [Fact]
    public void Remarks_OnAnUnverifiedProfile_RunTheDetectorsButNotTheLimit()
    {
        var unverified = Complete() with { Verified = false };

        Assert.Empty(MlsRuleEngine.CheckRemarks("public_remarks", new string('a', 5000), unverified, Names));
        Assert.Equal("mls.forbidden.phone_number", Assert.Single(MlsRuleEngine.CheckRemarks("public_remarks", "Call (949) 555-0123.", unverified, Names)).RuleId);
    }

    [Fact]
    public void Remarks_WithNoProfile_LeaveItToTheListingFinding()
    {
        Assert.Empty(MlsRuleEngine.CheckRemarks("public_remarks", new string('a', 5000), null, Names));
    }
}
