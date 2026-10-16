using System.Collections.Concurrent;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// Target of the probe job. Public and static so Hangfire can serialize the call by type and
/// method name and invoke it in the host's worker.
/// </summary>
public static class HangfireProbe
{
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Executed = new();

    public static void Mark(Guid token) => Executed[token] = DateTimeOffset.UtcNow;

    internal static bool WasExecuted(Guid token) => Executed.ContainsKey(token);
}

/// <summary>
/// T055 — Hangfire on the Supabase Postgres, in its own schema (research R12).
///
/// <para>
/// Three things were unverified until these ran (R12, R17):
/// </para>
/// <list type="number">
///   <item>
///     <b>Driver compatibility.</b> Hangfire.PostgreSql 1.21.1 is compiled against Npgsql
///     6.0.11; the host resolves Npgsql 10.0.3. A major-version gap in a binary dependency
///     either works or fails at the first call into it, so the only meaningful check is a real
///     job, enqueued, picked up by a worker and executed — not a package restore.
///   </item>
///   <item>
///     <b>Isolation.</b> Hangfire installs and upgrades its own tables. They must land in the
///     <c>hangfire</c> schema, never in <c>public</c>, where Supabase migrations and the Data API
///     live.
///   </item>
///   <item>
///     <b>Exposure.</b> Job arguments carry listing ids and pipeline state. Neither <c>anon</c>
///     nor <c>authenticated</c> may reach the schema — the lesson of research R18 is that a
///     Supabase default grant can quietly undo what a migration intended.
///   </item>
/// </list>
/// </summary>
public sealed class HangfireStorageTests : IClassFixture<HangfireStorageTests.Factory>
{
    private readonly Factory factory;

    public HangfireStorageTests(Factory factory)
    {
        this.factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
        }
    }

    [Fact]
    public async Task AnEnqueuedJob_IsExecutedByTheHostsWorker()
    {
        var client = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var token = Guid.NewGuid();

        var jobId = client.Enqueue(() => HangfireProbe.Mark(token));

        Assert.False(string.IsNullOrEmpty(jobId));

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!HangfireProbe.WasExecuted(token) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(
            HangfireProbe.WasExecuted(token),
            $"Job {jobId} was enqueued but not executed within 30 seconds. Either no worker is "
            + "polling the queue it was placed on, or Hangfire.PostgreSql failed against the "
            + "resolved Npgsql version — check the host log.");
    }

    [Fact]
    public async Task HangfireTables_LiveInTheHangfireSchema_AndNotInPublic()
    {
        // Touching the storage makes sure the host has started and installed its schema.
        _ = factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().Servers();

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var hangfireTables = await SupabaseDatabase.ScalarAsync<long>(
            connection,
            null,
            """
            select count(*) from information_schema.tables
            where table_schema = 'hangfire' and table_name in ('job', 'state', 'jobqueue', 'schema')
            """);

        Assert.Equal(4, hangfireTables);

        var leakedIntoPublic = await SupabaseDatabase.ScalarAsync<long>(
            connection,
            null,
            """
            select count(*) from information_schema.tables
            where table_schema = 'public'
              and table_name in ('job', 'state', 'jobqueue', 'jobparameter', 'hash', 'set',
                                 'list', 'counter', 'aggregatedcounter', 'server', 'lock')
            """);

        Assert.Equal(0, leakedIntoPublic);
    }

    [Theory]
    [InlineData("anon")]
    [InlineData("authenticated")]
    public async Task ClientRoles_CannotReachTheHangfireSchema(string role)
    {
        _ = factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().Servers();

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var hasUsage = await SupabaseDatabase.ScalarAsync<bool>(
            connection,
            null,
            "select has_schema_privilege($1, 'hangfire', 'USAGE')",
            role);

        Assert.False(hasUsage, $"{role} holds USAGE on the hangfire schema.");

        // Belt and braces: even with the schema closed, no table privilege should have been
        // granted by a default — R18 found exactly that kind of silent grant in public.
        var tablePrivileges = await SupabaseDatabase.ScalarAsync<long>(
            connection,
            null,
            """
            select count(*) from information_schema.role_table_grants
            where table_schema = 'hangfire' and grantee = $1
            """,
            role);

        Assert.Equal(0, tablePrivileges);
    }
}
