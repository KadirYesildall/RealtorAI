using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T089 — with no enabled model, the listing is still completable by hand (SC-010, FR-054).
///
/// <para>
/// This is the default state of the system, not an edge case: every registry entry stays
/// disabled until its license is recorded and its evaluation passes (VIII.4). So this runs on the
/// real host and the real registry, and walks everything Story 2 asks of the AI path through by
/// hand instead — classify, tag, caption, order, choose the cover, enter the required facts — to
/// prove none of it waits on a model.
/// </para>
/// </summary>
public sealed class ManualFallbackTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ManualFallbackTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task WithNoEnabledModel_ThePipelineSays503_AndEverythingCanBeDoneByHand()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "manual", 2);
        using var client = factory.CreateClientFor(agent);

        // ── The AI path is unavailable, and says what to do instead ──
        var started = await client.PostAsJsonAsync(PipelinePath(listing), new JsonObject());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, started.StatusCode);
        Assert.True((await started.Content.ReadFromJsonAsync<JsonObject>())!["manualCompletionAvailable"]!.GetValue<bool>());

        // ── Classify, tag and caption by hand ──
        var patched = await client.PatchAsJsonAsync(Photo(listing, photos[1]), new JsonObject
        {
            ["roomType"] = "exterior_front",
            ["featureTags"] = new JsonArray("Covered porch"),
            ["caption"] = "Front of the house with a covered porch",
        });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        // ── Order and cover by hand ──
        var ordered = await client.PutAsJsonAsync(Order(listing), new JsonObject
        {
            ["orderedPhotoIds"] = new JsonArray(JsonValue.Create(photos[1]), JsonValue.Create(photos[0])),
            ["coverPhotoId"] = photos[1],
        });
        Assert.Equal(HttpStatusCode.OK, ordered.StatusCode);

        // ── Required public-record facts by hand ──
        foreach (var (key, value) in new (string, int)[] { ("LivingArea", 2840), ("LotSizeSquareFeet", 7200), ("YearBuilt", 1998), ("TaxAnnualAmount", 14250) })
        {
            using var put = Conditional(HttpMethod.Put, Fields(listing), new JsonObject
            {
                ["resoFieldKey"] = key,
                ["value"] = value,
                ["provenance"] = "agent_entered",
            }, ifMatch: 0);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);
        }

        // ── The listing holds all of it, with no model involved ──
        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(listing)))!;
        var cover = detail["photos"]!.AsArray()[0]!.AsObject();

        Assert.Equal(photos[1], cover["id"]!.GetValue<Guid>());
        Assert.True(cover["isCover"]!.GetValue<bool>());
        Assert.Equal("exterior_front", cover["roomType"]!.GetValue<string>());
        Assert.Equal("Front of the house with a covered porch", cover["caption"]!.GetValue<string>());
        Assert.Null(cover["analysis"]);
        Assert.Empty(detail["missingRequiredFieldKeys"]!.AsArray());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.ai_usage where listing_id = $1", listing));
    }
}
