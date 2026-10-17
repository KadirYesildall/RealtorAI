using Libs.Compliance;

namespace Tests.Unit;

/// <summary>
/// T123 — AB 723 photo integrity (FR-020; Story 4 scenarios 5, 6; research R4).
///
/// <para>
/// This system never alters a pixel, so the only altered photos are ones the agent altered
/// elsewhere. Each must carry all three parts — the altered flag with its type, a label from the
/// MLS's approved list, and the unaltered original beside it — and an alteration the MLS does not
/// permit is blocking, with nothing to acknowledge.
/// </para>
/// </summary>
public sealed class PhotoIntegrityTests
{
    private static readonly MlsRules Rules = new(
        Verified: true,
        PublicRemarksMaxChars: 500,
        PrivateRemarksMaxChars: 300,
        PhotoMinWidth: 1024,
        PhotoMinHeight: 768,
        PhotoMinCount: 1,
        PhotoMaxCount: 50,
        ForbiddenInPublicRemarks: [],
        RequiredFields: [],
        AllowedAlterationTypes: ["virtual_staging"],
        DisclosureLabels: ["Virtually Staged"]);

    private static readonly Guid OriginalId = Guid.NewGuid();
    private static readonly Guid AlteredId = Guid.NewGuid();

    private static PhotoFacts Original(int order = 0) => new(OriginalId, order, false, null, null, null, []);

    private static PhotoFacts Altered(int order = 1, string? type = "virtual_staging", string? label = "Virtually Staged", Guid? paired = null) =>
        new(AlteredId, order, true, type, label, paired ?? OriginalId, []);

    private static IReadOnlyList<Finding> Check(PhotoFacts photo, params PhotoFacts[] all) =>
        PhotoIntegrityChecker.Check(photo, all, Rules);

    [Fact]
    public void AnUnalteredPhoto_WithNoFlags_Passes()
    {
        Assert.Empty(Check(Original(), Original()));
    }

    [Fact]
    public void ACompleteDeclaration_WithTheOriginalAdjacent_Passes()
    {
        var altered = Altered();

        Assert.Empty(Check(altered, Original(), altered));
    }

    /// <summary>The scenario T123 names: an alteration other than virtual staging is blocking.</summary>
    [Fact]
    public void ANonStagingAlteration_IsBlocking()
    {
        var altered = Altered(type: "other");

        var finding = Assert.Single(Check(altered, Original(), altered));

        Assert.Equal("photo.alteration_not_permitted", finding.RuleId);
        Assert.Equal(CheckType.PhotoIntegrity, finding.CheckType);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Fact]
    public void AnIncompleteDeclaration_IsBlocking()
    {
        var altered = Altered(type: null, label: null);

        var finding = Assert.Single(Check(altered, Original(), altered));

        Assert.Equal("photo.alteration_incomplete", finding.RuleId);
        Assert.Equal(Severity.Blocking, finding.Severity);
    }

    [Fact]
    public void ALabelOutsideTheApprovedList_IsBlocking()
    {
        var altered = Altered(label: "Digitally Enhanced");

        Assert.Equal("photo.label_not_approved", Assert.Single(Check(altered, Original(), altered)).RuleId);
    }

    /// <summary>With no approved label recorded (CRMLS until T161) nothing can be declared, so it fails closed.</summary>
    [Fact]
    public void WithNoApprovedLabels_ADeclaredAlteration_IsBlocking()
    {
        var altered = Altered();

        var finding = Assert.Single(PhotoIntegrityChecker.Check(altered, [Original(), altered], Rules with { DisclosureLabels = [] }));

        Assert.Equal("photo.label_not_approved", finding.RuleId);
    }

    [Fact]
    public void TheOriginalNotAdjacent_IsBlocking()
    {
        var altered = Altered(order: 2);
        var between = new PhotoFacts(Guid.NewGuid(), 1, false, null, null, null, []);

        Assert.Equal("photo.original_not_adjacent", Assert.Single(Check(altered, Original(), between, altered)).RuleId);
    }

    [Fact]
    public void TheOriginalMissingFromTheListing_IsBlocking()
    {
        var altered = Altered();

        Assert.Equal("photo.original_not_adjacent", Assert.Single(Check(altered, altered)).RuleId);
    }

    /// <summary>Story 2 scenario 5: signs of prior alteration ask the agent to confirm or declare.</summary>
    [Fact]
    public void SignsOfPriorAlteration_OnAnUndeclaredPhoto_AreAWarning()
    {
        var photo = new PhotoFacts(Guid.NewGuid(), 0, false, null, null, null, ["possible_prior_alteration"]);

        var finding = Assert.Single(Check(photo, photo));

        Assert.Equal("photo.possible_alteration", finding.RuleId);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    /// <summary>Story 2 scenario 4: content to resolve before external publishing.</summary>
    [Theory]
    [InlineData("people_visible")]
    [InlineData("personal_documents")]
    [InlineData("house_number")]
    [InlineData("license_plate")]
    [InlineData("watermark_or_branding")]
    public void AContentFlag_IsAWarningToResolve(string flag)
    {
        var photo = new PhotoFacts(Guid.NewGuid(), 0, false, null, null, null, [flag]);

        var finding = Assert.Single(Check(photo, photo));

        Assert.Equal($"photo.content.{flag}", finding.RuleId);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void WithNoProfile_AnAlteredPhoto_FailsClosed()
    {
        var altered = Altered();

        var finding = Assert.Single(PhotoIntegrityChecker.Check(altered, [Original(), altered], null));

        Assert.Equal(Severity.Blocking, finding.Severity);
    }
}
