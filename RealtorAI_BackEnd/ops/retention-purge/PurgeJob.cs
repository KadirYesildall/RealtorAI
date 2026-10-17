using System.Text.Json;
using Modules.Audit;
using Npgsql;

namespace RetentionPurge;

/// <summary>How a purge runs.</summary>
/// <param name="PurgerConnectionString">The retention_purger role's own login (0019). Never the application's.</param>
/// <param name="StorageApi">The Storage API, e.g. <c>http://127.0.0.1:54321/storage/v1</c>: deleting through it is what removes the bytes.</param>
/// <param name="StorageKey">A key the Storage API accepts. On its own it cannot delete an original; only an authorization this job writes opens the guard.</param>
/// <param name="DryRun">List what is eligible, authorize and delete nothing.</param>
public sealed record PurgeOptions(
    string PurgerConnectionString,
    Uri StorageApi,
    string StorageKey,
    string OriginalsBucket,
    int BatchSize = 500,
    bool DryRun = false);

/// <summary>What a run did.</summary>
public sealed record PurgeReport(
    IReadOnlyList<string> Eligible,
    IReadOnlyList<string> Purged,
    IReadOnlyList<(string Key, string Error)> Failed);

/// <summary>
/// T154 — the FR-065 retention purge.
///
/// <para>
/// <b>Order.</b> For each original whose listing was deleted longer ago than the retention period
/// (<c>ops.retention_settings</c>), the job writes an authorization as retention_purger, deletes
/// the object through the Storage API — the only way its bytes are removed — and marks the
/// authorization purged. The write-once guard re-checks both the authorization and the retention
/// at the moment of the delete, so neither a stale authorization nor a changed setting can
/// remove an original still under retention.
/// </para>
/// <para>
/// <b>Logged.</b> One <c>originals_purged</c> audit event per listing, naming every object
/// removed, written with no actor: the purge is the one system-initiated action (AuditWriter).
/// </para>
/// <para>
/// <b>Idempotent.</b> An authorization already marked purged is not tried again; one whose delete
/// failed is retried on the next run.
/// </para>
/// </summary>
public static class PurgeJob
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static async Task<PurgeReport> RunAsync(PurgeOptions options, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);

        await using var connection = new NpgsqlConnection(options.PurgerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var eligible = await EligibleAsync(connection, options, cancellationToken);

        if (options.DryRun)
        {
            return new PurgeReport(eligible.Select(entry => entry.Key).ToList(), [], []);
        }

        var purged = new List<(string Key, Guid Listing)>();
        var failed = new List<(string Key, string Error)>();

        foreach (var (key, listing) in eligible)
        {
            await ExecuteAsync(
                connection,
                """
                insert into ops.purge_authorizations (object_name, listing_id) values ($1, $2)
                on conflict (object_name) do nothing
                """,
                cancellationToken,
                key,
                listing);

            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{options.StorageApi.ToString().TrimEnd('/')}/object/{options.OriginalsBucket}/{key}"));
            request.Headers.Add("apikey", options.StorageKey);
            request.Headers.Add("Authorization", $"Bearer {options.StorageKey}");

            using var response = await http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                failed.Add((key, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}"));
                continue;
            }

            await ExecuteAsync(
                connection,
                "update ops.purge_authorizations set purged_at = timezone('utc', now()) where object_name = $1",
                cancellationToken,
                key);

            purged.Add((key, listing));
        }

        var writer = new AuditWriter(new PurgeAuditSink(connection));

        foreach (var group in purged.GroupBy(entry => entry.Listing))
        {
            await writer.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.OriginalsPurged,
                    ListingId = group.Key,
                    AfterValue = JsonSerializer.Serialize(new { objects = group.Select(entry => entry.Key).ToList() }, Web),
                },
                cancellationToken);
        }

        return new PurgeReport(eligible.Select(entry => entry.Key).ToList(), purged.Select(entry => entry.Key).ToList(), failed);
    }

    private static async Task<List<(string Key, Guid Listing)>> EligibleAsync(NpgsqlConnection connection, PurgeOptions options, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select o.name, split_part(o.name, '/', 1)::uuid
            from storage.objects o
            where o.bucket_id = $1
              and ops.original_is_expired(o.name)
              and not exists (select 1 from ops.purge_authorizations a where a.object_name = o.name and a.purged_at is not null)
            order by o.name
            limit $2
            """,
            connection)
        {
            Parameters = { new() { Value = options.OriginalsBucket }, new() { Value = options.BatchSize } },
        };

        var eligible = new List<(string, Guid)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            eligible.Add((reader.GetString(0), reader.GetGuid(1)));
        }

        return eligible;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Appends purge events with the purge credential, which may insert audit events and nothing more.</summary>
    private sealed class PurgeAuditSink(NpgsqlConnection connection) : IAuditSink
    {
        public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);

            await using var command = new NpgsqlCommand(
                """
                insert into public.audit_events (id, listing_id, action, target_type, target_id, after_value, payload_hash)
                values ($1, $2, 'originals_purged', $3, $4, $5::jsonb, $6)
                """,
                connection)
            {
                Parameters =
                {
                    new() { Value = Guid.CreateVersion7() },
                    new() { Value = (object?)record.Entry.ListingId ?? DBNull.Value },
                    new() { Value = (object?)record.Entry.TargetType ?? DBNull.Value },
                    new() { Value = (object?)record.Entry.TargetId ?? DBNull.Value },
                    new() { Value = (object?)record.Entry.AfterValue ?? DBNull.Value },
                    new() { Value = record.PayloadHash },
                },
            };

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
