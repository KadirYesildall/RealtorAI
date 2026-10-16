using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T065 — <c>POST /api/listings</c>, plus the two reads that return what it created:
/// <c>GET /api/listings</c> and <c>GET /api/listings/{id}</c> (FR-005, FR-008, FR-058, FR-061,
/// FR-063).
///
/// <para>
/// <b>What is at stake.</b> Creation is where two promises are first kept or broken. FR-061: an
/// agent still waiting for their brokerage to approve them can start working. FR-008: with no
/// public-record source configured, square footage, lot size, year built and tax stay empty and
/// are marked required — nothing guesses them.
/// </para>
/// </summary>
public sealed class CreateListingTests : IClassFixture<ApiFactory>
{
    private static readonly string[] PublicRecordKeys = ["LivingArea", "LotSizeSquareFeet", "YearBuilt", "TaxAnnualAmount"];

    private readonly ApiFactory factory;

    public CreateListingTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    // ═════════════════════════════ POST /api/listings ═════════════════════════════

    [Fact]
    public async Task Create_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(Listings, Basics());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>FR-058: no license, no listing.</summary>
    [Fact]
    public async Task Create_WithAnIncompleteProfile_Is403()
    {
        var agent = await TestAgents.CreateAsync("create-incomplete", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Listings, Basics());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.listings where agent_id = $1",
            agent));
    }

    [Fact]
    public async Task Create_ReturnsTheDraftWithItsVersionAndLocation()
    {
        var agent = await TestAgents.CreateAsync("create");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Listings, Basics());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var listing = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = IdOf(listing);

        Assert.Equal($"/api/listings/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);

        Assert.Equal("draft", listing["status"]!.GetValue<string>());
        Assert.Equal(1, listing["rowVersion"]!.GetValue<int>());
        Assert.Equal("1420 Crestline Drive", listing["addressLine1"]!.GetValue<string>());
        Assert.Equal("Irvine", listing["city"]!.GetValue<string>());
        Assert.Equal("CA", listing["state"]!.GetValue<string>());
        Assert.Equal("92618", listing["postalCode"]!.GetValue<string>());
        Assert.Equal("SingleFamilyResidence", listing["propertyType"]!.GetValue<string>());
        Assert.Equal(1485000m, listing["listPrice"]!.GetValue<decimal>());
        Assert.Equal(4, listing["bedrooms"]!.GetValue<int>());
        Assert.Equal(3.5m, listing["bathrooms"]!.GetValue<decimal>());
        Assert.Null(listing["deletedAt"]);
        Assert.Null(listing["coverPhotoId"]);

        Assert.Equal(agent, await TestDatabase.ScalarAsync<Guid>("select agent_id from public.listings where id = $1", id));
        Assert.Equal(
            "Seller prefers weekend showings.",
            await TestDatabase.ScalarAsync<string>("select agent_notes from public.listings where id = $1", id));
    }

    /// <summary>
    /// FR-061: a pending membership does not hold the agent back. The listing is filed under
    /// the brokerage they declared, which grants that brokerage nothing until it approves them —
    /// the authorizer requires both memberships to be approved (FR-050).
    /// </summary>
    [Fact]
    public async Task Create_WithAPendingMembership_IsAllowedAndFiledUnderTheDeclaredBrokerage()
    {
        var admin = await TestAgents.CreateAsync("create-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var pending = await TestAgents.CreateAsync("create-pending");
        await TestBrokerages.AddMemberAsync(pending, brokerage, "pending");

        var listing = await CreateAsync(factory, pending);

        Assert.Equal(brokerage, await TestDatabase.ScalarAsync<Guid>(
            "select brokerage_id from public.listings where id = $1",
            IdOf(listing)));
    }

    /// <summary>An agent who has declared no brokerage can still draft privately (Capabilities.CanPrepareListing).</summary>
    [Fact]
    public async Task Create_WithNoBrokerage_IsAllowedWithNoBrokerageRecorded()
    {
        var agent = await TestAgents.CreateAsync("create-solo");

        var listing = await CreateAsync(factory, agent);

        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select brokerage_id is null and target_mls_id is null from public.listings where id = $1",
            IdOf(listing)));
    }

    /// <summary>The MLS a listing targets comes from its brokerage's settings, never from the request.</summary>
    [Fact]
    public async Task Create_TakesTheTargetMlsFromTheBrokerageSettings()
    {
        var agent = await TestAgents.CreateAsync("create-mls");
        await TestBrokerages.CreateAsync(agent, targetMlsId: "CRMLS");

        var body = Basics();
        body["targetMlsId"] = "SOMEWHERE_ELSE";

        var listing = await CreateAsync(factory, agent, body);

        Assert.Equal("CRMLS", listing["targetMlsId"]!.GetValue<string>());
    }

    /// <summary>
    /// A rejected membership is no longer live, so the listing is not filed under that
    /// brokerage — it would otherwise reappear in the brokerage's scope if the agent were ever
    /// re-approved there, for work done while they were not a member.
    /// </summary>
    [Fact]
    public async Task Create_WithOnlyARejectedMembership_RecordsNoBrokerage()
    {
        var admin = await TestAgents.CreateAsync("create-rej-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var agent = await TestAgents.CreateAsync("create-rejected");
        await TestBrokerages.AddMemberAsync(agent, brokerage, "rejected");

        var listing = await CreateAsync(factory, agent);

        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select brokerage_id is null from public.listings where id = $1",
            IdOf(listing)));
    }

    /// <summary>
    /// FR-008 with no provider configured (research R5): the four public-record fields are not
    /// written at all — no row, so no provenance can be claimed for a value nobody supplied —
    /// and the listing reports each one as required.
    /// </summary>
    [Fact]
    public async Task Create_WithNoPublicRecordSource_LeavesThoseFieldsEmptyAndRequired()
    {
        var agent = await TestAgents.CreateAsync("create-prefill");
        var listing = await CreateAsync(factory, agent);
        var id = IdOf(listing);

        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.listing_fields where listing_id = $1",
            id));

        using var client = factory.CreateClientFor(agent);
        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(id)))!;

        var missing = detail["missingRequiredFieldKeys"]!.AsArray().Select(key => key!.GetValue<string>()).ToList();
        Assert.Equal(PublicRecordKeys.Order(StringComparer.Ordinal), missing.Order(StringComparer.Ordinal));
        Assert.Empty(detail["fields"]!.AsArray());
    }

    [Theory]
    [InlineData("addressLine1", "")]
    [InlineData("addressLine1", "   ")]
    [InlineData("city", "")]
    [InlineData("state", "California")]
    [InlineData("state", "ca")]
    [InlineData("postalCode", "9261")]
    [InlineData("postalCode", "ABCDE")]
    [InlineData("propertyType", "")]
    [InlineData("propertyType", "Spaceship")]
    public async Task Create_WithAnInvalidTextField_Is422NamingIt(string field, string value)
    {
        var body = Basics();
        body[field] = value;

        await AssertRejectedAsync(body, field);
    }

    [Theory]
    [InlineData("listPrice", -1)]
    [InlineData("listPrice", 10000000000)]
    [InlineData("listPrice", 100.125)]
    [InlineData("bedrooms", -1)]
    [InlineData("bedrooms", 100)]
    [InlineData("bathrooms", -0.5)]
    [InlineData("bathrooms", 2.3)]
    [InlineData("bathrooms", 100)]
    public async Task Create_WithAnOutOfRangeNumber_Is422NamingIt(string field, double value)
    {
        var body = Basics();
        body[field] = value;

        await AssertRejectedAsync(body, field);
    }

    [Fact]
    public async Task Create_WithOverlongNotes_Is422()
    {
        var body = Basics();
        body["agentNotes"] = new string('n', 5001);

        await AssertRejectedAsync(body, "agentNotes");
    }

    [Fact]
    public async Task Create_WithOnlyTheRequiredFields_LeavesTheOptionalOnesEmpty()
    {
        var agent = await TestAgents.CreateAsync("create-minimal");
        var body = new JsonObject
        {
            ["addressLine1"] = "9 Ocean View",
            ["city"] = "Laguna Beach",
            ["state"] = "CA",
            ["postalCode"] = "92651-1234",
            ["propertyType"] = "Condominium",
        };

        var listing = await CreateAsync(factory, agent, body);

        Assert.Null(listing["listPrice"]);
        Assert.Null(listing["bedrooms"]);
        Assert.Null(listing["bathrooms"]);
    }

    private async Task AssertRejectedAsync(JsonObject body, string field)
    {
        var agent = await TestAgents.CreateAsync("create-invalid");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Listings, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]![field]);
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.listings where agent_id = $1",
            agent));
    }

    // ═════════════════════════════ GET /api/listings ═════════════════════════════

    [Fact]
    public async Task List_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(Listings);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Results are the caller's own listings — never a colleague's, even an approved admin's view of one.</summary>
    [Fact]
    public async Task List_ReturnsOnlyTheCallersListings_MostRecentlyUpdatedFirst()
    {
        var agent = await TestAgents.CreateAsync("list-owner");
        var other = await TestAgents.CreateAsync("list-other");

        var older = await CreateAsync(factory, agent);
        var newer = await CreateAsync(factory, agent);
        await CreateAsync(factory, other);

        using var client = factory.CreateClientFor(agent);
        var listings = (await client.GetFromJsonAsync<JsonArray>(Listings))!;

        var ids = listings.Select(listing => IdOf(listing!.AsObject())).ToList();
        Assert.Equal([IdOf(newer), IdOf(older)], ids);
    }

    // ═════════════════════════════ GET /api/listings/{id} ═════════════════════════════

    [Fact]
    public async Task Detail_ForTheOwner_IncludesFieldsPhotosAndVersion()
    {
        var agent = await TestAgents.CreateAsync("detail-owner");
        var created = await CreateAsync(factory, agent);
        using var client = factory.CreateClientFor(agent);

        var response = await client.GetAsync(Listing(IdOf(created)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);

        var detail = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(IdOf(created), IdOf(detail));
        Assert.Equal(1, detail["rowVersion"]!.GetValue<int>());
        Assert.Empty(detail["photos"]!.AsArray());
        Assert.NotNull(detail["fields"]);
    }

    /// <summary>An unrelated caller cannot tell an existing listing from a missing one (FR-063).</summary>
    [Fact]
    public async Task Detail_ForAnUnrelatedAgent_Is404()
    {
        var owner = await TestAgents.CreateAsync("detail-private");
        var stranger = await TestAgents.CreateAsync("detail-stranger");
        var created = await CreateAsync(factory, owner);
        using var client = factory.CreateClientFor(stranger);

        var response = await client.GetAsync(Listing(IdOf(created)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_ForAnUnknownId_Is404()
    {
        var agent = await TestAgents.CreateAsync("detail-unknown");
        using var client = factory.CreateClientFor(agent);

        var response = await client.GetAsync(Listing(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>FR-064: the platform administrator governs the model registry, not listing content.</summary>
    [Fact]
    public async Task Detail_ForThePlatformAdministrator_Is404()
    {
        var owner = await TestAgents.CreateAsync("detail-admin-target");
        var created = await CreateAsync(factory, owner);
        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        var response = await client.GetAsync(Listing(IdOf(created)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>FR-050: an approved broker admin reads an approved member's listing.</summary>
    [Fact]
    public async Task Detail_ForAnApprovedBrokerAdminOfAnApprovedMember_Is200()
    {
        var admin = await TestAgents.CreateAsync("detail-broker-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("detail-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var created = await CreateAsync(factory, member);
        using var client = factory.CreateClientFor(admin);

        var response = await client.GetAsync(Listing(IdOf(created)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>FR-050: a pending member's listing is invisible to the brokerage that has not approved them.</summary>
    [Fact]
    public async Task Detail_ForTheBrokerAdminOfAPendingMember_Is404()
    {
        var admin = await TestAgents.CreateAsync("detail-pending-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var pending = await TestAgents.CreateAsync("detail-pending");
        await TestBrokerages.AddMemberAsync(pending, brokerage, "pending");
        var created = await CreateAsync(factory, pending);
        using var client = factory.CreateClientFor(admin);

        var response = await client.GetAsync(Listing(IdOf(created)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
