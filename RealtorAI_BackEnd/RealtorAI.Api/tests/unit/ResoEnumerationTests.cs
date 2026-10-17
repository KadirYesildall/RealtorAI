using Libs.Reso;

namespace Tests.Unit;

/// <summary>
/// T107 — RESO enumerated-value validation for the Slice A fields (Story 3 scenario 1; research
/// R3).
///
/// <para>
/// A structured field the AI fills holds only values the MLS will accept: the Data Dictionary's
/// standard lookup values, narrowed or extended by the MLS's own mapping. Anything else is
/// dropped, never stored under a spelling no MLS recognises.
/// </para>
/// </summary>
public sealed class ResoEnumerationTests
{
    private static readonly string[] Enumerated = ["InteriorFeatures", "Appliances", "Flooring", "ParkingFeatures", "PoolFeatures", "View"];

    [Fact]
    public void TheSixEnumeratedSliceAFields_EachHaveStandardValues()
    {
        Assert.Equal(Enumerated.Order(), ResoLookups.EnumeratedFields.Order());
        Assert.All(Enumerated, field => Assert.NotEmpty(ResoLookups.Standard(field)));
    }

    /// <summary>The remarks are the other two Slice A fields, and they are free text.</summary>
    [Theory]
    [InlineData("PublicRemarks")]
    [InlineData("PrivateRemarks")]
    [InlineData("LivingArea")]
    public void FreeTextAndUnlistedFields_AreNotEnumerated(string field)
    {
        Assert.False(ResoLookups.IsEnumerated(field));
        Assert.Empty(ResoLookups.Standard(field));
    }

    [Fact]
    public void EveryStandardValue_IsALookupNameShapedLikeOne()
    {
        Assert.All(
            Enumerated.SelectMany(ResoLookups.Standard),
            value => Assert.Matches("^[A-Z][A-Za-z0-9]*$", value));
        Assert.All(Enumerated, field => Assert.Equal(ResoLookups.Standard(field).Count, ResoLookups.Standard(field).Distinct().Count()));
    }

    /// <summary>
    /// The seed was transcribed without the published dictionary to hand, and says so until it is
    /// checked against it (research R35). Flipping this is a reviewed change, not a refactor.
    /// </summary>
    [Fact]
    public void TheSeed_RecordsItsDictionaryVersion_AndThatItIsUnverified()
    {
        Assert.Equal("2.0", ResoLookups.DataDictionaryVersion);
        Assert.False(ResoLookups.Verified);
    }

    [Fact]
    public void Permitted_KeepsStandardValuesInOrder_AndDropsEverythingElse()
    {
        var kept = ResoLookups.Permitted("InteriorFeatures", ["WetBar", "Disco Ball", "wetbar", "VaultedCeilings", "WetBar"], ResoLookupMapping.None);

        Assert.Equal(["WetBar", "VaultedCeilings"], kept);
    }

    [Fact]
    public void Permitted_ForAFieldThatIsNotEnumerated_IsEmpty()
    {
        Assert.Empty(ResoLookups.Permitted("PublicRemarks", ["Anything"], ResoLookupMapping.None));
    }

    /// <summary>An MLS restricting a lookup narrows it to the values it lists.</summary>
    [Fact]
    public void AnMlsRestriction_NarrowsTheStandardValues()
    {
        var mapping = ResoLookupMapping.Parse("""{ "Flooring": { "only": ["Hardwood", "Tile"] } }""");

        Assert.Equal(["Hardwood"], ResoLookups.Permitted("Flooring", ["Hardwood", "Carpet"], mapping));
        Assert.Equal(["Hardwood", "Tile"], mapping.Allowed("Flooring"));
    }

    /// <summary>An MLS extending a lookup adds its local values to the standard ones.</summary>
    [Fact]
    public void AnMlsExtension_AddsLocalValues()
    {
        var mapping = ResoLookupMapping.Parse("""{ "View": { "extra": ["Catalina"] } }""");

        Assert.Equal(["Catalina", "Ocean"], ResoLookups.Permitted("View", ["Catalina", "Ocean", "Moon"], mapping));
        Assert.Contains("Catalina", mapping.Allowed("View"));
        Assert.Contains("Ocean", mapping.Allowed("View"));
    }

    /// <summary>A restriction to values outside the standard set cannot smuggle them in.</summary>
    [Fact]
    public void ARestrictionToNonStandardValues_PermitsNothingExtra()
    {
        var mapping = ResoLookupMapping.Parse("""{ "Flooring": { "only": ["Hardwood", "Lava"] } }""");

        Assert.Equal(["Hardwood"], mapping.Allowed("Flooring"));
    }

    [Fact]
    public void TheEmptyMapping_LeavesTheStandardValues()
    {
        Assert.Equal(ResoLookups.Standard("Appliances"), ResoLookupMapping.Parse("{}").Allowed("Appliances"));
    }

    /// <summary>A malformed mapping fails closed: the profile is wrong, and nothing is guessed from it.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "Flooring": ["Hardwood"] }""")]
    [InlineData("""{ "Flooring": { "only": "Hardwood" } }""")]
    [InlineData("""{ "Flooring": { "sometimes": ["Hardwood"] } }""")]
    [InlineData("not json")]
    public void AMalformedMapping_IsRefused(string json)
    {
        Assert.Throws<FormatException>(() => ResoLookupMapping.Parse(json));
    }
}
