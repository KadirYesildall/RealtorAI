using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace Pipeline;

/// <summary>Settings for the background pipeline's job storage and workers.</summary>
/// <param name="ConnectionString">
/// Npgsql connection string for the Supabase Postgres. Server-only, like every connection string
/// in this API.
/// </param>
/// <param name="WorkerCount">
/// Concurrent jobs this process runs. Each holds a database connection while it works, so this
/// is sized against the connection pool, not against CPU count — Hangfire's own default of five
/// workers per core would exhaust a Supabase pool on an ordinary developer machine.
/// </param>
/// <param name="Queue">
/// The queue pipeline runs are placed on and this process serves. <c>pipeline</c> in every real
/// deployment; a test host names a queue of its own, so its jobs cannot be taken by another
/// process's workers running a different configuration.
/// </param>
public sealed record PipelineOptions(string ConnectionString, int WorkerCount, string Queue = HangfireSetup.PipelineQueue);

/// <summary>
/// T055 — Hangfire on Postgres, in the dedicated <c>hangfire</c> schema (research R12).
///
/// <para>
/// <b>Storage.</b> The schema is created and closed to client roles by
/// <c>supabase/migrations/0013_hangfire_schema.sql</c>; Hangfire installs its own tables inside
/// it on first start. Its tables never appear in <c>public</c>, so they cannot collide with a
/// migration and are not exposed by the Data API.
/// </para>
/// <para>
/// <b>Driver.</b> Hangfire.PostgreSql 1.21.1 is built against Npgsql 6.0.11 while this host
/// resolves Npgsql 10.0.3. That was verified by running a real job end to end, not inferred from
/// a clean restore — see HangfireStorageTests and research R28.
/// </para>
/// <para>
/// <b>No dashboard.</b> Hangfire's dashboard shows job arguments, which include listing ids,
/// and its authorization model is separate from the Supabase token validation every other
/// endpoint uses. It is deliberately not mapped; job state is inspected through the database by
/// operators, and pipeline progress reaches agents through the SignalR hub.
/// </para>
/// </summary>
public static class HangfireSetup
{
    /// <summary>The schema 0013_hangfire_schema.sql creates.</summary>
    public const string Schema = "hangfire";

    /// <summary>
    /// The queue pipeline steps are placed on. Listed first so pipeline work is taken ahead of
    /// anything left on the default queue.
    /// </summary>
    public const string PipelineQueue = "pipeline";

    public static IServiceCollection AddPipelineJobs(
        this IServiceCollection services,
        PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WorkerCount, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Queue);

        services.AddSingleton(options);

        services.AddHangfire(configuration => configuration
            // Pins the storage format, so a package upgrade cannot change how already-queued
            // jobs are read back.
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                bootstrap => bootstrap.UseNpgsqlConnection(options.ConnectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = Schema,

                    // Installs Hangfire's tables into the schema the migration created, and
                    // upgrades them when the package version requires it.
                    PrepareSchemaIfNecessary = true,
                }));

        services.AddHangfireServer((provider, server) =>
        {
            server.WorkerCount = options.WorkerCount;
            server.Queues = [options.Queue, "default"];

            // Jobs are activated from THIS host's container. Without it, Hangfire falls back to
            // its process-wide static activator, which the last host to start overwrites — so
            // with two hosts in one process (the test suites run many) a job could run inside
            // another host's container, with another registry and another configuration.
            // Found by the pipeline tests (research R34).
            server.Activator = new AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
        });

        return services;
    }
}
