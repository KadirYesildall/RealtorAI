namespace RealtorAI.Tests.Integration;

/// <summary>
/// T030 — FR-007: AI may not originate a measurement or a legal/jurisdictional fact.
///
/// <para>
/// These are the values an agent is professionally liable for and a buyer relies on: square
/// footage, lot size, year built, tax figures, HOA fees, school districts, zoning. A model
/// estimating any of them from photographs produces a number that looks authoritative and is
/// not. The database therefore rejects <c>ai_detected_from_photo</c> and <c>ai_suggested</c> on
/// those keys outright, so the restriction does not depend on prompt wording or on the pipeline
/// remembering to check.
/// </para>
/// </summary>
public sealed class FieldProvenanceCheckTests
{
    /// <summary>
    /// The protected keys, copied from <c>listing_fields_no_ai_measurements</c> in
    /// 0005_listings.sql. Duplicated deliberately: if the migration's list is narrowed, this
    /// test fails and the change has to be argued for rather than slipping through.
    /// </summary>
    public static TheoryData<string> ProtectedResoKeys { get; } = new()
    {
        "LivingArea",
        "LivingAreaSquareFeet",
        "LotSizeSquareFeet",
        "LotSizeAcres",
        "YearBuilt",
        "TaxAnnualAmount",
        "TaxYear",
        "AssociationFee",
        "AssociationFeeFrequency",
        "HighSchoolDistrict",
        "ElementarySchoolDistrict",
        "MiddleOrJuniorSchoolDistrict",
        "Zoning",
    };

    /// <summary>
    /// T060: every field the public-record pre-fill fills (FR-008) is one a model may never
    /// fill (FR-007). Checked against the list above, which the theories below pin to the
    /// database constraint, so the pre-fill set cannot grow to include a field AI could write.
    /// </summary>
    [Fact]
    public void PublicRecordFields_AreAllProtectedFromAi()
    {
        var protectedKeys = ProtectedResoKeys.Cast<object[]>().Select(row => (string)row[0]).ToHashSet(StringComparer.Ordinal);

        Assert.All(
            Modules.Listings.PropertyData.PublicRecordFields.Keys,
            key => Assert.Contains(key, protectedKeys));
    }

    [Theory]
    [MemberData(nameof(ProtectedResoKeys))]
    public async Task ProtectedKey_WithAiDetectedProvenance_IsRefused(string resoFieldKey)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            FieldInsert,
            "listing_fields_no_ai_measurements",
            Guid.NewGuid(), listingId, resoFieldKey, "1850", "ai_detected_from_photo");

        await transaction.RollbackAsync();
    }

    [Theory]
    [MemberData(nameof(ProtectedResoKeys))]
    public async Task ProtectedKey_WithAiSuggestedProvenance_IsRefused(string resoFieldKey)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            FieldInsert,
            "listing_fields_no_ai_measurements",
            Guid.NewGuid(), listingId, resoFieldKey, "1850", "ai_suggested");

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("agent_entered")]
    [InlineData("public_record")]
    public async Task ProtectedKey_FromAHumanOrRecordSource_IsAccepted(string provenance)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        var affected = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            FieldInsert,
            Guid.NewGuid(), listingId, "LivingArea", "1850", provenance);

        Assert.Equal(1, affected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The constraint must not be a blanket ban on AI provenance. Observing that a kitchen has
    /// granite counters is exactly what photo analysis is for, and blocking it would make the
    /// feature pointless — so this verifies the restriction is scoped to the keys that carry
    /// liability.
    /// </summary>
    [Theory]
    [InlineData("KitchenCountertopMaterial")]
    [InlineData("PoolPrivateYN")]
    [InlineData("FireplaceYN")]
    public async Task ObservableFeature_WithAiProvenance_IsAccepted(string resoFieldKey)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        var affected = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            FieldInsert,
            Guid.NewGuid(), listingId, resoFieldKey, "true", "ai_detected_from_photo");

        Assert.Equal(1, affected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// An AI-derived value must also start unconfirmed. <c>confirmed</c> defaults to false and
    /// a separate constraint requires that confirming a field records who did it and when, so
    /// "confirmed by nobody" is not a representable state (FR-008).
    /// </summary>
    [Fact]
    public async Task ConfirmingAField_WithoutRecordingWhoAndWhen_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            """
            insert into public.listing_fields
                (id, listing_id, reso_field_key, value, provenance, confirmed)
            values ($1, $2, 'FireplaceYN', 'true'::jsonb, 'ai_detected_from_photo', true)
            """,
            "listing_fields_confirmation_complete",
            Guid.NewGuid(),
            listingId);

        await transaction.RollbackAsync();
    }

    private const string FieldInsert = """
        insert into public.listing_fields
            (id, listing_id, reso_field_key, value, provenance)
        values ($1, $2, $3, to_jsonb($4::text), $5::provenance)
        """;
}
