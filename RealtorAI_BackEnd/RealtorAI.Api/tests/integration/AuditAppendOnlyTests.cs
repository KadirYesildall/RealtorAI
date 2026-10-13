namespace RealtorAI.Tests.Integration;

/// <summary>
/// T027 — <c>audit_events</c> is append-only (FR-049).
///
/// <para>
/// These run as <c>postgres</c>, a superuser that bypasses RLS and ignores table grants. That
/// is the point: an append-only guarantee that rests on grants alone is not append-only, it is
/// append-only-for-unprivileged-callers. Since the API's own connection is privileged, the rule
/// has to hold at the highest privilege level available, which is why 0009 adds a
/// statement-level BEFORE UPDATE OR DELETE trigger on top of the INSERT-only policy.
/// </para>
/// </summary>
public sealed class AuditAppendOnlyTests
{
    [Fact]
    public async Task Update_IsRefused_EvenForASuperuser()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var eventId = await InsertAuditEventAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "update public.audit_events set payload_hash = 'tampered' where id = $1",
            "append-only",
            eventId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Delete_IsRefused_EvenForASuperuser()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var eventId = await InsertAuditEventAsync(connection, transaction);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "delete from public.audit_events where id = $1",
            "append-only",
            eventId);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The trigger is statement-level, so it must fire even when the statement matches no rows.
    /// A row-level trigger would let <c>delete from audit_events where false</c> through, and a
    /// <c>truncate</c>-shaped or predicate-wide deletion would then be a matter of finding a
    /// predicate that matches nothing at validation time.
    /// </summary>
    [Fact]
    public async Task Delete_MatchingNoRows_IsStillRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "delete from public.audit_events where id = '00000000-0000-0000-0000-000000000000'",
            "append-only");

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Insert_IsAllowed()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Guards against the opposite failure: a trigger written slightly too broadly would
        // make the audit trail unwritable, and every other test here would still pass.
        var eventId = await InsertAuditEventAsync(connection, transaction);

        var stored = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction,
            "select count(*) from public.audit_events where id = $1",
            eventId);

        Assert.Equal(1, stored);

        await transaction.RollbackAsync();
    }

    private static async Task<Guid> InsertAuditEventAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction)
    {
        var listingId = await SupabaseDatabase.InsertListingAsync(
            connection,
            transaction,
            SupabaseDatabase.AgentA);

        var eventId = Guid.NewGuid();

        await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.audit_events (id, listing_id, actor_id, action, payload_hash)
            values ($1, $2, $3, 'field_edit', 'original')
            """,
            eventId,
            listingId,
            SupabaseDatabase.AgentA);

        return eventId;
    }
}
