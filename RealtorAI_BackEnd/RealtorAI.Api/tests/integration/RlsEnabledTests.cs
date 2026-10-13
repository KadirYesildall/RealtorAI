namespace RealtorAI.Tests.Integration;

/// <summary>
/// T026 — every table this feature adds has row-level security enabled, and has at least one
/// policy.
///
/// <para>
/// Both halves are needed. RLS enabled with no policy denies everything, which fails closed and
/// is safe but broken; a policy with RLS disabled is decorative and fails open. Checking only
/// <c>relrowsecurity</c> would pass for a table whose policies were all dropped.
/// </para>
/// <para>
/// The table list is written out rather than discovered from the schema on purpose: a test that
/// asks "do all tables that exist have RLS" goes green when a table is accidentally dropped.
/// This asks "do these specific tables exist and have RLS", so a disappearing table is a
/// failure.
/// </para>
/// </summary>
public sealed class RlsEnabledTests
{
    /// <summary>
    /// The 16 tables added by migrations 0003–0010, plus <c>profiles</c> from feature 001,
    /// which 0004 extends with the license and platform_admin columns.
    /// </summary>
    public static TheoryData<string> SecuredTables { get; } = new()
    {
        "brokerages",
        "brokerage_memberships",
        "brokerage_settings",
        "profiles",
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

    [Theory]
    [MemberData(nameof(SecuredTables))]
    public async Task Table_HasRowLevelSecurityEnabled(string tableName)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var rowSecurityEnabled = await SupabaseDatabase.ScalarAsync<bool?>(
            connection,
            transaction: null,
            """
            select c.relrowsecurity
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public' and c.relname = $1 and c.relkind = 'r'
            """,
            tableName);

        Assert.True(
            rowSecurityEnabled.HasValue,
            $"public.{tableName} does not exist. Run `supabase db reset` to apply migrations.");

        Assert.True(
            rowSecurityEnabled!.Value,
            $"public.{tableName} exists but has RLS disabled. Add `alter table ... enable row "
            + "level security` to its migration — without it the anon and authenticated roles "
            + "read every row.");
    }

    /// <summary>
    /// <c>photo_embeddings</c> is the one table that deliberately has no policy.
    ///
    /// <para>
    /// Embeddings are an internal artefact used for near-duplicate detection; no client has any
    /// reason to read them, and a raw vector is a fingerprint of the image it came from. RLS
    /// enabled with no policy and no grant means the authenticated role cannot touch the table
    /// at all — denying everything is the intent here, not an omission.
    /// </para>
    /// <para>
    /// It is listed as an exception rather than excluded from the suite, and
    /// <see cref="PolicylessTable_GrantsNothingToAuthenticated"/> verifies the stronger property
    /// that makes the exception safe. An exception with no accompanying check would be a hole.
    /// </para>
    /// </summary>
    private const string DeliberatelyPolicylessTable = "photo_embeddings";

    [Theory]
    [MemberData(nameof(SecuredTables))]
    public async Task Table_HasAtLeastOnePolicy(string tableName)
    {
        if (tableName == DeliberatelyPolicylessTable)
        {
            return;
        }

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var policyCount = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            "select count(*) from pg_policies where schemaname = 'public' and tablename = $1",
            tableName);

        Assert.True(
            policyCount > 0,
            $"public.{tableName} has RLS enabled but no policies, so it denies all access to "
            + "the authenticated role. That fails closed, which is safe but almost certainly "
            + "not what the migration intended. If it IS intended, add it to "
            + $"{nameof(DeliberatelyPolicylessTable)} with the reason.");
    }

    /// <summary>
    /// The service-role-only table must also hold no privileges for <c>authenticated</c> or
    /// <c>anon</c>. Without this, a later migration could add a blanket
    /// <c>grant select on all tables</c> and the table would become world-readable while
    /// still having no policy — and RLS with a grant but no policy still denies, so the only
    /// thing standing between the two is a detail no test was watching.
    /// </summary>
    [Fact]
    public async Task PolicylessTable_GrantsNothingToAuthenticated()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var grantCount = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            """
            select count(*)
            from information_schema.role_table_grants
            where table_schema = 'public'
              and table_name = $1
              and grantee in ('authenticated', 'anon')
            """,
            DeliberatelyPolicylessTable);

        Assert.True(
            grantCount == 0,
            $"public.{DeliberatelyPolicylessTable} is meant to be service-role only, but the "
            + $"authenticated or anon role holds {grantCount} privilege(s) on it.");
    }

    /// <summary>
    /// Counts the whole set in one assertion as well, so adding a table to a migration without
    /// adding it to <see cref="SecuredTables"/> shows up here rather than going unchecked.
    /// </summary>
    [Fact]
    public async Task NoPublicTableIsLeftWithoutRowLevelSecurity()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            """
            select c.relname
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
              and not c.relrowsecurity
            order by c.relname
            """,
            connection);

        var unprotected = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            unprotected.Add(reader.GetString(0));
        }

        Assert.True(
            unprotected.Count == 0,
            "These public tables have RLS disabled: " + string.Join(", ", unprotected));
    }
}
