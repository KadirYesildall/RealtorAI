using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T119 — <c>POST /compliance/{findingId}/acknowledge</c> (FR-022, FR-023; Story 4 scenarios 8, 9).
///
/// <para>
/// <b>What is at stake.</b> A warning may be accepted, with a reason, an actor and a time on the
/// record. A blocking finding may not, by any route — the only way past it is to change the
/// content. 422, not 403: the request is understood and the agent is entitled to make it of a
/// warning; this finding simply is not one.
/// </para>
/// </summary>
public sealed class AcknowledgeTests : IClassFixture<ApiFactory>
{
    private const string Reason = "The church is a landmark the buyer asked about.";

    private readonly ApiFactory factory;

    public AcknowledgeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Acknowledge_AWarning_RecordsTheReasonActorAndTime()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "ack-warning", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.religion.distance_to_worship");

        var response = await PostAsync(agent, listing, IdOf(finding), Reason);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acknowledged", (await response.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.compliance_findings
            where id = $1 and status = 'acknowledged' and acknowledged_reason = $2 and acknowledged_by = $3 and acknowledged_at is not null
            """,
            IdOf(finding),
            Reason,
            agent));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'finding_acknowledged' and target_id = $1 and actor_id = $2 and after_value ->> 'reason' = $3
            """,
            IdOf(finding),
            agent,
            Reason));
    }

    /// <summary>FR-022 at the API: 422, and nothing changes.</summary>
    [Fact]
    public async Task Acknowledge_ABlockingFinding_Is422_AndLeavesItOpen()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "ack-blocking", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families");

        var response = await PostAsync(agent, listing, IdOf(finding), Reason);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("open", await TestDatabase.ScalarAsync<string>("select status::text from public.compliance_findings where id = $1", IdOf(finding)));
    }

    /// <summary>An acknowledgement survives a re-run that finds the same thing in the same words.</summary>
    [Fact]
    public async Task Acknowledgement_SurvivesARerunOverTheSameWords()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "ack-rerun", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.religion.distance_to_worship");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(agent, listing, IdOf(finding), Reason)).StatusCode);

        var findings = await CheckAsync(factory, agent, listing);

        Assert.Equal("acknowledged", findings.Single(candidate => IdOf(candidate!.AsObject()) == IdOf(finding))!["status"]!.GetValue<string>());
        Assert.DoesNotContain(Open(findings), candidate => candidate["ruleId"]!.GetValue<string>() == "fair_housing.religion.distance_to_worship");
    }

    [Theory]
    [InlineData("""{ "reason": "too short" }""")]
    [InlineData("""{ "reason": "            " }""")]
    [InlineData("""{ }""")]
    public async Task Acknowledge_WithoutAReasonOfTenCharacters_Is422(string body)
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "ack-reason", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.religion.distance_to_worship");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsync(Acknowledge(listing, IdOf(finding)), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_Twice_Is409()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "ack-twice", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.religion.distance_to_worship");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(agent, listing, IdOf(finding), Reason)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(agent, listing, IdOf(finding), Reason)).StatusCode);
    }

    [Fact]
    public async Task Acknowledge_ByTheApprovedBrokerAdmin_Is403_AndAnUnknownFinding_404()
    {
        var admin = await TestAgents.CreateAsync("ack-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("ack-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, member));

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(admin, listing, Guid.NewGuid(), Reason)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(member, listing, Guid.NewGuid(), Reason)).StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid caller, Guid listing, Guid finding, string reason)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PostAsJsonAsync(Acknowledge(listing, finding), new JsonObject { ["reason"] = reason });
    }
}
