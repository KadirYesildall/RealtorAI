using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Listings;
using Modules.Listings.PropertyData;

namespace Tests.Unit;

/// <summary>
/// T073 — the listing lifecycle, every pair of states (data-model.md "State machine").
/// </summary>
public sealed class ListingLifecycleTests
{
    private static readonly HashSet<(ListingStatus, ListingStatus)> Allowed =
    [
        (ListingStatus.Draft, ListingStatus.Analyzing),
        (ListingStatus.Draft, ListingStatus.ReadyForReview),
        (ListingStatus.Analyzing, ListingStatus.ReadyForReview),
        (ListingStatus.Analyzing, ListingStatus.Draft),
        (ListingStatus.ReadyForReview, ListingStatus.Approved),
        (ListingStatus.Approved, ListingStatus.ReadyForReview),
        (ListingStatus.Draft, ListingStatus.Archived),
        (ListingStatus.Analyzing, ListingStatus.Archived),
        (ListingStatus.ReadyForReview, ListingStatus.Archived),
        (ListingStatus.Approved, ListingStatus.Archived),
    ];

    public static TheoryData<ListingStatus, ListingStatus> EveryPair()
    {
        var data = new TheoryData<ListingStatus, ListingStatus>();

        foreach (var from in Enum.GetValues<ListingStatus>())
        {
            foreach (var to in Enum.GetValues<ListingStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Transition_IsPermittedExactlyWhereTheStateMachineDrawsIt(ListingStatus from, ListingStatus to)
    {
        var expected = Allowed.Contains((from, to));

        Assert.Equal(expected, ListingLifecycle.CanTransition(from, to));

        if (expected)
        {
            Assert.Equal(to, ListingLifecycle.Transition(from, to));
        }
        else
        {
            Assert.Throws<InvalidListingTransitionException>(() => ListingLifecycle.Transition(from, to));
        }
    }

    /// <summary>FR-067: archived is final — nothing leaves it, not even back to draft.</summary>
    [Fact]
    public void Archived_HasNoWayOut()
    {
        Assert.All(Enum.GetValues<ListingStatus>(), to => Assert.False(ListingLifecycle.CanTransition(ListingStatus.Archived, to)));
        Assert.False(ListingLifecycle.IsEditable(ListingStatus.Archived));
        Assert.Throws<InvalidListingTransitionException>(() => ListingLifecycle.AfterEdit(ListingStatus.Archived));
    }

    /// <summary>FR-025: an approval covers content that an edit replaces.</summary>
    [Fact]
    public void AfterEdit_ReturnsAnApprovedListingToReview_AndLeavesOthersAlone()
    {
        Assert.Equal(ListingStatus.ReadyForReview, ListingLifecycle.AfterEdit(ListingStatus.Approved));
        Assert.Equal(ListingStatus.Draft, ListingLifecycle.AfterEdit(ListingStatus.Draft));
        Assert.Equal(ListingStatus.Analyzing, ListingLifecycle.AfterEdit(ListingStatus.Analyzing));
        Assert.Equal(ListingStatus.ReadyForReview, ListingLifecycle.AfterEdit(ListingStatus.ReadyForReview));
    }
}

/// <summary>T073 — the six basic details.</summary>
public sealed class ListingBasicsRulesTests
{
    private static ListingBasicsInput Valid() => new(
        " 1420 Crestline Drive ", "Irvine", "CA", "92618", "SingleFamilyResidence", 1485000m, 4, 3.5m, "  ");

    [Fact]
    public void Validate_TrimsTextAndDropsBlankNotes()
    {
        var (basics, errors) = ListingBasicsRules.Validate(Valid());

        Assert.Empty(errors);
        Assert.Equal("1420 Crestline Drive", basics!.AddressLine1);
        Assert.Null(basics.AgentNotes);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(2.5, true)]
    [InlineData(99.5, true)]
    [InlineData(2.25, false)]
    [InlineData(2.3, false)]
    [InlineData(100.0, false)]
    [InlineData(-0.5, false)]
    public void Validate_CountsBathroomsInHalves(double bathrooms, bool valid)
    {
        var (_, errors) = ListingBasicsRules.Validate(Valid() with { Bathrooms = (decimal)bathrooms });

        Assert.Equal(!valid, errors.ContainsKey("bathrooms"));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1485000.99, true)]
    [InlineData(9999999999.99, true)]
    [InlineData(10000000000.00, false)]
    [InlineData(1.005, false)]
    [InlineData(-1.0, false)]
    public void Validate_HoldsThePriceToDollarsAndCentsWithinTheColumn(double price, bool valid)
    {
        var (_, errors) = ListingBasicsRules.Validate(Valid() with { ListPrice = (decimal)price });

        Assert.Equal(!valid, errors.ContainsKey("listPrice"));
    }

    [Fact]
    public void Validate_ReportsEveryBrokenFieldAtOnce()
    {
        var (basics, errors) = ListingBasicsRules.Validate(new ListingBasicsInput(null, "", "ca", "1", "Castle", -1, -1, -1, null));

        Assert.Null(basics);
        Assert.Equal(
            ["addressLine1", "bathrooms", "bedrooms", "city", "listPrice", "postalCode", "propertyType", "state"],
            errors.Keys.Order(StringComparer.Ordinal));
    }
}

/// <summary>T074 — field provenance rules (FR-005 to FR-008).</summary>
public sealed class ListingFieldServiceTests
{
    private static readonly Dictionary<string, IReadOnlySet<string>> NoLookups = [];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (ValidatedField? Field, Dictionary<string, string[]> Errors) Validate(
        string key,
        string valueJson,
        string provenance,
        string? note = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? lookups = null) =>
        ListingFieldService.Validate(new FieldUpsertInput(key, Json(valueJson), provenance, note), lookups ?? NoLookups, currentYear: 2026);

    [Fact]
    public void EveryPublicRecordField_IsProtectedFromAiProvenance()
    {
        Assert.All(PublicRecordFields.Keys, key => Assert.Contains(key, ListingFieldService.MeasurementAndLegalKeys));
    }

    [Theory]
    [InlineData("ai_detected_from_photo")]
    [InlineData("ai_suggested")]
    public void Validate_RefusesAiProvenanceOnEveryProtectedKey(string provenance)
    {
        Assert.All(
            ListingFieldService.MeasurementAndLegalKeys,
            key => Assert.True(Validate(key, "1", provenance).Errors.ContainsKey("provenance"), key));
    }

    [Fact]
    public void Validate_AllowsAiProvenanceOnDescriptiveFields()
    {
        var (field, errors) = Validate("InteriorFeatures", """["Fireplace"]""", "ai_detected_from_photo");

        Assert.Empty(errors);
        Assert.Equal(Provenance.AiDetectedFromPhoto, field!.Provenance);
        Assert.True(ListingFieldService.RequiresConfirmation(field.Provenance));
    }

    [Theory]
    [InlineData("1600", true)]
    [InlineData("2029", true)]
    [InlineData("2030", false)]
    [InlineData("1599", false)]
    [InlineData("1998.5", false)]
    public void Validate_BoundsYearsByTheCurrentYear(string year, bool valid)
    {
        Assert.Equal(!valid, Validate("YearBuilt", year, "agent_entered").Errors.ContainsKey("value"));
    }

    [Fact]
    public void Validate_EnforcesTheRuleProfilesEnumeratedLookups()
    {
        var lookups = new Dictionary<string, IReadOnlySet<string>>
        {
            ["Cooling"] = new HashSet<string>(["CentralAir", "Zoned"], StringComparer.Ordinal),
        };

        Assert.Empty(Validate("Cooling", """["CentralAir"]""", "agent_entered", lookups: lookups).Errors);
        Assert.True(Validate("Cooling", """["Swamp"]""", "agent_entered", lookups: lookups).Errors.ContainsKey("value"));
        Assert.Empty(Validate("Heating", """["Swamp"]""", "agent_entered", lookups: lookups).Errors);
    }

    [Fact]
    public void Validate_RefusesObjectsAndNestedLists()
    {
        Assert.True(Validate("InteriorFeatures", """{"a":1}""", "agent_entered").Errors.ContainsKey("value"));
        Assert.True(Validate("InteriorFeatures", """[["a"]]""", "agent_entered").Errors.ContainsKey("value"));
    }

    [Fact]
    public void ChangesContent_IgnoresFormattingButNotValueOrSource()
    {
        var (field, _) = Validate("InteriorFeatures", """["Fireplace","WetBar"]""", "ai_suggested");

        Assert.False(ListingFieldService.ChangesContent("""[ "Fireplace", "WetBar" ]""", Provenance.AiSuggested, field!));
        Assert.True(ListingFieldService.ChangesContent("""["Fireplace"]""", Provenance.AiSuggested, field!));
        Assert.True(ListingFieldService.ChangesContent("""["Fireplace","WetBar"]""", Provenance.AgentEntered, field!));
    }

    [Fact]
    public void RequiredKeys_AreThePublicRecordSetPlusTheRuleProfiles()
    {
        var keys = ListingFieldService.RequiredKeys(["PublicRemarks"]);

        Assert.Equal(PublicRecordFields.Keys.Append("PublicRemarks").Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
    }
}

/// <summary>T078 — public-record pre-fill (FR-008; Story 1 scenarios 4 and 5).</summary>
public sealed class PrefillServiceTests
{
    private static readonly PropertyAddress Address = new("1420 Crestline Drive", null, "Irvine", "CA", "92618");

    private sealed class FixedProvider(Func<PropertyDataLookup> answer) : IPropertyDataProvider
    {
        public Task<PropertyDataLookup> LookupAsync(PropertyAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }

    private static PrefillService Service(Func<PropertyDataLookup> answer) =>
        new(new FixedProvider(answer), NullLogger<PrefillService>.Instance);

    [Fact]
    public async Task WithNoSource_PlansNothing()
    {
        var plan = await new PrefillService(new NullPropertyDataProvider(), NullLogger<PrefillService>.Instance)
            .PlanAsync(Address, default);

        Assert.Equal(PrefillOutcome.NotConfigured, plan.Outcome);
        Assert.Empty(plan.Fields);
    }

    [Fact]
    public async Task WithNoMatch_PlansNothing()
    {
        var plan = await Service(() => new PropertyDataLookup.NoMatch("County assessor")).PlanAsync(Address, default);

        Assert.Equal(PrefillOutcome.NoMatch, plan.Outcome);
        Assert.Empty(plan.Fields);
    }

    /// <summary>Scenario 4: each value is recorded with its source and retrieval date.</summary>
    [Fact]
    public async Task WithARecord_PlansEachFieldWithItsSourceAndDate()
    {
        var plan = await Service(() => new PropertyDataLookup.Found(
            "County assessor",
            new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero),
            [
                new PublicRecordField("LivingArea", JsonDocument.Parse("2840").RootElement),
                new PublicRecordField("YearBuilt", JsonDocument.Parse("1998").RootElement),
            ])).PlanAsync(Address, default);

        Assert.Equal(PrefillOutcome.Found, plan.Outcome);
        Assert.Equal(["LivingArea", "YearBuilt"], plan.Fields.Select(field => field.ResoFieldKey));
        Assert.All(plan.Fields, field => Assert.Equal("County assessor, retrieved 2026-10-13", field.SourceNote));
    }

    /// <summary>A provider's implausible value is left for the agent rather than stored as a public record.</summary>
    [Fact]
    public async Task WithAnImplausibleValue_DropsThatFieldOnly()
    {
        var plan = await Service(() => new PropertyDataLookup.Found(
            "County assessor",
            DateTimeOffset.UtcNow,
            [
                new PublicRecordField("LivingArea", JsonDocument.Parse("-5").RootElement),
                new PublicRecordField("LotSizeSquareFeet", JsonDocument.Parse("7200").RootElement),
            ])).PlanAsync(Address, default);

        Assert.Equal(["LotSizeSquareFeet"], plan.Fields.Select(field => field.ResoFieldKey));
    }

    /// <summary>A vendor outage must not stop an agent starting a listing.</summary>
    [Fact]
    public async Task WhenTheProviderFails_ReportsUnavailableAndPlansNothing()
    {
        var plan = await Service(() => throw new HttpRequestException("vendor down")).PlanAsync(Address, default);

        Assert.Equal(PrefillOutcome.Unavailable, plan.Outcome);
        Assert.Empty(plan.Fields);
    }
}
