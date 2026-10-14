using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T051 — <c>GET</c> and <c>PUT /api/admin/ai/registry</c> (FR-064, Principle VIII.2/VIII.4).
///
/// <para>
/// <b>Why this endpoint gets the most suspicious test suite in the API.</b> The registry decides
/// whether a model may run at all. A hole here does not leak one listing; it lets someone switch
/// every photo in the system onto an unlicensed or unevaluated model. So the suite asserts three
/// things separately: that only a platform administrator reaches it (the authorization matrix),
/// that the VIII.4 gate cannot be talked around through the API (enabling, self-certifying an
/// evaluation, or swapping the model under an existing pass), and that every change that does
/// happen leaves an attributed <c>registry_changed</c> audit record.
/// </para>
///
/// <para>
/// <b>Isolation.</b> Requests run in the API's own database connection, so a test transaction
/// cannot wrap them. Each test therefore works only on registry rows it inserts itself — marked
/// by a <c>contract-test/</c> model id — and deletes them afterwards. The seeded rows are read
/// but never modified. Audit rows the API writes are left in place, because the database
/// refuses to delete them; that is the guarantee under test elsewhere, not a cleanup problem.
/// </para>
/// <para>
/// Enable-path tests use <c>compliance.judge</c>, which no other suite enables, so the
/// one-enabled-row-per-task index cannot make these tests contend with the integration suite.
/// </para>
/// </summary>
public sealed class AiRegistryEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Route = "/api/admin/ai/registry";
    private const string IsolatedTask = "compliance.judge";

    private readonly ApiFactory factory;
    private readonly List<Guid> createdRows = [];

    public AiRegistryEndpointTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (createdRows.Count == 0)
        {
            return;
        }

        // Fallback references first, so no row is left pointing at one being removed.
        await TestDatabase.ExecuteAsync(
            "update public.ai_model_registry set fallback_registry_id = null where id = any($1)",
            createdRows.ToArray());
        await TestDatabase.ExecuteAsync(
            "delete from public.ai_model_registry where id = any($1)",
            createdRows.ToArray());
    }

    // ───────────────────────────── Arrangement ─────────────────────────────

    /// <summary>
    /// Inserts a registry row directly, standing in for the evaluation harness when a test
    /// needs a row whose evaluation has already passed.
    /// </summary>
    private async Task<Guid> CreateRowAsync(
        string taskKey = IsolatedTask,
        string? license = null,
        string evalStatus = "unverified",
        bool enabled = false)
    {
        var id = Guid.CreateVersion7();
        createdRows.Add(id);

        await TestDatabase.ExecuteAsync(
            """
            insert into public.ai_model_registry
                (id, task_key, provider, model_id, parameters, license, eval_status, eval_result,
                 enabled, verified_at)
            values ($1, $2, 'contract-provider', $3, '{"temperature": 0}'::jsonb, $4,
                    $5::model_eval_status,
                    case when $5 = 'passed' then '{"recall": 0.97}'::jsonb end,
                    $6,
                    case when $5 = 'passed' then timezone('utc', now()) end)
            """,
            id,
            taskKey,
            "contract-test/" + id,
            license,
            evalStatus,
            enabled);

        return id;
    }

    private async Task<JsonObject> GetRowAsync(Guid id)
    {
        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var rows = await client.GetFromJsonAsync<JsonArray>(Route);

        Assert.NotNull(rows);

        return rows!
            .OfType<JsonObject>()
            .Single(row => row["id"]!.GetValue<Guid>() == id);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, JsonObject body) =>
        client.PutAsJsonAsync(Route, body);

    private static async Task<long> CountRegistryAuditAsync(Guid rowId) =>
        await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'registry_changed' and target_id = $1
            """,
            rowId);

    // ═════════════════════════════ Authorization matrix ═════════════════════════════

    [Fact]
    public async Task Get_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Every non-administrator relationship the product distinguishes. A broker admin is the
    /// case most worth naming: they administer something, and a plausible implementation could
    /// confuse "an admin" with "the admin". FR-064 makes them unrelated roles.
    /// </summary>
    [Theory]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000001")] // approved broker_admin
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002")] // pending agent
    [InlineData("cccccccc-0000-4000-8000-000000000003")] // approved agent, other brokerage
    public async Task Get_AsNonPlatformAdmin_Is403(string agentId)
    {
        using var client = factory.CreateClientFor(Guid.Parse(agentId));

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A correctly signed token for a subject with no profile row. The handler must deny it
    /// rather than treat "no row" as anything other than "not an administrator".
    /// </summary>
    [Fact]
    public async Task Get_AsUnknownSubject_Is403()
    {
        using var client = factory.CreateClientFor(Guid.CreateVersion7());

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsPlatformAdmin_Is200()
    {
        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Proves the fixture substitutes only the key: a token the real validation would reject
    /// for its issuer, audience, lifetime or signature is still rejected, even when it names
    /// the platform administrator.
    /// </summary>
    [Fact]
    public async Task Get_WithTokensFailingValidation_Is401()
    {
        var invalidTokens = new[]
        {
            TestTokens.Create(Seed.PlatformAdmin, expires: DateTime.UtcNow.AddMinutes(-5)),
            TestTokens.Create(Seed.PlatformAdmin, audience: "anon"),
            TestTokens.Create(Seed.PlatformAdmin, issuer: "https://attacker.example/auth/v1"),
            TestTokens.Create(Seed.PlatformAdmin, signingKey: TestTokens.CreateForeignKey()),
        };

        foreach (var token in invalidTokens)
        {
            using var client = factory.CreateClientWithToken(token);

            var response = await client.GetAsync(Route);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Put_Anonymous_Is401_AndChangesNothing()
    {
        var id = await CreateRowAsync();
        using var client = factory.CreateAnonymousClient();

        var body = await GetRowAsync(id);
        body["parameters"] = new JsonObject { ["temperature"] = 0.9 };

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, (await GetRowAsync(id))["parameters"]!["temperature"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000001")]
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002")]
    [InlineData("cccccccc-0000-4000-8000-000000000003")]
    public async Task Put_AsNonPlatformAdmin_Is403_ChangesNothing_AndAuditsNothing(string agentId)
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["parameters"] = new JsonObject { ["temperature"] = 0.9 };

        using var client = factory.CreateClientFor(Guid.Parse(agentId));

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, (await GetRowAsync(id))["parameters"]!["temperature"]!.GetValue<double>());
        Assert.Equal(0, await CountRegistryAuditAsync(id));
    }

    // ═════════════════════════════ GET shape ═════════════════════════════

    /// <summary>
    /// The seven seeded candidates are present, disabled and unverified — the correct resting
    /// state until each passes evaluation — and the jsonb columns come back as JSON objects
    /// rather than as strings containing JSON.
    /// </summary>
    [Fact]
    public async Task Get_ReturnsSeededEntries_InTheContractShape()
    {
        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var rows = await client.GetFromJsonAsync<JsonArray>(Route);

        Assert.NotNull(rows);

        var seeded = rows!
            .OfType<JsonObject>()
            .Where(row => !row["modelId"]!.GetValue<string>().StartsWith("contract-test/", StringComparison.Ordinal)
                && row["provider"]!.GetValue<string>() != "test-provider")
            .ToList();

        Assert.Equal(7, seeded.Count);

        foreach (var row in seeded)
        {
            Assert.False(row["enabled"]!.GetValue<bool>());
            Assert.Equal("unverified", row["evalStatus"]!.GetValue<string>());
            Assert.Equal(JsonValueKind.Object, row["parameters"]!.GetValueKind());

            foreach (var property in new[]
                     {
                         "id", "taskKey", "provider", "modelId", "endpoint", "parameters",
                         "fallbackRegistryId", "license", "evalStatus", "evalResult", "enabled",
                         "verifiedAt",
                     })
            {
                Assert.True(row.ContainsKey(property), $"RegistryEntry is missing '{property}'.");
            }
        }

        Assert.Contains(seeded, row => row["taskKey"]!.GetValue<string>() == "photo.analysis");
        Assert.Contains(seeded, row => row["taskKey"]!.GetValue<string>() == "compliance.judge");
    }

    // ═════════════════════════════ PUT: permitted changes ═════════════════════════════

    [Fact]
    public async Task Put_ParametersChange_Is200_PersistsAndAuditsWithTheAdministrator()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["parameters"] = new JsonObject { ["temperature"] = 0.4, ["max_tokens"] = 900 };

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await GetRowAsync(id);
        Assert.Equal(0.4, stored["parameters"]!["temperature"]!.GetValue<double>());
        Assert.Equal(900, stored["parameters"]!["max_tokens"]!.GetValue<int>());

        // One attributed record carrying both states, so the trail shows what was replaced.
        await using var connection = await TestDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select actor_id, target_type, before_value::text, after_value::text, payload_hash
            from public.audit_events
            where action = 'registry_changed' and target_id = $1
            """;
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "No registry_changed audit record was written.");
        Assert.Equal(Seed.PlatformAdmin, reader.GetGuid(0));
        Assert.Equal("ai_model_registry", reader.GetString(1));
        Assert.Contains("\"temperature\": 0", reader.GetString(2), StringComparison.Ordinal);
        Assert.Contains("0.4", reader.GetString(3), StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", reader.GetString(4));
        Assert.False(await reader.ReadAsync(), "A single change produced more than one audit record.");
    }

    /// <summary>
    /// Recording a license is the administrator's half of VIII.4; it is audited like any other
    /// change because it is the claim enablement will later rest on.
    /// </summary>
    [Fact]
    public async Task Put_RecordingALicense_Is200_AndAudited()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["license"] = "Apache-2.0";

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Apache-2.0", (await GetRowAsync(id))["license"]!.GetValue<string>());
        Assert.Equal(1, await CountRegistryAuditAsync(id));
    }

    [Fact]
    public async Task Put_EnablingALicensedAndPassedEntry_Is200()
    {
        var id = await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed");
        var body = await GetRowAsync(id);
        body["enabled"] = true;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await GetRowAsync(id))["enabled"]!.GetValue<bool>());
        Assert.Equal(1, await CountRegistryAuditAsync(id));
    }

    /// <summary>
    /// A no-op PUT (the GET payload sent straight back) succeeds and records nothing: an audit
    /// trail full of empty changes buries the real ones.
    /// </summary>
    [Fact]
    public async Task Put_WithNoChanges_Is200_AndAuditsNothing()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CountRegistryAuditAsync(id));
    }

    // ═════════════════════════════ PUT: the VIII.4 gate ═════════════════════════════

    [Fact]
    public async Task Put_EnablingWithoutLicenseOrPassingEval_Is422_AndChangesNothing()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["enabled"] = true;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False((await GetRowAsync(id))["enabled"]!.GetValue<bool>());
        Assert.Equal(0, await CountRegistryAuditAsync(id));
    }

    /// <summary>
    /// A license alone is not enough; the evaluation must have passed too.
    /// </summary>
    [Fact]
    public async Task Put_EnablingWithLicenseButFailedEval_Is422()
    {
        var id = await CreateRowAsync(license: "Apache-2.0", evalStatus: "failed");
        var body = await GetRowAsync(id);
        body["enabled"] = true;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False((await GetRowAsync(id))["enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// The evaluation result is written by the harness, not asserted by a person. If the API
    /// accepted <c>evalStatus: passed</c>, an administrator could satisfy the database check by
    /// typing the word, and VIII.4's "passes the evaluation harness" would mean nothing.
    /// </summary>
    [Fact]
    public async Task Put_SettingEvalStatusThroughTheApi_Is422()
    {
        var id = await CreateRowAsync(license: "Apache-2.0");
        var body = await GetRowAsync(id);
        body["evalStatus"] = "passed";
        body["evalResult"] = new JsonObject { ["recall"] = 1.0 };

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unverified", (await GetRowAsync(id))["evalStatus"]!.GetValue<string>());
        Assert.Equal(0, await CountRegistryAuditAsync(id));
    }

    /// <summary>
    /// An evaluation pass belongs to the model that was evaluated. Swapping the model id under
    /// a passed row clears the pass and disables the row, so a new model cannot inherit an old
    /// model's evaluation.
    /// </summary>
    [Fact]
    public async Task Put_ChangingTheModel_ResetsEvaluationAndDisables()
    {
        var id = await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed", enabled: true);
        var body = await GetRowAsync(id);
        body["modelId"] = "contract-test/replacement-" + id;
        body["enabled"] = false;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await GetRowAsync(id);
        Assert.Equal("unverified", stored["evalStatus"]!.GetValue<string>());
        Assert.Null(stored["evalResult"]);
        Assert.Null(stored["verifiedAt"]);
        Assert.False(stored["enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// Asking to keep a row enabled while changing its model is refused outright rather than
    /// quietly disabled: the administrator asked for something the gate forbids, and should be
    /// told rather than discover it later from a 503.
    /// </summary>
    [Fact]
    public async Task Put_ChangingTheModelWhileKeepingItEnabled_Is422()
    {
        var id = await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed", enabled: true);
        var body = await GetRowAsync(id);
        body["provider"] = "another-provider";

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var stored = await GetRowAsync(id);
        Assert.Equal("contract-provider", stored["provider"]!.GetValue<string>());
        Assert.True(stored["enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// The registry allows one enabled model per task so dispatch is never ambiguous. A second
    /// one is a conflict with existing state, not a malformed request.
    /// </summary>
    [Fact]
    public async Task Put_EnablingASecondEntryForTheSameTask_Is409()
    {
        await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed", enabled: true);
        var second = await CreateRowAsync(license: "Apache-2.0", evalStatus: "passed");
        var body = await GetRowAsync(second);
        body["enabled"] = true;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetRowAsync(second))["enabled"]!.GetValue<bool>());
        Assert.Equal(0, await CountRegistryAuditAsync(second));
    }

    // ═════════════════════════════ PUT: validation ═════════════════════════════

    [Fact]
    public async Task Put_UnknownId_Is404()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["id"] = Guid.CreateVersion7();

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The task key is the row's identity within the registry. Moving a row to another task
    /// would carry its evaluation to a task it was never evaluated for.
    /// </summary>
    [Fact]
    public async Task Put_ChangingTheTaskKey_Is422()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["taskKey"] = "listing.content";

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(IsolatedTask, (await GetRowAsync(id))["taskKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Put_FallbackToItself_Is422()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["fallbackRegistryId"] = id;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>
    /// A fallback must serve the same task; a content model is not a fallback for a vision one.
    /// </summary>
    [Fact]
    public async Task Put_FallbackForADifferentTask_Is422()
    {
        var id = await CreateRowAsync();
        var otherTask = await CreateRowAsync(taskKey: "listing.content");
        var body = await GetRowAsync(id);
        body["fallbackRegistryId"] = otherTask;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Put_FallbackToSameTask_Is200()
    {
        var id = await CreateRowAsync();
        var fallback = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["fallbackRegistryId"] = fallback;

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(fallback, (await GetRowAsync(id))["fallbackRegistryId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task Put_ParametersThatAreNotAnObject_Is422()
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body["parameters"] = new JsonArray(1, 2, 3);

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("modelId")]
    public async Task Put_BlankIdentityField_Is422(string field)
    {
        var id = await CreateRowAsync();
        var body = await GetRowAsync(id);
        body[field] = "  ";

        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await PutAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
