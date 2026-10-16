namespace RealtorAI.Tests.Integration;

/// <summary>
/// Regression tests for 0012_privilege_hardening.sql.
///
/// <para>
/// These exist because of a defect found while writing T026: Supabase's default privileges on
/// the <c>public</c> schema grant ALL on every new table to <c>anon</c> and
/// <c>authenticated</c>, so migrations 0003–0011 handed out SELECT, INSERT, UPDATE, DELETE,
/// TRUNCATE, REFERENCES and TRIGGER on every table they created — irrespective of the narrower
/// <c>grant</c> statements they wrote.
/// </para>
/// <para>
/// The consequence was not cosmetic. TRUNCATE is not subject to row-level security, and the
/// append-only trigger covered only UPDATE and DELETE, so an ordinary signed-in agent at an
/// unrelated brokerage could truncate <c>listings</c> and <c>audit_events</c> and cascade
/// through eight more tables — destroying every listing and the whole audit trail while RLS
/// behaved exactly as designed. It was reproduced before the fix was written.
/// </para>
/// <para>
/// The tests are phrased as privilege assertions rather than "try it and see it fail", because
/// a privilege that should not exist is the defect; waiting for a successful exploit is a
/// weaker check and a destructive one.
/// </para>
/// </summary>
[Collection(TableLockingTests.Name)]
public sealed class PrivilegeHardeningTests
{
    /// <summary>Tables this feature owns. <c>profiles</c> predates it and is granted by 0001/0004.</summary>
    public static TheoryData<string> FeatureTables { get; } = new()
    {
        "brokerages",
        "brokerage_memberships",
        "brokerage_settings",
        "listings",
        "listing_fields",
        "mls_rule_profiles",
        "photos",
        "photo_analyses",
        "photo_embeddings",
        "content_drafts",
        "compliance_findings",
        "approvals",
        "ai_model_registry",
        "prompt_versions",
        "ai_usage",
        "audit_events",
    };

    /// <summary>
    /// TRUNCATE is the privilege that made this a real problem: it ignores RLS entirely, so no
    /// policy anywhere can contain it. No client role may hold it on any table.
    /// </summary>
    [Theory]
    [MemberData(nameof(FeatureTables))]
    public async Task NoClientRole_HoldsTruncate(string tableName)
    {
        await AssertNoPrivilegeAsync(tableName, "TRUNCATE");
    }

    /// <summary>
    /// REFERENCES lets a role point a foreign key at the table, which is a way to probe whether
    /// a row RLS is hiding exists: the insert succeeds or fails depending on the hidden row.
    /// </summary>
    [Theory]
    [MemberData(nameof(FeatureTables))]
    public async Task NoClientRole_HoldsReferences(string tableName)
    {
        await AssertNoPrivilegeAsync(tableName, "REFERENCES");
    }

    /// <summary>TRIGGER would let a client role attach code that runs on every write.</summary>
    [Theory]
    [MemberData(nameof(FeatureTables))]
    public async Task NoClientRole_HoldsTrigger(string tableName)
    {
        await AssertNoPrivilegeAsync(tableName, "TRIGGER");
    }

