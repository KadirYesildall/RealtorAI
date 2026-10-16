using Npgsql;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>
/// Identities created by <c>supabase/seed.sql</c>, named by the relationship each one exists
/// to exercise.
/// </summary>
internal static class Seed
{
    /// <summary>Approved <c>broker_admin</c> of Pacific Crest — the brokerage's first registrant.</summary>
    internal static readonly Guid AgentA = new("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary><c>pending</c> agent at Pacific Crest.</summary>
    internal static readonly Guid AgentB = new("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>Approved agent at Harbor Point, a different brokerage.</summary>
    internal static readonly Guid AgentC = new("cccccccc-0000-4000-8000-000000000003");

    /// <summary><c>profiles.platform_admin = true</c>, set by direct database access in the seed.</summary>
    internal static readonly Guid PlatformAdmin = new("dddddddd-0000-4000-8000-000000000004");

    internal static readonly Guid PacificCrest = new("11110000-0000-4000-8000-000000000001");

    internal static readonly Guid HarborPoint = new("22220000-0000-4000-8000-000000000002");
}

/// <summary>
/// Agents created by a test, for flows that would otherwise change a seeded persona — approving
/// Agent B, for instance, would silently break every test that relies on B being pending.
///
/// <para>
/// <b>They persist.</b> Anything an endpoint audits is referenced by an append-only audit row,
/// and the database refuses to delete what audit rows reference, so test-created agents and
/// brokerages that went through an audited endpoint stay in the local stack. Their emails end in
/// <c>@contract.test</c>; <c>supabase db reset</c> clears them.
/// </para>
/// </summary>
internal static class TestAgents
{
    /// <summary>
    /// Creates an agent. With <paramref name="completeProfile"/>, also records a license so the
    /// agent can declare a brokerage (FR-058).
    /// </summary>
    internal static async Task<Guid> CreateAsync(string label, bool completeProfile = true)
    {
        var id = Guid.CreateVersion7();

        // Token columns as empty strings: GoTrue cannot scan NULL into them (see seed.sql).
        await TestDatabase.ExecuteAsync(
            """
            insert into auth.users
                (id, instance_id, aud, role, email, encrypted_password, email_confirmed_at,
                 created_at, updated_at, confirmation_token, recovery_token, email_change,
                 email_change_token_new)
            values ($1, '00000000-0000-0000-0000-000000000000', 'authenticated', 'authenticated',
                    $2, 'x', timezone('utc', now()), timezone('utc', now()), timezone('utc', now()),
                    '', '', '', '')
            """,
            id,
            $"{label}-{id:N}@contract.test");

        // feature 001's trigger has already created the profile row.
        await TestDatabase.ExecuteAsync(
            "update public.profiles set full_name = $2 where id = $1",
            id,
            label);

        if (completeProfile)
        {
            await TestDatabase.ExecuteAsync(
                """
                update public.profiles
                set license_number = $2, license_state = 'CA',
                    profile_completed_at = timezone('utc', now())
                where id = $1
                """,
                id,
                "CA-" + id.ToString("N")[..8]);
        }

        return id;
    }
}

/// <summary>
/// Direct access to the database the API under test is using, for arranging state and for
/// asserting on what a request actually persisted — a response saying "updated" is a claim; the
/// row is the evidence.
/// </summary>
internal static class TestDatabase
{
    internal static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("REALTORAI_TEST_POSTGRES")
        ?? "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Maximum Pool Size=10";

    internal static async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    internal static async Task<T?> ScalarAsync<T>(string sql, params object?[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, sql, parameters);

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    internal static async Task ExecuteAsync(string sql, params object?[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, sql, parameters);

        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object?[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        }

        return command;
    }
}
