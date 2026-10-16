using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T071 — an agent whose brokerage membership is still <c>pending</c> can do the whole of a
/// listing's private preparation, and the brokerage still sees none of it (FR-050, FR-061).
///
/// <para>
/// FR-061 is two promises at once, and a test of only one of them would pass an implementation
/// that broke the other: the permissive half (a pending agent is not made to wait for an admin
/// before working) and the protective half (the brokerage acquires no visibility into work by
/// someone it has not accepted). Both are exercised here over one listing, end to end through
/// the API.
/// </para>
/// <para>
/// <b>Scope.</b> This covers the US1 operations: create, autosave, upload, field entry and
/// confirmation. Starting AI analysis under a pending membership is the same permission
/// (<c>Capabilities.CanPrepareListing</c>) applied by the pipeline endpoint, and is asserted in
/// <c>PipelineStartTests</c> (T084), where that endpoint is specified.
/// </para>
/// </summary>
public sealed class PendingMembershipScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public PendingMembershipScopeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task APendingAgent_PreparesAListingEndToEnd_AndTheBrokerageSeesNoneOfIt()
    {
        var admin = await TestAgents.CreateAsync("scope-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var pending = await TestAgents.CreateAsync("scope-pending");
        await TestBrokerages.AddMemberAsync(pending, brokerage, "pending");

        using var agent = factory.CreateClientFor(pending);

        // ── Create ──
        var created = await agent.PostAsJsonAsync(Listings, Basics());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listing = IdOf((await created.Content.ReadFromJsonAsync<JsonObject>())!);

        // ── Autosave ──
        var edit = Basics();
        edit["listPrice"] = 1_525_000;
        using (var patch = Conditional(HttpMethod.Patch, Listing(listing), edit, ifMatch: 1))
        {
            Assert.Equal(HttpStatusCode.OK, (await agent.SendAsync(patch)).StatusCode);
        }

        // ── Upload ──
        using (var form = Files(("front.jpg", TestImages.Jpeg(), "image/jpeg")))
        {
            var upload = await agent.PostAsync(Photos(listing), form);
            Assert.Equal(HttpStatusCode.MultiStatus, upload.StatusCode);
            Assert.True((await upload.Content.ReadFromJsonAsync<JsonArray>())![0]!["accepted"]!.GetValue<bool>());
        }

        // ── Fields, including confirming an AI-derived one ──
        var field = new JsonObject
        {
            ["resoFieldKey"] = "InteriorFeatures",
            ["value"] = new JsonArray("Fireplace"),
            ["provenance"] = "ai_detected_from_photo",
        };

        Guid fieldId;
        using (var put = Conditional(HttpMethod.Put, Fields(listing), field, ifMatch: 0))
        {
            var response = await agent.SendAsync(put);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            fieldId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        }

        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync(Confirm(listing, fieldId), content: null)).StatusCode);

        // ── The draft resumes with everything in it ──
        var detail = (await agent.GetFromJsonAsync<JsonObject>(Listing(listing)))!;
        Assert.Equal(1_525_000m, detail["listPrice"]!.GetValue<decimal>());
        Assert.Single(detail["photos"]!.AsArray());
        Assert.True(detail["fields"]!.AsArray().Single()!["confirmed"]!.GetValue<bool>());

        // ── The brokerage that has not approved this agent sees nothing ──
        using var brokerageAdmin = factory.CreateClientFor(admin);

        Assert.Equal(HttpStatusCode.NotFound, (await brokerageAdmin.GetAsync(Listing(listing))).StatusCode);
        Assert.DoesNotContain(
            listing,
            (await brokerageAdmin.GetFromJsonAsync<JsonArray>(Listings))!.Select(row => IdOf(row!.AsObject())));
    }

    /// <summary>
    /// Approval changes what the brokerage may see, from the very next request (FR-060): the
    /// listing filed under the brokerage while pending becomes readable — read-only — once the
    /// agent is approved.
    /// </summary>
    [Fact]
    public async Task OnceApproved_TheBrokerAdminCanReadButNotEditTheListing()
    {
        var admin = await TestAgents.CreateAsync("scope-approve-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var agentId = await TestAgents.CreateAsync("scope-approve-agent");
        await TestBrokerages.AddMemberAsync(agentId, brokerage, "pending");
        var listing = IdOf(await CreateAsync(factory, agentId));

        using var brokerageAdmin = factory.CreateClientFor(admin);
        Assert.Equal(HttpStatusCode.NotFound, (await brokerageAdmin.GetAsync(Listing(listing))).StatusCode);

        // The decision as the decide endpoint records it. Through the API it would also be
        // audited, which would make this test's agent permanent; the decision flow is covered by
        // BrokerageMembershipTests.
        await TestDatabase.ExecuteAsync(
            """
            update public.brokerage_memberships
            set status = 'approved', decided_by = $2, decided_at = timezone('utc', now())
            where agent_id = $1 and brokerage_id = $3
            """,
            agentId,
            admin,
            brokerage);

        // A fresh host, so no membership decision cached by the first one can answer for it.
        await using var freshHost = new ApiFactory();
        using var freshAdmin = freshHost.CreateClientFor(admin);

        Assert.Equal(HttpStatusCode.OK, (await freshAdmin.GetAsync(Listing(listing))).StatusCode);

        using var patch = Conditional(HttpMethod.Patch, Listing(listing), Basics(), ifMatch: 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await freshAdmin.SendAsync(patch)).StatusCode);
    }
}
