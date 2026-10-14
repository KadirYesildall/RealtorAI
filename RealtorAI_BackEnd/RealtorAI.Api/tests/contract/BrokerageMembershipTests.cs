using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T061 — brokerage and membership endpoints (FR-057, FR-060, FR-061, FR-062, FR-063).
///
/// <para>
/// <b>What is at stake.</b> A membership decides whether a broker admin can read an agent's
/// listings and audit trail (FR-050). Every endpoint here is therefore tested against the full
/// set of relationships: the brokerage's approved admin, a pending member, an approved ordinary
/// member, an admin of a different brokerage, a platform administrator (who has no brokerage
/// role at all), and an anonymous caller.
/// </para>
/// <para>
/// <b>Isolation.</b> Seeded personas are only read. Every flow that changes a membership or a
/// brokerage uses agents and brokerages this suite creates, because approving Agent B — the
/// seeded pending agent — would quietly break every other suite that depends on B being pending.
/// </para>
/// </summary>
public sealed class BrokerageMembershipTests : IClassFixture<ApiFactory>
{
    private const string Memberships = "/api/brokerages/memberships";

    private readonly ApiFactory factory;

    public BrokerageMembershipTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string BrokerageMemberships(Guid brokerageId) => $"/api/brokerages/{brokerageId}/memberships";

    private static string Decision(Guid brokerageId, Guid membershipId) =>
        $"/api/brokerages/{brokerageId}/memberships/{membershipId}/decision";

    private static string Settings(Guid brokerageId) => $"/api/brokerages/{brokerageId}/settings";

    private static JsonObject NewBrokerage(string name) => new()
    {
        ["brokerageName"] = name,
        ["brokerageLicenseNumber"] = "CA-DRE-" + Random.Shared.Next(10_000_000, 99_999_999),
        ["brokerageState"] = "CA",
    };

    private static JsonObject JoinPacificCrest() => new()
    {
        ["brokerageId"] = Seed.PacificCrest,
        ["brokerageName"] = "Pacific Crest Realty",
    };

