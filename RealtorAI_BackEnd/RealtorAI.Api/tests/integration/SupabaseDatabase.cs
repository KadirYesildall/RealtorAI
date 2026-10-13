using Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// Shared access to the local Supabase-managed PostgreSQL instance for the schema-guarantee
/// tests (T026–T033).
///
/// <para>
/// <b>Why not Testcontainers.</b> A bare <c>postgres</c> image cannot host these tests: every
/// guarantee being verified depends on things the Supabase CLI stack provides and a plain
/// container does not — the <c>authenticated</c>, <c>anon</c>, and <c>service_role</c> roles
/// that RLS policies name, the <c>auth</c> schema and <c>auth.uid()</c> that every policy calls,
/// the <c>storage</c> schema the bucket policies attach to, and the <c>pgvector</c> extension.
/// Recreating that by hand would mean testing against an imitation of the deployment target
/// instead of the target itself, and the constitution's local-first rule requires schema work
/// to be verified against <c>supabase start</c>. The Testcontainers package was therefore
/// removed from this project rather than left unused.
/// </para>
///
/// <para>
/// Consequence: these tests need the local stack running. They fail with a pointer to
/// quickstart.md rather than skipping, because a silently skipped constitutional guarantee is
/// worse than a red test.
/// </para>
/// </summary>
internal static class SupabaseDatabase
{
    /// <summary>Seed identities from supabase/seed.sql. Approved broker_admin of Pacific Crest.</summary>
    internal static readonly Guid AgentA = new("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary>Pending (not yet approved) member of the same brokerage as <see cref="AgentA"/>.</summary>
    internal static readonly Guid AgentB = new("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>Approved agent at a different brokerage.</summary>
    internal static readonly Guid AgentC = new("cccccccc-0000-4000-8000-000000000003");

    internal static readonly Guid PlatformAdmin = new("dddddddd-0000-4000-8000-000000000004");

    internal static readonly Guid BrokeragePacificCrest = new("11110000-0000-4000-8000-000000000001");

    internal static readonly Guid SeedListing = new("99990000-0000-4000-8000-000000000001");

    /// <summary>
    /// Overridable so CI can point at its own stack, with the verified local default from
    /// supabase/config.toml otherwise. The password is the CLI's fixed local development
    /// value, not a secret.
    /// </summary>
    internal static string ConnectionString =>
        Environment.GetEnvironmentVariable("REALTORAI_TEST_POSTGRES")
        ?? "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>
    /// One shared <see cref="DbContextOptions"/> for every test context.
    ///
    /// <para>
    /// Built once on purpose. EF derives an internal service provider from the options
    /// instance, so constructing fresh options per context builds a fresh provider each time —
    /// and past twenty of them EF escalates <c>ManyServiceProvidersCreatedWarning</c> to an
    /// exception. That is not a test quirk to be suppressed: the same pattern in production
    /// would leak a provider per request, which is exactly what the warning is for. Sharing one
    /// options instance is the actual fix in both places.
    /// </para>
    /// </summary>
    private static readonly Lazy<DbContextOptions<AppDbContext>> SharedOptions = new(() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, AppDbContext.ConfigureNpgsql)
            .ReplaceService<IMigrator, MigrationsAreOwnedBySqlMigrator>()
            .Options);

    /// <summary>
    /// A context configured exactly as the host configures it, so a test never exercises a
    /// differently-mapped model than production uses.
    /// </summary>
    internal static AppDbContext CreateContext() => new(SharedOptions.Value);

    /// <summary>
    /// Opens a connection as <c>postgres</c>, which is a superuser and therefore
    /// <b>bypasses RLS</b>. That is deliberate for the constraint and trigger tests: proving a
    /// rule holds against the most privileged connection available is a much stronger claim
    /// than proving an RLS policy hid the row from a restricted one.
    /// </summary>
    internal static async Task<NpgsqlConnection> OpenSuperuserAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);

        try
        {
            await connection.OpenAsync();
        }
        catch (NpgsqlException exception)
        {
            await connection.DisposeAsync();

            throw new InvalidOperationException(
                "Could not reach the local Supabase database at "
                + $"{new NpgsqlConnectionStringBuilder(ConnectionString).Host}:"
                + $"{new NpgsqlConnectionStringBuilder(ConnectionString).Port}. "
                + "Start it with `npx --yes supabase@latest start` from "
                + "RealtorAI_BackEnd/, then `npx --yes supabase@latest db reset` to apply "
                + "migrations and seed. See specs/002-ai-listing-launch/quickstart.md.",
                exception);
        }

        return connection;
    }

    /// <summary>
    /// Begins a transaction impersonating a signed-in agent: the <c>authenticated</c> role
    /// (which RLS policies are written against) with <c>auth.uid()</c> resolving to
    /// <paramref name="agentId"/>.
    ///
    /// <para>
    /// Both settings are <c>LOCAL</c>, so rolling the transaction back restores the connection
    /// for the pool. This matters more than it looks: during manual verification a
    /// <c>set_config(..., true)</c> issued outside a transaction appeared to do nothing,
    /// because psql committed each statement on its own and the setting vanished before the
    /// next one ran. Inside an explicit transaction, transaction-local is exactly right.
    /// </para>
    /// </summary>
    internal static async Task<NpgsqlTransaction> BeginAsAgentAsync(
        NpgsqlConnection connection,
        Guid agentId)
    {
        var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "set local role authenticated");

        // auth.uid() reads this setting; see pg_get_functiondef('auth.uid'). Parameterised so
        // the id cannot be spliced into SQL text.
        await ExecuteAsync(
            connection,
            transaction,
            "select set_config('request.jwt.claims', $1, true)",
            $"{{\"sub\":\"{agentId}\",\"role\":\"authenticated\"}}");

        return transaction;
    }

    internal static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params object?[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    internal static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params object?[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>
    /// Asserts that <paramref name="sql"/> is refused by the database, and that it is refused
    /// for the expected reason.
    ///
    /// <para>
    /// Checking the constraint or trigger name matters: a test that merely asserts "some error"
    /// passes just as happily when the statement fails on a typo, a missing column, or a
    /// foreign key — which would leave the actual guarantee unverified while showing green.
    /// </para>
    /// </summary>
    internal static async Task AssertRefusedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        string expectedReasonContains,
        params object?[] parameters)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, transaction, sql, parameters));

        var detail = $"{exception.ConstraintName} {exception.MessageText} {exception.SqlState}";

        Assert.Contains(expectedReasonContains, detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inserts a minimal listing owned by <paramref name="agentId"/> and returns its id. Used
    /// by tests that need a parent row; callers roll back, so nothing persists.
    /// </summary>
    internal static async Task<Guid> InsertListingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid agentId)
    {
        var id = Guid.NewGuid();

        await ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.listings
                (id, agent_id, brokerage_id, address_line1, city, state, postal_code, property_type)
            values ($1, $2, $3, '1 Test Way', 'Testville', 'CA', '90001', 'Residential')
            """,
            id,
            agentId,
            BrokeragePacificCrest);

        return id;
    }
}
