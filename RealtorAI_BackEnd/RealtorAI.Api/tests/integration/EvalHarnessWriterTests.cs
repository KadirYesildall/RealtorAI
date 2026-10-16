using AiEval;
using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T054 — the evaluation harness recording a verdict against the real database.
///
/// <para>
/// The harness is the only writer of <c>eval_status</c>, so these tests pin what that write
/// may and may not do: it is attributed to a platform administrator and audited in the same
/// transaction; it refuses anyone else; it refuses to stamp a verdict on a row whose model was
/// swapped while the suite ran; and a failed re-evaluation of an enabled model switches it off.
/// </para>
/// <para>
/// The writer commits its own transaction, so each test inserts its own registry row with an
/// <c>eval-test/</c> model id and deletes it afterwards; audit rows remain, as the database
/// requires. Rows use <c>photo.embedding</c>, which no other suite enables, so the
/// one-enabled-per-task index cannot make suites in parallel test assemblies collide.
/// </para>
/// </summary>
public sealed class EvalHarnessWriterTests : IAsyncLifetime
{
    private readonly List<Guid> rows = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await SupabaseDatabase.ExecuteAsync(
            connection,
            null,
            "delete from public.ai_model_registry where id = any($1)",
            rows.ToArray());
    }

    private async Task<RegistryTarget> CreateRowAsync(
        string? license = null,
        string evalStatus = "unverified",
        bool enabled = false)
    {
        var id = Guid.CreateVersion7();
        rows.Add(id);

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await SupabaseDatabase.ExecuteAsync(
            connection,
            null,
            """
            insert into public.ai_model_registry
                (id, task_key, provider, model_id, license, eval_status, enabled)
            values ($1, 'photo.embedding', 'eval-provider', $2, $3, $4::model_eval_status, $5)
            """,
            id,
            "eval-test/" + id,
            license,
            evalStatus,
            enabled);

        return new RegistryTarget(id, "photo.embedding", "eval-provider", "eval-test/" + id, null, enabled);
    }

    private static RegistryResultWriter Writer() => new(SupabaseDatabase.ConnectionString);

    private static (Verdict, IReadOnlyList<MetricResult>) Judge(double recall, double falsePositives, double schemaValidity)
    {
        // A catalog whose schema-validity bar is set, so a full pass is reachable in the test.
        IReadOnlyList<EvalMetric> metrics =
        [
            new(MetricCatalog.FairHousingRecall, "recall", "SC-003", Comparison.AtLeast, 0.95),
            new(MetricCatalog.FairHousingFalsePositiveRate, "fp", "SC-003", Comparison.AtMost, 0.10),
            new(MetricCatalog.SchemaValidityRate, "schema", "FR-015", Comparison.AtLeast, 0.99),
        ];

        return EvalVerdict.Evaluate(metrics, new Dictionary<string, double>
        {
            [MetricCatalog.FairHousingRecall] = recall,
            [MetricCatalog.FairHousingFalsePositiveRate] = falsePositives,
            [MetricCatalog.SchemaValidityRate] = schemaValidity,
        });
    }

    private static async Task<(string Status, bool Enabled, string? Result)> ReadAsync(Guid id)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var command = new NpgsqlCommand(
            "select eval_status::text, enabled, eval_result::text from public.ai_model_registry where id = $1",
            connection);
        command.Parameters.AddWithValue(id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<long> AuditCountAsync(Guid id)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        return await SupabaseDatabase.ScalarAsync<long>(
            connection,
            null,
            """
            select count(*) from public.audit_events
            where action = 'registry_changed' and target_id = $1 and actor_id = $2
            """,
            id,
            SupabaseDatabase.PlatformAdmin);
    }

    [Fact]
    public async Task APass_IsRecorded_WithItsEvidence_AndAudited()
    {
        var target = await CreateRowAsync(license: "Apache-2.0");
        var (verdict, results) = Judge(0.97, 0.05, 1.0);

        Assert.Equal(Verdict.Passed, verdict);

        await Writer().WriteAsync(target, verdict, results, SupabaseDatabase.PlatformAdmin, DateTimeOffset.UtcNow, default);

        var stored = await ReadAsync(target.Id);
        Assert.Equal("passed", stored.Status);
        Assert.Contains(target.ModelId, stored.Result!, StringComparison.Ordinal);
        Assert.Contains(MetricCatalog.FairHousingRecall, stored.Result!, StringComparison.Ordinal);
        Assert.Equal(1, await AuditCountAsync(target.Id));
    }

    /// <summary>
    /// The harness records a pass; it does not enable. Enabling stays a separate, audited act by
    /// an administrator through the registry endpoint.
    /// </summary>
    [Fact]
    public async Task APass_DoesNotEnableTheModel()
    {
        var target = await CreateRowAsync(license: "Apache-2.0");
        var (verdict, results) = Judge(0.97, 0.05, 1.0);

        await Writer().WriteAsync(target, verdict, results, SupabaseDatabase.PlatformAdmin, DateTimeOffset.UtcNow, default);

        Assert.False((await ReadAsync(target.Id)).Enabled);
    }

    [Fact]
    public async Task AFailure_OfAnEnabledModel_DisablesIt_InTheSameAuditedChange()
    {
        var target = await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed", enabled: true);
        var (verdict, results) = Judge(0.80, 0.05, 1.0);

        Assert.Equal(Verdict.Failed, verdict);

        await Writer().WriteAsync(target, verdict, results, SupabaseDatabase.PlatformAdmin, DateTimeOffset.UtcNow, default);

        var stored = await ReadAsync(target.Id);
        Assert.Equal("failed", stored.Status);
        Assert.False(stored.Enabled);
        Assert.Equal(1, await AuditCountAsync(target.Id));
    }

    [Theory]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000001")] // broker admin — not a platform admin
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002")]
    public async Task ANonAdministratorActor_IsRefused_AndNothingIsWritten(string actor)
    {
        var target = await CreateRowAsync(license: "Apache-2.0");
        var (verdict, results) = Judge(0.97, 0.05, 1.0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().WriteAsync(
            target, verdict, results, Guid.Parse(actor), DateTimeOffset.UtcNow, default));

        Assert.Equal("unverified", (await ReadAsync(target.Id)).Status);
    }

    /// <summary>
    /// The suite evaluated one model; an administrator then pointed the row at another. The
    /// verdict describes the first, so it must not land on the second.
    /// </summary>
    [Fact]
    public async Task AModelSwappedDuringTheRun_IsNotStampedWithTheVerdict()
    {
        var target = await CreateRowAsync(license: "Apache-2.0");
        var (verdict, results) = Judge(0.97, 0.05, 1.0);

        await using (var connection = await SupabaseDatabase.OpenSuperuserAsync())
        {
            await SupabaseDatabase.ExecuteAsync(
                connection,
                null,
                "update public.ai_model_registry set model_id = $1 where id = $2",
                "eval-test/swapped-" + target.Id,
                target.Id);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().WriteAsync(
            target, verdict, results, SupabaseDatabase.PlatformAdmin, DateTimeOffset.UtcNow, default));

        Assert.Equal("unverified", (await ReadAsync(target.Id)).Status);
        Assert.Equal(0, await AuditCountAsync(target.Id));
    }

    [Fact]
    public async Task AnIncompleteVerdict_IsNeverRecorded()
    {
        var target = await CreateRowAsync(license: "Apache-2.0");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().WriteAsync(
            target, Verdict.Incomplete, [], SupabaseDatabase.PlatformAdmin, DateTimeOffset.UtcNow, default));

        Assert.Equal("unverified", (await ReadAsync(target.Id)).Status);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheRowUnderEvaluation()
    {
        var target = await CreateRowAsync();

        var loaded = await Writer().LoadAsync(target.Id, default);

        Assert.Equal(target, loaded);
        Assert.Null(await Writer().LoadAsync(Guid.CreateVersion7(), default));
    }
}
