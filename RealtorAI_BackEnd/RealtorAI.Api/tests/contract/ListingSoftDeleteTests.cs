using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T067 — <c>DELETE /api/listings/{id}</c> (FR-065, FR-067).
///
/// <para>
/// <b>What is at stake.</b> There is no hard delete. Removing the row would destroy the
/// append-only audit trail or be refused by its foreign key, and the originals must outlive the
/// listing by the retention period. "Delete" therefore means: archived, hidden from working
/// views, still readable with its history, and audited once — however many times it is asked.
/// </para>
/// </summary>
public sealed class ListingSoftDeleteTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ListingSoftDeleteTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<HttpResponseMessage> DeleteAsync(Guid caller, Guid listingId)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.DeleteAsync(Listing(listingId));
    }

    private static Task<long> SoftDeleteAuditCountAsync(Guid listingId) =>
        TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'listing_soft_deleted' and listing_id = $1",
            listingId);

    [Fact]
    public async Task Delete_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(Listing(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ByTheOwner_ArchivesKeepsTheRowAndAudits()
    {
        var agent = await TestAgents.CreateAsync("delete");
        var id = IdOf(await CreateAsync(factory, agent));

        var response = await DeleteAsync(agent, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select status = 'archived' and deleted_at is not null from public.listings where id = $1",
            id));
        Assert.Equal(1L, await SoftDeleteAuditCountAsync(id));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'listing_soft_deleted' and listing_id = $1 and actor_id = $2
              and before_value ->> 'status' = 'draft' and after_value ->> 'status' = 'archived'
            """,
            id,
            agent));
    }

    /// <summary>Idempotent: deleting again answers 204 and changes nothing — no second audit event.</summary>
    [Fact]
    public async Task Delete_Twice_Is204BothTimesAndAuditedOnce()
    {
        var agent = await TestAgents.CreateAsync("delete-twice");
        var id = IdOf(await CreateAsync(factory, agent));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(agent, id)).StatusCode);
        var deletedAt = await TestDatabase.ScalarAsync<DateTime>("select deleted_at from public.listings where id = $1", id);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(agent, id)).StatusCode);

        Assert.Equal(1L, await SoftDeleteAuditCountAsync(id));
        Assert.Equal(deletedAt, await TestDatabase.ScalarAsync<DateTime>("select deleted_at from public.listings where id = $1", id));
    }

    /// <summary>Hidden from working views, but still there for anyone who asks to see archived listings.</summary>
    [Fact]
    public async Task Delete_HidesTheListingUnlessArchivedListingsAreRequested()
    {
        var agent = await TestAgents.CreateAsync("delete-views");
        var kept = IdOf(await CreateAsync(factory, agent));
        var deleted = IdOf(await CreateAsync(factory, agent));
        await DeleteAsync(agent, deleted);

        using var client = factory.CreateClientFor(agent);

        var working = (await client.GetFromJsonAsync<JsonArray>(Listings))!
            .Select(listing => IdOf(listing!.AsObject())).ToList();
        var everything = (await client.GetFromJsonAsync<JsonArray>(Listings + "?includeArchived=true"))!
            .Select(listing => IdOf(listing!.AsObject())).ToList();

        Assert.Equal([kept], working);
        Assert.Contains(deleted, everything);
        Assert.Contains(kept, everything);
    }

    /// <summary>FR-067: the archived listing and its trail stay readable.</summary>
    [Fact]
    public async Task Delete_LeavesTheListingReadableAsArchived()
    {
        var agent = await TestAgents.CreateAsync("delete-readable");
        var id = IdOf(await CreateAsync(factory, agent));
        await DeleteAsync(agent, id);

        using var client = factory.CreateClientFor(agent);
        var response = await client.GetAsync(Listing(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("archived", detail["status"]!.GetValue<string>());
        Assert.NotNull(detail["deletedAt"]);
    }

    /// <summary>A broker admin may read an agent's listing but never remove it.</summary>
    [Fact]
    public async Task Delete_ByTheApprovedBrokerAdmin_Is403AndChangesNothing()
    {
        var admin = await TestAgents.CreateAsync("delete-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("delete-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var id = IdOf(await CreateAsync(factory, member));

        var response = await DeleteAsync(admin, id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select status = 'draft' and deleted_at is null from public.listings where id = $1",
            id));
        Assert.Equal(0L, await SoftDeleteAuditCountAsync(id));
    }

    [Fact]
    public async Task Delete_ByAnUnrelatedAgent_Is404()
    {
        var owner = await TestAgents.CreateAsync("delete-owner");
        var stranger = await TestAgents.CreateAsync("delete-stranger");
        var id = IdOf(await CreateAsync(factory, owner));

        var response = await DeleteAsync(stranger, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, await SoftDeleteAuditCountAsync(id));
    }

    [Fact]
    public async Task Delete_AnUnknownListing_Is404()
    {
        var agent = await TestAgents.CreateAsync("delete-unknown");

        var response = await DeleteAsync(agent, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