    private async Task<JsonObject> DeclareAsync(Guid agentId, JsonObject body)
    {
        using var client = factory.CreateClientFor(agentId);

        var response = await client.PostAsJsonAsync(Memberships, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>A brokerage created by a fresh agent, who is therefore its approved admin.</summary>
    private async Task<(Guid Admin, Guid Brokerage)> CreateBrokerageAsync()
    {
        var admin = await TestAgents.CreateAsync("founder");
        var membership = await DeclareAsync(admin, NewBrokerage("Contract Realty " + Guid.NewGuid().ToString("N")[..6]));

        return (admin, membership["brokerageId"]!.GetValue<Guid>());
    }

    /// <summary>A fresh agent with a pending membership at Pacific Crest, which Agent A administers.</summary>
    private async Task<(Guid Agent, Guid Membership)> CreatePendingAtPacificCrestAsync()
    {
        var agent = await TestAgents.CreateAsync("applicant");
        var membership = await DeclareAsync(agent, JoinPacificCrest());

        return (agent, membership["id"]!.GetValue<Guid>());
    }

    private static async Task<long> AuditCountAsync(string action, Guid targetId) =>
        await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = $1::audit_action and target_id = $2",
            action,
            targetId);

    // ═════════════════════════════ POST /memberships — declare ═════════════════════════════

    [Fact]
    public async Task Declare_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(Memberships, JoinPacificCrest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>FR-058: license and state come first; a brokerage is declared on top of them.</summary>
    [Fact]
    public async Task Declare_WithAnIncompleteProfile_Is403()
    {
        var agent = await TestAgents.CreateAsync("incomplete", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Memberships, JoinPacificCrest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// FR-060: joining an existing brokerage is a claim, so it starts pending and is audited as
    /// a request — and nothing is decided.
    /// </summary>
    [Fact]
    public async Task Declare_JoiningAnExistingBrokerage_Is201Pending_AndAudited()
    {
        var agent = await TestAgents.CreateAsync("joiner");

        var membership = await DeclareAsync(agent, JoinPacificCrest());

        Assert.Equal("pending", membership["status"]!.GetValue<string>());
        Assert.Equal("agent", membership["role"]!.GetValue<string>());
        Assert.Equal(Seed.PacificCrest, membership["brokerageId"]!.GetValue<Guid>());
        Assert.Null(membership["decidedAt"]);
        Assert.False(membership["capabilities"]!["canApproveMarketing"]!.GetValue<bool>());
        Assert.False(membership["capabilities"]!["canSubmitToMls"]!.GetValue<bool>());

        var membershipId = membership["id"]!.GetValue<Guid>();
        Assert.Equal(1, await AuditCountAsync("membership_requested", membershipId));
        Assert.Equal(0, await AuditCountAsync("membership_decided", membershipId));
    }

    /// <summary>
    /// FR-062: the first registrant of a new brokerage becomes its approved broker admin — and
    /// because that is an approval, it is audited as a decision as well as a request.
    /// </summary>
    [Fact]
    public async Task Declare_ANewBrokerage_MakesTheRegistrantItsApprovedAdmin()
    {
        var founder = await TestAgents.CreateAsync("founder");

        var membership = await DeclareAsync(founder, NewBrokerage("Founders Realty"));

        Assert.Equal("approved", membership["status"]!.GetValue<string>());
        Assert.Equal("broker_admin", membership["role"]!.GetValue<string>());
        Assert.True(membership["capabilities"]!["canApproveMarketing"]!.GetValue<bool>());

        var membershipId = membership["id"]!.GetValue<Guid>();
        Assert.Equal(1, await AuditCountAsync("membership_requested", membershipId));
        Assert.Equal(1, await AuditCountAsync("membership_decided", membershipId));

        // The settings row exists from the start, with the database defaults.
        using var client = factory.CreateClientFor(founder);
        var settings = await client.GetFromJsonAsync<JsonObject>(Settings(membership["brokerageId"]!.GetValue<Guid>()));
        Assert.Equal("Founders Realty", settings!["name"]!.GetValue<string>());
        Assert.Equal("professional", settings["defaultTone"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declare_ASecondLiveMembership_Is409()
    {
        var (agent, _) = await CreatePendingAtPacificCrestAsync();
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Memberships, NewBrokerage("Second Realty"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>A brokerage cannot exist without the license and state its disclosures need (FR-021, FR-057).</summary>
    [Theory]
    [InlineData("brokerageLicenseNumber")]
    [InlineData("brokerageState")]
    public async Task Declare_ANewBrokerageWithoutLicenseOrState_Is422(string missing)
    {
        var agent = await TestAgents.CreateAsync("incomplete-brokerage");
        var body = NewBrokerage("Missing Realty");
        body.Remove(missing);

        using var client = factory.CreateClientFor(agent);
        var response = await client.PostAsJsonAsync(Memberships, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Declare_AnUnknownBrokerageId_Is422()
    {
        var agent = await TestAgents.CreateAsync("lost");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Memberships, new JsonObject
        {
            ["brokerageId"] = Guid.CreateVersion7(),
            ["brokerageName"] = "Nowhere Realty",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>
    /// The name must match the id: a mistyped or stale id should not quietly attach an agent to
    /// a brokerage they did not mean to declare.
    /// </summary>
    [Fact]
    public async Task Declare_WithANameThatDoesNotMatchTheId_Is422()
    {
        var agent = await TestAgents.CreateAsync("mismatch");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Memberships, new JsonObject
        {
            ["brokerageId"] = Seed.PacificCrest,
            ["brokerageName"] = "Harbor Point Properties",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ═════════════════════════════ GET /memberships — own ═════════════════════════════

    [Fact]
    public async Task GetOwn_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Memberships)).StatusCode);
    }

    [Fact]
    public async Task GetOwn_PendingAgent_SeesPending_WithNoCapabilities()
    {
        using var client = factory.CreateClientFor(Seed.AgentB);

        var membership = await client.GetFromJsonAsync<JsonObject>(Memberships);

        Assert.Equal("pending", membership!["status"]!.GetValue<string>());
        Assert.False(membership["capabilities"]!["canApproveMarketing"]!.GetValue<bool>());
        Assert.False(membership["capabilities"]!["canSubmitToMls"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetOwn_ApprovedAdmin_SeesBothCapabilities()
    {
        using var client = factory.CreateClientFor(Seed.AgentA);

        var membership = await client.GetFromJsonAsync<JsonObject>(Memberships);

        Assert.Equal("approved", membership!["status"]!.GetValue<string>());
        Assert.True(membership["capabilities"]!["canApproveMarketing"]!.GetValue<bool>());
        Assert.True(membership["capabilities"]!["canSubmitToMls"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetOwn_WithNoMembership_IsNull()
    {
        var agent = await TestAgents.CreateAsync("unaffiliated");
        using var client = factory.CreateClientFor(agent);

        var response = await client.GetAsync(Memberships);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    // ═════════════════════════════ GET /{brokerageId}/memberships ═════════════════════════════

    [Fact]
    public async Task List_AsTheApprovedAdmin_IncludesThePendingApplicantAndWhoTheyAre()
    {
        using var client = factory.CreateClientFor(Seed.AgentA);

        var rows = await client.GetFromJsonAsync<JsonArray>(BrokerageMemberships(Seed.PacificCrest) + "?status=pending");

        var agentB = rows!.OfType<JsonObject>().Single(row => row["agentId"]!.GetValue<Guid>() == Seed.AgentB);

        Assert.Equal("pending", agentB["status"]!.GetValue<string>());

        // The admin needs to know who is asking, and their license, to decide.
        Assert.False(string.IsNullOrEmpty(agentB["agentLicenseNumber"]!.GetValue<string>()));
        Assert.Equal("CA", agentB["agentLicenseState"]!.GetValue<string>());

        Assert.All(rows!.OfType<JsonObject>(), row => Assert.Equal("pending", row["status"]!.GetValue<string>()));
    }

    /// <summary>
    /// The cases a plausible implementation gets wrong: a pending member, an approved ordinary
    /// member, an admin of another brokerage, and a platform administrator — who administers
    /// models, not brokerages (FR-064).
    /// </summary>
    [Theory]
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002", "11110000-0000-4000-8000-000000000001")] // pending member
    [InlineData("cccccccc-0000-4000-8000-000000000003", "11110000-0000-4000-8000-000000000001")] // other brokerage
    [InlineData("cccccccc-0000-4000-8000-000000000003", "22220000-0000-4000-8000-000000000002")] // approved, not admin
    [InlineData("aaaaaaaa-0000-4000-8000-000000000001", "22220000-0000-4000-8000-000000000002")] // admin elsewhere
    [InlineData("dddddddd-0000-4000-8000-000000000004", "11110000-0000-4000-8000-000000000001")] // platform admin
    public async Task List_AsAnyoneButThatBrokeragesApprovedAdmin_Is403(string caller, string brokerage)
    {
        using var client = factory.CreateClientFor(Guid.Parse(caller));

        var response = await client.GetAsync(BrokerageMemberships(Guid.Parse(brokerage)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync(BrokerageMemberships(Seed.PacificCrest))).StatusCode);
    }

    // ═════════════════════════════ POST /decision ═════════════════════════════

    /// <summary>
    /// FR-062: the decision is audited with actor, timestamp and outcome — and FR-060: it takes
    /// effect at once, with no stale cached membership in the way.
    /// </summary>
    [Fact]
    public async Task Decide_Approve_RecordsTheDecider_AuditsIt_AndTakesEffectImmediately()
    {
        var (agent, membershipId) = await CreatePendingAtPacificCrestAsync();

        // Prime the agent's cached membership as pending.
        using (var agentClient = factory.CreateClientFor(agent))
        {
            Assert.Equal("pending", (await agentClient.GetFromJsonAsync<JsonObject>(Memberships))!["status"]!.GetValue<string>());
        }

        using var admin = factory.CreateClientFor(Seed.AgentA);
        var response = await admin.PostAsJsonAsync(
            Decision(Seed.PacificCrest, membershipId),
            new JsonObject { ["decision"] = "approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decided = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("approved", decided["status"]!.GetValue<string>());
        Assert.NotNull(decided["decidedAt"]);

        Assert.Equal(Seed.AgentA, await TestDatabase.ScalarAsync<Guid>(
            "select decided_by from public.brokerage_memberships where id = $1", membershipId));

        // Audit: actor, outcome, and the transition.
        await using var connection = await TestDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select actor_id, brokerage_id, before_value->>'status', after_value->>'status', created_at
            from public.audit_events
            where action = 'membership_decided' and target_id = $1
            """;
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = membershipId });
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "The decision was not audited.");
        Assert.Equal(Seed.AgentA, reader.GetGuid(0));
        Assert.Equal(Seed.PacificCrest, reader.GetGuid(1));
        Assert.Equal("pending", reader.GetString(2));
        Assert.Equal("approved", reader.GetString(3));
        Assert.False(reader.IsDBNull(4));

        // Immediately visible to the agent, despite the primed cache.
        using var after = factory.CreateClientFor(agent);
        Assert.Equal("approved", (await after.GetFromJsonAsync<JsonObject>(Memberships))!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Decide_Reject_IsAudited_AndGrantsNothing()
    {
        var (agent, membershipId) = await CreatePendingAtPacificCrestAsync();

        using var admin = factory.CreateClientFor(Seed.AgentA);
        var response = await admin.PostAsJsonAsync(
            Decision(Seed.PacificCrest, membershipId),
            new JsonObject { ["decision"] = "rejected" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await AuditCountAsync("membership_decided", membershipId));

        using var rejected = factory.CreateClientFor(agent);
        var own = await rejected.GetFromJsonAsync<JsonObject>(Memberships);
        Assert.Equal("rejected", own!["status"]!.GetValue<string>());
        Assert.False(own["capabilities"]!["canApproveMarketing"]!.GetValue<bool>());

        // A rejected agent may declare again; rejection is history, not a live membership.
        using var retry = factory.CreateClientFor(agent);
        Assert.Equal(
            HttpStatusCode.Created,
            (await retry.PostAsJsonAsync(Memberships, NewBrokerage("Second Chance Realty"))).StatusCode);
    }

    [Fact]
    public async Task Decide_AMembershipThatIsNotPending_Is409()
    {
        var (_, membershipId) = await CreatePendingAtPacificCrestAsync();
        using var admin = factory.CreateClientFor(Seed.AgentA);

        await admin.PostAsJsonAsync(Decision(Seed.PacificCrest, membershipId), new JsonObject { ["decision"] = "approved" });
        var again = await admin.PostAsJsonAsync(Decision(Seed.PacificCrest, membershipId), new JsonObject { ["decision"] = "rejected" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// A membership addressed through a brokerage it does not belong to does not exist there —
    /// an admin cannot decide another brokerage's applicants by putting their own brokerage id
    /// in the path.
    /// </summary>
    [Fact]
    public async Task Decide_AMembershipOfAnotherBrokerage_Is404()
    {
        var (otherAdmin, otherBrokerage) = await CreateBrokerageAsync();
        _ = otherAdmin;
        var applicant = await TestAgents.CreateAsync("elsewhere");
        var membership = await DeclareAsync(applicant, new JsonObject
        {
            ["brokerageId"] = otherBrokerage,
            ["brokerageName"] = (await TestDatabase.ScalarAsync<string>("select name from public.brokerages where id = $1", otherBrokerage))!,
        });

        using var admin = factory.CreateClientFor(Seed.AgentA);
        var response = await admin.PostAsJsonAsync(
            Decision(Seed.PacificCrest, membership["id"]!.GetValue<Guid>()),
            new JsonObject { ["decision"] = "approved" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("pending", await TestDatabase.ScalarAsync<string>(
            "select status::text from public.brokerage_memberships where id = $1",
            membership["id"]!.GetValue<Guid>()));
    }

    [Theory]
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002")] // pending member of the same brokerage
    [InlineData("cccccccc-0000-4000-8000-000000000003")] // approved agent elsewhere
    [InlineData("dddddddd-0000-4000-8000-000000000004")] // platform admin
    public async Task Decide_AsAnyoneButAnApprovedAdmin_Is403_AndChangesNothing(string caller)
    {
        var (_, membershipId) = await CreatePendingAtPacificCrestAsync();
        using var client = factory.CreateClientFor(Guid.Parse(caller));

        var response = await client.PostAsJsonAsync(
            Decision(Seed.PacificCrest, membershipId),
            new JsonObject { ["decision"] = "approved" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("pending", await TestDatabase.ScalarAsync<string>(
            "select status::text from public.brokerage_memberships where id = $1", membershipId));
        Assert.Equal(0, await AuditCountAsync("membership_decided", membershipId));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("revoked")]
    [InlineData("maybe")]
    public async Task Decide_WithAnythingButApprovedOrRejected_Is422(string decision)
    {
        var (_, membershipId) = await CreatePendingAtPacificCrestAsync();
        using var admin = factory.CreateClientFor(Seed.AgentA);

        var response = await admin.PostAsJsonAsync(
            Decision(Seed.PacificCrest, membershipId),
            new JsonObject { ["decision"] = decision });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ═════════════════════════════ GET/PUT /settings ═════════════════════════════

    [Theory]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000001", "11110000-0000-4000-8000-000000000001")] // admin
    [InlineData("cccccccc-0000-4000-8000-000000000003", "22220000-0000-4000-8000-000000000002")] // approved agent
    public async Task GetSettings_AsAnApprovedMember_Is200(string caller, string brokerage)
    {
        using var client = factory.CreateClientFor(Guid.Parse(caller));

        var settings = await client.GetFromJsonAsync<JsonObject>(Settings(Guid.Parse(brokerage)));

        Assert.Equal(Guid.Parse(brokerage), settings!["brokerageId"]!.GetValue<Guid>());
        Assert.Equal("CRMLS", settings["targetMlsId"]!.GetValue<string>());
        Assert.IsType<JsonArray>(settings["requiredDisclaimers"]);
    }

    [Theory]
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002", "11110000-0000-4000-8000-000000000001")] // pending
    [InlineData("cccccccc-0000-4000-8000-000000000003", "11110000-0000-4000-8000-000000000001")] // other brokerage
    [InlineData("dddddddd-0000-4000-8000-000000000004", "11110000-0000-4000-8000-000000000001")] // platform admin
    public async Task GetSettings_AsAnyoneElse_Is403(string caller, string brokerage)
    {
        using var client = factory.CreateClientFor(Guid.Parse(caller));

        var response = await client.GetAsync(Settings(Guid.Parse(brokerage)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutSettings_AsTheAdmin_PersistsEveryConfigurableField()
    {
        var (admin, brokerage) = await CreateBrokerageAsync();
        using var client = factory.CreateClientFor(admin);

        var body = (await client.GetFromJsonAsync<JsonObject>(Settings(brokerage)))!;
        body["name"] = "Renamed Realty";
        body["licenseNumber"] = "CA-DRE-02000000";
        body["state"] = "CA";
        body["requiredDisclaimers"] = new JsonArray("Renamed Realty, DRE #02000000.");
        body["targetMlsId"] = "CRMLS";
        body["defaultTone"] = "luxury";
        body["aiBudgetTokens"] = 150_000;
        body["aiBudgetImages"] = 90;

        var response = await client.PutAsJsonAsync(Settings(brokerage), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = (await client.GetFromJsonAsync<JsonObject>(Settings(brokerage)))!;
        Assert.Equal("Renamed Realty", stored["name"]!.GetValue<string>());
        Assert.Equal("CA-DRE-02000000", stored["licenseNumber"]!.GetValue<string>());
        Assert.Equal("luxury", stored["defaultTone"]!.GetValue<string>());
        Assert.Equal(150_000, stored["aiBudgetTokens"]!.GetValue<int>());
        Assert.Equal(90, stored["aiBudgetImages"]!.GetValue<int>());
        Assert.Equal("Renamed Realty, DRE #02000000.", stored["requiredDisclaimers"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// FR-057: only an approved admin configures a brokerage. An approved ordinary member of the
    /// same brokerage can read the settings but not change them.
    /// </summary>
    [Fact]
    public async Task PutSettings_AsAnApprovedNonAdminMember_Is403()
    {
        var (admin, brokerage) = await CreateBrokerageAsync();
        var member = await TestAgents.CreateAsync("member");
        var name = (await TestDatabase.ScalarAsync<string>("select name from public.brokerages where id = $1", brokerage))!;
        var membership = await DeclareAsync(member, new JsonObject { ["brokerageId"] = brokerage, ["brokerageName"] = name });

        using (var adminClient = factory.CreateClientFor(admin))
        {
            var decided = await adminClient.PostAsJsonAsync(
                Decision(brokerage, membership["id"]!.GetValue<Guid>()),
                new JsonObject { ["decision"] = "approved" });
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        }

        using var client = factory.CreateClientFor(member);
        var body = (await client.GetFromJsonAsync<JsonObject>(Settings(brokerage)))!;
        body["name"] = "Hijacked Realty";

        var response = await client.PutAsJsonAsync(Settings(brokerage), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(name, await TestDatabase.ScalarAsync<string>("select name from public.brokerages where id = $1", brokerage));
    }

    [Theory]
    [InlineData("bbbbbbbb-0000-4000-8000-000000000002")] // pending
    [InlineData("cccccccc-0000-4000-8000-000000000003")] // other brokerage
    [InlineData("dddddddd-0000-4000-8000-000000000004")] // platform admin
    public async Task PutSettings_AsAnyoneButTheAdmin_Is403(string caller)
    {
        using var admin = factory.CreateClientFor(Seed.AgentA);
        var body = (await admin.GetFromJsonAsync<JsonObject>(Settings(Seed.PacificCrest)))!;
        body["name"] = "Taken Over Realty";

        using var client = factory.CreateClientFor(Guid.Parse(caller));
        var response = await client.PutAsJsonAsync(Settings(Seed.PacificCrest), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "Pacific Crest Realty",
            await TestDatabase.ScalarAsync<string>("select name from public.brokerages where id = $1", Seed.PacificCrest));
    }

    [Theory]
    [InlineData("state", "California")]
    [InlineData("name", " ")]
    [InlineData("licenseNumber", "")]
    [InlineData("targetMlsId", "NOT-AN-MLS")]
    [InlineData("defaultTone", "sarcastic")]
    [InlineData("aiBudgetTokens", "-1")]
    [InlineData("aiBudgetImages", "0")]
    public async Task PutSettings_WithAnInvalidField_Is422(string field, string value)
    {
        var (admin, brokerage) = await CreateBrokerageAsync();
        using var client = factory.CreateClientFor(admin);
        var body = (await client.GetFromJsonAsync<JsonObject>(Settings(brokerage)))!;

        body[field] = field.StartsWith("aiBudget", StringComparison.Ordinal)
            ? JsonValue.Create(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            : JsonValue.Create(value);

        var response = await client.PutAsJsonAsync(Settings(brokerage), body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
