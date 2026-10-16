using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T066 — <c>PATCH /api/listings/{id}</c>, the autosave behind Story 1 scenario 6 (FR-066).
///
/// <para>
/// <b>What is at stake.</b> Autosave fires without the agent pressing anything, which makes it
/// the write most likely to collide — two tabs, or a phone and a laptop. A stale save must be
/// refused with the server's current state, never applied over an edit the agent did not see.
/// </para>
/// </summary>
public sealed class ListingAutosaveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ListingAutosaveTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private static JsonObject Edited(string address = "1422 Crestline Drive")
    {
        var body = Basics();
        body["addressLine1"] = address;
        body["listPrice"] = 1499000;
        body["bedrooms"] = 5;
        body["agentNotes"] = "Updated after the walkthrough.";

        return body;
    }

    private async Task<HttpResponseMessage> PatchAsync(Guid caller, Guid listingId, JsonObject body, int? ifMatch)
    {
        using var client = factory.CreateClientFor(caller);
        using var request = Conditional(HttpMethod.Patch, Listing(listingId), body, ifMatch);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Autosave_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();
        using var request = Conditional(HttpMethod.Patch, Listing(Guid.NewGuid()), Edited(), 1);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Autosave_AtTheCurrentVersion_PersistsAndAdvancesTheVersion()
    {
        var agent = await TestAgents.CreateAsync("autosave");
        var id = IdOf(await CreateAsync(factory, agent));
        var createdUpdatedAt = await TestDatabase.ScalarAsync<DateTime>("select updated_at from public.listings where id = $1", id);

        var response = await PatchAsync(agent, id, Edited(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);

        var listing = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(2, listing["rowVersion"]!.GetValue<int>());
        Assert.Equal("1422 Crestline Drive", listing["addressLine1"]!.GetValue<string>());
        Assert.Equal(1499000m, listing["listPrice"]!.GetValue<decimal>());
        Assert.Equal(5, listing["bedrooms"]!.GetValue<int>());

        // Resuming restores exactly what was saved (scenario 6).
        Assert.Equal("1422 Crestline Drive", await TestDatabase.ScalarAsync<string>("select address_line1 from public.listings where id = $1", id));
        Assert.Equal("Updated after the walkthrough.", await TestDatabase.ScalarAsync<string>("select agent_notes from public.listings where id = $1", id));
        Assert.Equal(2, await TestDatabase.ScalarAsync<int>("select row_version from public.listings where id = $1", id));
        Assert.True(await TestDatabase.ScalarAsync<DateTime>("select updated_at from public.listings where id = $1", id) > createdUpdatedAt);
    }

    /// <summary>FR-066 and SC-012: the second writer is told what the record now holds.</summary>
    [Fact]
    public async Task Autosave_AtAStaleVersion_Is409WithTheCurrentState()
    {
        var agent = await TestAgents.CreateAsync("autosave-stale");
        var id = IdOf(await CreateAsync(factory, agent));

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(agent, id, Edited("First tab"), ifMatch: 1)).StatusCode);

        var stale = await PatchAsync(agent, id, Edited("Second tab"), ifMatch: 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var problem = (await stale.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("https://realtorai.app/problems/version-conflict", problem["type"]!.GetValue<string>());
        Assert.Equal(2, problem["currentRowVersion"]!.GetValue<int>());
        Assert.Equal("First tab", problem["current"]!["addressLine1"]!.GetValue<string>());
        Assert.Equal(2, problem["current"]!["rowVersion"]!.GetValue<int>());

        Assert.Equal("First tab", await TestDatabase.ScalarAsync<string>("select address_line1 from public.listings where id = $1", id));
    }

    /// <summary>Without If-Match the write cannot be checked, so it is not made: 428, not a silent overwrite.</summary>
    [Fact]
    public async Task Autosave_WithoutIfMatch_Is428()
    {
        var agent = await TestAgents.CreateAsync("autosave-no-precondition");
        var id = IdOf(await CreateAsync(factory, agent));

        var response = await PatchAsync(agent, id, Edited(), ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync<int>("select row_version from public.listings where id = $1", id));
    }

    [Fact]
    public async Task Autosave_WithAnInvalidField_Is422AndLeavesTheVersion()
    {
        var agent = await TestAgents.CreateAsync("autosave-invalid");
        var id = IdOf(await CreateAsync(factory, agent));
        var body = Edited();
        body["postalCode"] = "nope";

        var response = await PatchAsync(agent, id, body, ifMatch: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync<int>("select row_version from public.listings where id = $1", id));
    }

    /// <summary>FR-049: an edit records what it replaced and what replaced it.</summary>
    [Fact]
    public async Task Autosave_IsAuditedWithBeforeAndAfterValues()
    {
        var agent = await TestAgents.CreateAsync("autosave-audit");
        var id = IdOf(await CreateAsync(factory, agent));

        await PatchAsync(agent, id, Edited(), ifMatch: 1);

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'field_edit' and listing_id = $1 and target_type = 'listing' and target_id = $1
              and actor_id = $2
              and before_value ->> 'addressLine1' = '1420 Crestline Drive'
              and after_value ->> 'addressLine1' = '1422 Crestline Drive'
            """,
            id,
            agent));
    }

    /// <summary>The audit trail records changes, so a save that changes nothing records nothing.</summary>
    [Fact]
    public async Task Autosave_WithNoChange_WritesNoAuditEvent()
    {
        var agent = await TestAgents.CreateAsync("autosave-noop");
        var id = IdOf(await CreateAsync(factory, agent));

        var response = await PatchAsync(agent, id, Basics(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where listing_id = $1",
            id));
    }

    [Fact]
    public async Task Autosave_ByAnUnrelatedAgent_Is404()
    {
        var owner = await TestAgents.CreateAsync("autosave-owner");
        var stranger = await TestAgents.CreateAsync("autosave-stranger");
        var id = IdOf(await CreateAsync(factory, owner));

        var response = await PatchAsync(stranger, id, Edited(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Oversight is read-only: a broker admin never edits content an agent is licensed for.</summary>
    [Fact]
    public async Task Autosave_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("autosave-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("autosave-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var id = IdOf(await CreateAsync(factory, member));

        var response = await PatchAsync(admin, id, Edited(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync<int>("select row_version from public.listings where id = $1", id));
    }

    /// <summary>FR-067: an archived listing is kept for its history, not for further work.</summary>
    [Fact]
    public async Task Autosave_OnAnArchivedListing_Is403()
    {
        var agent = await TestAgents.CreateAsync("autosave-archived");
        var id = IdOf(await CreateAsync(factory, agent));
        using (var client = factory.CreateClientFor(agent))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Listing(id))).StatusCode);
        }

        var response = await PatchAsync(agent, id, Edited(), ifMatch: 2);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
