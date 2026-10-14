using System.Text.Json;
using Modules.Listings.PropertyData;

namespace Tests.Unit;

/// <summary>
/// T060 — the public-record pre-fill boundary (FR-007, FR-008, research R5).
/// </summary>
public sealed class PropertyDataProviderTests
{
    private static readonly PropertyAddress Address = new("1234 Ocean View Dr", null, "Laguna Beach", "CA", "92651");

    [Fact]
    public async Task NullProvider_ReportsNotConfigured()
    {
        var result = await new NullPropertyDataProvider().LookupAsync(Address, default);

        Assert.IsType<PropertyDataLookup.NotConfigured>(result);
    }

    /// <summary>The FR-008 pre-fill set: square footage, lot size, year built, tax.</summary>
    [Fact]
    public void PublicRecordFields_AreTheFr008Set()
    {
        Assert.Equal(
            ["LivingArea", "LotSizeSquareFeet", "YearBuilt", "TaxAnnualAmount"],
            PublicRecordFields.Keys);
    }

    // That every pre-fill field is also an FR-007 field the database refuses from a model is
    // asserted against the database's own list, in the integration suite's
    // FieldProvenanceCheckTests.PublicRecordFields_AreAllProtectedFromAi.

    /// <summary>FR-008 requires the source and retrieval date; a found record without them cannot exist.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AFoundRecord_RequiresItsSource(string source)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PropertyDataLookup.Found(source, DateTimeOffset.UtcNow, []));
    }

    [Fact]
    public void AFoundRecord_RejectsTheSameFieldTwice()
    {
        var value = JsonDocument.Parse("1850").RootElement;

        Assert.Throws<ArgumentException>(() => new PropertyDataLookup.Found(
            "County Assessor",
            DateTimeOffset.UtcNow,
            [new PublicRecordField("LivingArea", value), new PublicRecordField("LivingArea", value)]));
    }

    [Fact]
    public void AFoundRecord_KeepsItsSourceDateAndFields()
    {
        var retrieved = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var field = new PublicRecordField("YearBuilt", JsonDocument.Parse("1987").RootElement);

        var found = new PropertyDataLookup.Found("County Assessor", retrieved, [field]);

        Assert.Equal("County Assessor", found.SourceName);
        Assert.Equal(retrieved, found.RetrievedAt);
        Assert.Equal(field, Assert.Single(found.Fields));
    }
}
