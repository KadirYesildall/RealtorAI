namespace RealtorAI.Tests.Integration;

/// <summary>
/// T032 — a listing cannot be hard-deleted once it has an audit history (FR-065).
///
/// <para>
/// This is not a separate rule so much as a consequence of two others colliding, and the
/// collision is deliberate: <c>audit_events</c> is append-only, and
/// <c>audit_events.listing_id</c> references <c>listings</c> with NO ACTION. So deleting a
/// listing would require deleting its audit rows, which the trigger refuses. The audit trail
/// wins over cleanup.
/// </para>
/// <para>
/// In practice this applies to every listing that has ever existed, because creating one is
/// itself an audited action. Soft delete via <c>deleted_at</c> plus the <c>archived</c> status
/// is the only removal path, and the photo originals are purged out of band after three years
/// by a credential the application does not hold.
/// </para>
/// </summary>
public sealed class NoHardDeleteTests
{
    [Fact]
    public async Task DeletingAListingWithAuditHistory_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.audit_events (listing_id, actor_id, action)
            values ($1, $2, 'field_edit')
            """,
            listingId,
            SupabaseDatabase.AgentA);

        // Foreign key violation, from the audit reference — not a policy or a trigger on
        // listings itself. Asserting the constraint name keeps the test honest about which
        // mechanism is doing the work.
        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "delete from public.listings where id = $1",
            "audit_events",
            listingId);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Confirms the FK is NO ACTION rather than CASCADE. If it cascaded, deleting a listing
    /// would try to delete its audit rows — and while the append-only trigger would still catch
    /// that, the protection would then depend on the trigger alone. Reading the referential
    /// action directly makes the two mechanisms independently verified.
    /// </summary>
    [Fact]
    public async Task AuditReferenceToListings_UsesNoAction_NotCascade()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var deleteRule = await SupabaseDatabase.ScalarAsync<char?>(
            connection,
            transaction: null,
            """
            select con.confdeltype
            from pg_constraint con
            join pg_class child on child.oid = con.conrelid
            join pg_class parent on parent.oid = con.confrelid
            where con.contype = 'f'
              and child.relname = 'audit_events'
              and parent.relname = 'listings'
            """);

        // 'a' = NO ACTION, 'r' = RESTRICT, 'c' = CASCADE, 'n' = SET NULL, 'd' = SET DEFAULT.
        Assert.Equal('a', deleteRule);
    }

    /// <summary>
    /// The supported path works: archiving sets both the status and the timestamp. A constraint
    /// requires them to move together, so a listing cannot be archived without a deletion time
    /// (which would make retention unenforceable) or carry a deletion time while still
    /// appearing active.
    /// </summary>
    [Fact]
    public async Task SoftDelete_SettingStatusAndDeletedAtTogether_IsAccepted()
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
            """
            update public.listings
            set status = 'archived', deleted_at = timezone('utc', now())
            where id = $1
            """,
            listingId);

        Assert.Equal(1, affected);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ArchivingWithoutADeletionTimestamp_IsRefused()
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
            "update public.listings set status = 'archived' where id = $1",
            "listings_archive_consistent",
            listingId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SettingADeletionTimestampWithoutArchiving_IsRefused()
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
            "update public.listings set deleted_at = timezone('utc', now()) where id = $1",
            "listings_archive_consistent",
            listingId);

        await transaction.RollbackAsync();
    }
}
