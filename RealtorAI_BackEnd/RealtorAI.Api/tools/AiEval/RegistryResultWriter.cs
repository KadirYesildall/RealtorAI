using System.Globalization;
using System.Text.Json;
using Modules.Audit;
using Npgsql;

namespace AiEval;

/// <summary>
/// Reads the registry row under evaluation and records the verdict against it.
///
/// <para>
/// This is the only code anywhere that writes <c>ai_model_registry.eval_status</c>. The admin
/// API refuses to set it (T050), so a pass in the registry always traces back to a run of this
/// harness, by a named platform administrator, recorded in the audit trail.
/// </para>
/// </summary>
internal sealed class RegistryResultWriter
{
    /// <summary>Version of the <c>eval_result</c> document shape.</summary>
    internal const int ResultFormatVersion = 1;

    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web);

    private readonly string connectionString;

    internal RegistryResultWriter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    internal async Task<RegistryTarget?> LoadAsync(Guid registryId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select id, task_key, provider, model_id, endpoint, enabled
            from public.ai_model_registry
            where id = $1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = registryId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RegistryTarget(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5));
    }

    /// <summary>
    /// Records a <see cref="Verdict.Passed"/> or <see cref="Verdict.Failed"/> verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Guarded against a model swap mid-run.</b> The update matches on the provider, model id
    /// and endpoint that were evaluated. If an administrator changed the row while the suite was
    /// running, nothing is written: the verdict describes a model the row no longer names.
    /// </para>
    /// <para>
    /// <b>A failure disables.</b> The registry's check constraint forbids an enabled row whose
    /// evaluation is not <c>passed</c>, so recording a failed re-evaluation of an enabled model
    /// also switches it off — in the same audited change, which is the fail-closed outcome
    /// VIII.4 asks for.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The actor is not a platform administrator, the verdict is not recordable, or the row no
    /// longer names the evaluated model.
    /// </exception>
    internal async Task WriteAsync(
        RegistryTarget target,
        Verdict verdict,
        IReadOnlyList<MetricResult> results,
        Guid actorId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);

        if (verdict == Verdict.Incomplete)
        {
            throw new InvalidOperationException(
                "An incomplete evaluation is not recorded. Only a pass or a measured failure "
                + "says something true about the model.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsurePlatformAdminAsync(connection, transaction, actorId, cancellationToken);

        var before = await SnapshotAsync(connection, transaction, target.Id, cancellationToken);

        var status = verdict == Verdict.Passed ? "passed" : "failed";
        var evalResult = BuildResultDocument(target, verdict, results, evaluatedAt);

        await using (var update = new NpgsqlCommand(
            """
            update public.ai_model_registry
            set eval_status = $1::public.model_eval_status,
                eval_result = $2::jsonb,
                verified_at = $3,
                enabled = case when $1 = 'passed' then enabled else false end
            where id = $4
              and provider = $5
              and model_id = $6
              and endpoint is not distinct from $7
            """,
            connection,
            transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = status });
            update.Parameters.Add(new NpgsqlParameter { Value = evalResult });
            update.Parameters.Add(new NpgsqlParameter { Value = evaluatedAt });
            update.Parameters.Add(new NpgsqlParameter { Value = target.Id });
            update.Parameters.Add(new NpgsqlParameter { Value = target.Provider });
            update.Parameters.Add(new NpgsqlParameter { Value = target.ModelId });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)target.Endpoint ?? DBNull.Value });

            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Registry entry {target.Id} no longer names {target.Provider}/{target.ModelId}. "
                    + "It was changed during the evaluation, so this verdict was not recorded. "
                    + "Re-run the harness against the entry as it is now.");
            }
        }

        var after = await SnapshotAsync(connection, transaction, target.Id, cancellationToken);

        var writer = new AuditWriter(new NpgsqlAuditSink(connection, transaction));

        await writer.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.RegistryChanged,
                ActorId = actorId,
                TargetType = "ai_model_registry",
                TargetId = target.Id,
                BeforeValue = before,
                AfterValue = after,
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The <c>eval_result</c> document: what was evaluated, against which bars, with what result.
    /// Self-describing, so a reader years later needs no access to this version of the code.
    /// </summary>
    internal static string BuildResultDocument(
        RegistryTarget target,
        Verdict verdict,
        IReadOnlyList<MetricResult> results,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);

        var document = new
        {
            format = ResultFormatVersion,
            evaluatedAt = evaluatedAt.ToString("O", CultureInfo.InvariantCulture),
            model = new { target.Provider, target.ModelId, target.Endpoint },
            verdict = verdict.ToString().ToLowerInvariant(),
            metrics = results.Select(result => new
            {
                key = result.Metric.Key,
                source = result.Metric.Source,
                rule = result.Metric.FormatRule(),
                threshold = result.Metric.Threshold,
                value = result.Value,
                outcome = result.Outcome switch
                {
                    MetricOutcome.Passed => "passed",
                    MetricOutcome.Failed => "failed",
                    MetricOutcome.NotMeasured => "not_measured",
                    MetricOutcome.NoThreshold => "no_threshold",
                    _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}."),
                },
            }),
        };

        return JsonSerializer.Serialize(document, ResultOptions);
    }

    private static async Task EnsurePlatformAdminAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select platform_admin from public.profiles where id = $1",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = actorId });

        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                $"{actorId} is not a platform administrator. Recording an evaluation changes "
                + "whether a model may run, which FR-064 reserves for that role.");
        }
    }

    private static async Task<string> SnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid registryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select jsonb_build_object(
                'evalStatus', eval_status,
                'evalResult', eval_result,
                'verifiedAt', verified_at,
                'enabled', enabled)::text
            from public.ai_model_registry
            where id = $1
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = registryId });

        return (string)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Registry entry {registryId} disappeared."));
    }

    /// <summary>
    /// Appends audit rows inside the harness's own transaction, so the verdict and its audit
    /// record commit or roll back together — the same guarantee the API's sink gives.
    /// </summary>
    private sealed class NpgsqlAuditSink : IAuditSink
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;

        internal NpgsqlAuditSink(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (record.Entry.Action != AuditAction.RegistryChanged)
            {
                // The harness records exactly one kind of event; anything else reaching here is
                // a bug, and writing it under the wrong label would corrupt the trail.
                throw new InvalidOperationException(
                    $"The evaluation harness only records registry changes, not {record.Entry.Action}.");
            }

            var entry = record.Entry;

            await using var command = new NpgsqlCommand(
                """
                insert into public.audit_events
                    (id, action, actor_id, target_type, target_id, before_value, after_value,
                     payload_hash)
                values ($1, 'registry_changed', $2, $3, $4, $5::jsonb, $6::jsonb, $7)
                """,
                connection,
                transaction);

            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = entry.ActorId!.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = entry.TargetType! });
            command.Parameters.Add(new NpgsqlParameter { Value = entry.TargetId!.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.BeforeValue ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.AfterValue ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = record.PayloadHash });

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
