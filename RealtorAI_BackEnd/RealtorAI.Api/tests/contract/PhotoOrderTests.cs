using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T087 — <c>PUT /api/listings/{id}/photos/order</c>: reorder and choose the cover (FR-010,
/// FR-011).
///
/// <para>
/// The order is the agent's: a suggestion never moves a photo by itself. A reorder names every
/// photo exactly once, so a stale view — missing a photo uploaded from another tab, or listing
/// one deleted meanwhile — is refused rather than applied to a set it does not describe. It lands
/// in one transaction (the order index is unique and deferrable), so no reader sees half an order.
/// </para>
/// </summary>
public sealed class PhotoOrderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public PhotoOrderTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private static JsonObject Body(IEnumerable<Guid> ordered, Guid? cover = null)
    {
        var body = new JsonObject
        {
            ["orderedPhotoIds"] = new JsonArray(ordered.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
        };

        if (cover is { } coverId)
        {
            body["coverPhotoId"] = coverId;
        }

        return body;
    }

    private async Task<HttpResponseMessage> PutAsync(Guid caller, Guid listing, JsonObject body)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PutAsJsonAsync(Order(listing), body);
    }

    private static Task<int> IndexAsync(Guid photo) =>
        TestDatabase.ScalarAsync<int>("select order_index from public.photos where id = $1", photo);

    [Fact]
    public async Task Reorder_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PutAsJsonAsync(Order(Guid.NewGuid()), Body([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_AppliesTheOrderAndTheCoverAndAudits()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "order", 3);
        var reordered = new[] { photos[2], photos[0], photos[1] };

        var response = await PutAsync(agent, listing, Body(reordered, cover: photos[2]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await IndexAsync(photos[2]));
        Assert.Equal(1, await IndexAsync(photos[0]));
        Assert.Equal(2, await IndexAsync(photos[1]));
        Assert.True(await TestDatabase.ScalarAsync<bool>("select is_cover from public.photos where id = $1", photos[2]));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("select count(*) from public.photos where listing_id = $1 and is_cover", listing));
        Assert.Equal(photos[2], await TestDatabase.ScalarAsync<Guid>("select cover_photo_id from public.listings where id = $1", listing));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'photo_reorder' and listing_id = $1 and actor_id = $2
              and after_value -> 'orderedPhotoIds' ->> 0 = $3::text
            """,
            listing,
            agent,
            photos[2].ToString()));

        using var client = factory.CreateClientFor(agent);
        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(listing)))!;
        Assert.Equal(reordered, detail["photos"]!.AsArray().Select(photo => photo!["id"]!.GetValue<Guid>()));
        Assert.Equal(photos[2], detail["coverPhotoId"]!.GetValue<Guid>());
    }

    /// <summary>Choosing another cover moves it, so a listing never has two.</summary>
    [Fact]
    public async Task Reorder_WithANewCover_MovesTheCover()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "order-cover", 2);
        await PutAsync(agent, listing, Body(photos, cover: photos[0]));

        var response = await PutAsync(agent, listing, Body(photos, cover: photos[1]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await TestDatabase.ScalarAsync<bool>("select is_cover from public.photos where id = $1", photos[0]));
        Assert.True(await TestDatabase.ScalarAsync<bool>("select is_cover from public.photos where id = $1", photos[1]));
    }

    /// <summary>Without a cover in the body, the order changes and the cover stays where it was.</summary>
    [Fact]
    public async Task Reorder_WithoutACover_KeepsTheCurrentOne()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "order-keep-cover", 2);
        await PutAsync(agent, listing, Body(photos, cover: photos[1]));

        await PutAsync(agent, listing, Body([photos[1], photos[0]]));

        Assert.True(await TestDatabase.ScalarAsync<bool>("select is_cover from public.photos where id = $1", photos[1]));
        Assert.Equal(0, await IndexAsync(photos[1]));
    }

    public static TheoryData<string> StaleOrders { get; } = new() { "missing", "extra", "duplicate", "empty" };

    /// <summary>An order that is not exactly this listing's photos describes some other set; it is refused whole.</summary>
    [Theory]
    [MemberData(nameof(StaleOrders))]
    public async Task Reorder_ThatIsNotExactlyTheListingsPhotos_Is422AndChangesNothing(string kind)
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "order-stale", 2);
        Guid[] ordered = kind switch
        {
            "missing" => [photos[1]],
            "extra" => [photos[1], photos[0], Guid.NewGuid()],
            "duplicate" => [photos[1], photos[1]],
            _ => [],
        };

        var response = await PutAsync(agent, listing, Body(ordered));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await IndexAsync(photos[0]));
        Assert.Equal(1, await IndexAsync(photos[1]));
    }

    [Fact]
    public async Task Reorder_WithACoverNotInTheList_Is422()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "order-bad-cover", 2);

        var response = await PutAsync(agent, listing, Body(photos, cover: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("order-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("order-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        var photos = await UploadAsync(factory, member, listing, TestImages.Jpeg());

        var response = await PutAsync(admin, listing, Body(photos));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_ByAnUnrelatedAgent_Is404()
    {
        var (_, listing, photos) = await ListingWithPhotosAsync(factory, "order-owner", 1);
        var stranger = await TestAgents.CreateAsync("order-stranger");

        var response = await PutAsync(stranger, listing, Body(photos));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