    /// <summary>
    /// Nothing in this feature is public, so the pre-authentication role holds nothing at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(FeatureTables))]
    public async Task AnonRole_HoldsNoPrivilegeWhatsoever(string tableName)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var privileges = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            """
            select count(*) from information_schema.role_table_grants
            where table_schema = 'public' and table_name = $1 and grantee = 'anon'
            """,
            tableName);

        Assert.True(
            privileges == 0,
            $"anon holds {privileges} privilege(s) on public.{tableName}. Listing data is "
            + "brokerage-confidential; the unauthenticated role should hold nothing.");
    }

    /// <summary>
    /// FR-065: soft delete only. Withholding DELETE makes that unconditional, where previously
    /// it depended on the listing already having an audit row to anchor the foreign key.
    /// </summary>
    [Fact]
    public async Task Authenticated_CannotDeleteListings()
    {
        await AssertNoPrivilegeAsync("listings", "DELETE");
    }

    /// <summary>FR-049: the audit trail is append-only at the privilege level too.</summary>
    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task Authenticated_CannotModifyAuditEvents(string privilege)
    {
        await AssertNoPrivilegeAsync("audit_events", privilege);
    }

    /// <summary>
    /// An agent cannot manufacture a clean compliance result or erase an inconvenient finding.
    /// UPDATE is permitted (acknowledging a warning is an update) but INSERT and DELETE are not.
    /// </summary>
    [Theory]
    [InlineData("INSERT")]
    [InlineData("DELETE")]
    public async Task Authenticated_CannotFabricateOrEraseComplianceFindings(string privilege)
    {
        await AssertNoPrivilegeAsync("compliance_findings", privilege);
    }

    /// <summary>
    /// An approval that was granted stays on record. Revocation is an update that sets
    /// <c>revoked_at</c>, not a deletion of the evidence that it once happened.
    /// </summary>
    [Fact]
    public async Task Authenticated_CannotDeleteApprovals()
    {
        await AssertNoPrivilegeAsync("approvals", "DELETE");
    }

    /// <summary>
    /// The embeddings table is the one that is service-role only, and now genuinely is.
    /// </summary>
    [Fact]
    public async Task PhotoEmbeddings_GrantsNothingToClientRoles()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var privileges = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            """
            select count(*) from information_schema.role_table_grants
            where table_schema = 'public'
              and table_name = 'photo_embeddings'
              and grantee in ('anon', 'authenticated')
            """);

        Assert.True(
            privileges == 0,
            $"photo_embeddings grants {privileges} privilege(s) to a client role. A raw "
            + "embedding is a fingerprint of the image it came from, and no client needs to "
            + "read one.");
    }

    /// <summary>
    /// The trigger guard, verified at the highest privilege level. The privileges above keep
    /// client roles out; this is what still holds when the connection is the API's own, which
    /// is privileged, or when a future migration re-grants by accident.
    /// </summary>
    [Fact]
    public async Task TruncatingAuditEvents_IsRefused_EvenForASuperuser()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "truncate public.audit_events",
            "append-only");

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TruncatingListings_IsRefused_EvenForASuperuser()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // CASCADE named explicitly because listings is referenced by nine tables; without it
        // the statement fails on the references instead of reaching the guard, and the test
        // would pass for the wrong reason.
        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "truncate public.listings cascade",
            "cannot be truncated");

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The reproduction of the original defect, now expected to be refused at the privilege
    /// level. Agent C is an approved agent at an unrelated brokerage who owns no listings — the
    /// least privileged real user in the seed.
    /// </summary>
    [Fact]
    public async Task UnrelatedAgent_CannotTruncateListingsOrAudit()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentC);

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "truncate public.listings, public.audit_events cascade",
            "42501");

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The complement: hardening must not have locked out the operations the feature needs. An
    /// over-broad revoke would pass every test above while breaking the product.
    /// </summary>
    [Fact]
    public async Task OwningAgent_CanStillReadAndUpdateTheirListing()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        var visible = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction,
            "select count(*) from public.listings where id = $1",
            SupabaseDatabase.SeedListing);

        Assert.Equal(1, visible);

        var updated = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            "update public.listings set city = 'Still Editable' where id = $1",
            SupabaseDatabase.SeedListing);

        Assert.Equal(1, updated);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task OwningAgent_CanStillInsertAListing()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        var inserted = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.listings
                (agent_id, brokerage_id, address_line1, city, state, postal_code, property_type)
            values ($1, $2, '2 Privilege Lane', 'Testville', 'CA', '90002', 'Residential')
            """,
            SupabaseDatabase.AgentA,
            SupabaseDatabase.BrokeragePacificCrest);

        Assert.Equal(1, inserted);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task OwningAgent_CanStillAppendAnAuditEvent()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await SupabaseDatabase.BeginAsAgentAsync(
            connection,
            SupabaseDatabase.AgentA);

        var inserted = await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.audit_events (listing_id, actor_id, action)
            values ($1, $2, 'field_edit')
            """,
            SupabaseDatabase.SeedListing,
            SupabaseDatabase.AgentA);

        Assert.Equal(1, inserted);

        await transaction.RollbackAsync();
    }

    private static async Task AssertNoPrivilegeAsync(string tableName, string privilege)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var holders = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            """
            select count(*) from information_schema.role_table_grants
            where table_schema = 'public'
              and table_name = $1
              and grantee in ('anon', 'authenticated')
              and privilege_type = $2
            """,
            tableName,
            privilege);

        Assert.True(
            holders == 0,
            $"A client role holds {privilege} on public.{tableName}. Supabase's schema default "
            + "grants ALL on new tables to anon and authenticated, so this is most likely a "
            + "table added after 0012 without its own revoke — not an explicit grant.");
    }

    /// <summary>
    /// Future tables must not inherit the schema default either, or this whole class becomes a
    /// snapshot of one afternoon's cleanup. 0012 revokes the default for the migration role.
    /// </summary>
    [Fact]
    public async Task SchemaDefaultPrivileges_NoLongerGrantToClientRoles()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var offending = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            """
            select count(*)
            from pg_default_acl d
            join pg_namespace n on n.oid = d.defaclnamespace
            cross join unnest(d.defaclacl) as entry
            where n.nspname = 'public'
              and d.defaclobjtype = 'r'
              and d.defaclrole = 'postgres'::regrole
              and (entry::text like 'anon=%' or entry::text like 'authenticated=%')
            """);

        Assert.True(
            offending == 0,
            "The public schema still has default privileges granting table access to anon or "
            + "authenticated for the migration role, so the next table created will repeat the "
            + "0012 defect. Re-apply the `alter default privileges ... revoke` in that "
            + "migration.");
    }
}
