namespace RealtorAI.Tests.Integration;

/// <summary>
/// T028 — Constitution Principle VIII.4: a model may be enabled only if its license is
/// recorded and it has passed the evaluation harness.
///
/// <para>
/// All four permutations are covered, not just the failing one. A check constraint written as
/// <c>enabled = false OR license IS NOT NULL</c> would pass a test that only tried the
/// both-missing case, while still allowing an unevaluated model to be switched on.
/// </para>
/// </summary>
public sealed class RegistryGateTests
{
    [Theory]
    // license, eval_status, may this row be enabled?
    [InlineData("apache-2.0", "passed", true)]
    [InlineData(null, "passed", false)]
    [InlineData("apache-2.0", "unverified", false)]
    [InlineData(null, "unverified", false)]
    [InlineData("apache-2.0", "failed", false)]
    public async Task EnablingAModel_RequiresBothLicenseAndPassingEval(
        string? license,
        string evalStatus,
        bool expectedToBeAllowed)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string insert = """
            insert into public.ai_model_registry
                (id, task_key, provider, model_id, license, eval_status, enabled)
            values ($1, 'photo.analysis', 'test-provider', $2, $3, $4::model_eval_status, true)
            """;

        // Each row gets a unique model id because a partial unique index allows only one
        // ENABLED row per task_key; without that the second successful insert would fail for
        // an unrelated reason and the test would be measuring the wrong rule.
        var parameters = new object?[]
        {
            Guid.NewGuid(),
            $"test/model-{Guid.NewGuid():N}",
            license,
            evalStatus,
        };

        if (expectedToBeAllowed)
        {
            var affected = await SupabaseDatabase.ExecuteAsync(
                connection,
                transaction,
                insert,
                parameters);

            Assert.Equal(1, affected);
        }
        else
        {
            await SupabaseDatabase.AssertRefusedAsync(
                connection,
                transaction,
                insert,
                "registry_enable_requires_license_and_eval",
                parameters);
        }

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The gate must also hold on UPDATE. A constraint is checked on both, but an
    /// implementation that moved this logic into an INSERT trigger would leave
    /// <c>update ... set enabled = true</c> wide open — which is the realistic path, since
    /// enabling a model is an edit to an existing row.
    /// </summary>
    [Fact]
    public async Task EnablingAnExistingUnverifiedModel_IsRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Every seeded row is enabled = false, eval_status = 'unverified', license null —
        // the correct starting state under VIII.4.
        var unverifiedId = await SupabaseDatabase.ScalarAsync<Guid?>(
            connection,
            transaction,
            """
            select id from public.ai_model_registry
            where enabled = false and eval_status = 'unverified' and license is null
            limit 1
            """);

        Assert.True(
            unverifiedId.HasValue,
            "Expected at least one seeded registry row in the unverified state. "
            + "0008_ai_platform.sql seeds seven.");

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            "update public.ai_model_registry set enabled = true where id = $1",
            "registry_enable_requires_license_and_eval",
            unverifiedId!.Value);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Records the state the repository actually ships in. If someone enables a model as part
    /// of unrelated work, that is a constitutional change (VIII.4) and should not pass review
    /// unnoticed.
    /// </summary>
    [Fact]
    public async Task NoModelIsEnabledInTheSeededRegistry()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var enabledCount = await SupabaseDatabase.ScalarAsync<long?>(
            connection,
            transaction: null,
            "select count(*) from public.ai_model_registry where enabled");

        Assert.True(
            enabledCount == 0,
            $"{enabledCount} model(s) are enabled. Enabling one requires a recorded license "
            + "and a passing evaluation run (VIII.4); if that has genuinely happened, update "
            + "this test and say so in the commit.");
    }

    /// <summary>
    /// At most one enabled row per task, so dispatch can never be ambiguous. Verified by
    /// trying to enable two properly-licensed, properly-evaluated rows for the same task —
    /// the only situation in which the index is reachable.
    /// </summary>
    [Fact]
    public async Task TwoEnabledModelsForTheSameTask_AreRefused()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string insert = """
            insert into public.ai_model_registry
                (id, task_key, provider, model_id, license, eval_status, enabled)
            values ($1, 'listing.content', 'test-provider', $2, 'apache-2.0', 'passed', true)
            """;

        await SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            insert,
            Guid.NewGuid(),
            $"test/model-{Guid.NewGuid():N}");

        await SupabaseDatabase.AssertRefusedAsync(
            connection,
            transaction,
            insert,
            "registry_one_enabled_per_task",
            Guid.NewGuid(),
            $"test/model-{Guid.NewGuid():N}");

        await transaction.RollbackAsync();
    }
}
