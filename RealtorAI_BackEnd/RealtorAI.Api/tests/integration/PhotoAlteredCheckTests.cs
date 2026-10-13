using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T029 — FR-020 / California AB 723: a photo the agent declares as altered must carry the
/// full disclosure, and an unaltered photo must not carry a partial one.
///
/// <para>
/// The disclosure is three attributes, not one flag: what kind of alteration
/// (<c>alteration_type</c>), the label shown to consumers (<c>disclosure_label</c>), and a
/// reference to the unaltered original (<c>paired_original_id</c>). All three or none — a photo
/// marked altered with no original to compare against is an unverifiable claim, and a labelled
/// photo with no stated alteration type discloses nothing useful.
/// </para>
/// </summary>
public sealed class PhotoAlteredCheckTests
{
    [Fact]
    public async Task AlteredPhoto_WithoutAlterationType_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var (listingId, originalId) = await InsertListingWithOriginalPhotoAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            PhotoInsert,
            "photos_altered_requires_disclosure",
            Guid.NewGuid(), listingId, 1, true, null, "Photo digitally altered", originalId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AlteredPhoto_WithoutDisclosureLabel_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var (listingId, originalId) = await InsertListingWithOriginalPhotoAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            PhotoInsert,
            "photos_altered_requires_disclosure",
            Guid.NewGuid(), listingId, 1, true, "virtual_staging", null, originalId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AlteredPhoto_WithoutAPairedOriginal_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var (listingId, _) = await InsertListingWithOriginalPhotoAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            PhotoInsert,
            "photos_altered_requires_disclosure",
            Guid.NewGuid(), listingId, 1, true, "virtual_staging", "Photo digitally altered", null);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The complement: an unaltered photo may not carry disclosure attributes either. Without
    /// this, a photo could be labelled "virtually staged" while claiming to be unaltered, which
    /// is a disclosure that contradicts itself.
    /// </summary>
    [Fact]
    public async Task UnalteredPhoto_CarryingADisclosureLabel_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var (listingId, _) = await InsertListingWithOriginalPhotoAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            PhotoInsert,
            "photos_unaltered_has_no_disclosure",
            Guid.NewGuid(), listingId, 1, false, null, "Photo digitally altered", null);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AlteredPhoto_WithTheCompleteDisclosure_IsAccepted()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var (listingId, originalId) = await InsertListingWithOriginalPhotoAsync(connection, transaction);

        var affected = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            PhotoInsert,
            Guid.NewGuid(), listingId, 1, true, "virtual_staging", "Photo digitally altered", originalId);

        Assert.Equal(1, affected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Only formats the pipeline can actually decode are accepted. HEIC is in the list because
    /// it is the iPhone default and therefore the most likely real upload — and the imaging
    /// library was changed to Magick.NET specifically because the original choice could not
    /// decode it (research R17).
    /// </summary>
    [Fact]
    public async Task UnsupportedMimeType_IsRefused()
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
            insert into public.photos
                (id, listing_id, original_storage_key, checksum_sha256, mime_type, order_index)
            values ($1, $2, 'originals/x.tiff', repeat('a', 64), 'image/tiff', 0)
            """,
            "photos_supported_mime",
            Guid.NewGuid(),
            listingId);

        await transaction.RollbackAsync();
    }

    private const string PhotoInsert = """
        insert into public.photos
            (id, listing_id, original_storage_key, checksum_sha256, mime_type, order_index,
             altered, alteration_type, disclosure_label, paired_original_id)
        values ($1, $2, 'originals/' || $1 || '.jpg', md5(random()::text) || md5(random()::text),
                'image/jpeg', $3, $4, $5::alteration_type, $6, $7)
        """;

    private static async Task<(Guid ListingId, Guid OriginalPhotoId)> InsertListingWithOriginalPhotoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        var originalId = Guid.NewGuid();

        await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            PhotoInsert,
            originalId, listingId, 0, false, null, null, null);

        return (listingId, originalId);
    }
}
