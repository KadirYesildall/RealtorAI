using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>Photo and pipeline calls shared by the US2 suites.</summary>
internal static class PipelineTesting
{
    internal static string PipelinePath(Guid listingId) => $"/api/listings/{listingId}/pipeline";

    internal static string Photo(Guid listingId, Guid photoId) => $"/api/listings/{listingId}/photos/{photoId}";

    internal static string Order(Guid listingId) => $"/api/listings/{listingId}/photos/order";

    /// <summary>Uploads <paramref name="photos"/> and returns their ids in order.</summary>
    internal static async Task<List<Guid>> UploadAsync(ApiFactory factory, Guid agent, Guid listing, params byte[][] photos)
    {
        using var client = factory.CreateClientFor(agent);
        using var form = TestListings.Files(photos.Select((content, index) => ($"photo-{index}.jpg", content, "image/jpeg")).ToArray());

        var response = await client.PostAsync(TestListings.Photos(listing), form);
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        var results = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.All(results, result => Assert.True(result!["accepted"]!.GetValue<bool>(), result!.ToJsonString()));

        return results.Select(result => result!["photoId"]!.GetValue<Guid>()).ToList();
    }

    /// <summary>A listing owned by a fresh agent, with <paramref name="photoCount"/> uploaded photos.</summary>
    internal static async Task<(Guid Agent, Guid Listing, List<Guid> Photos)> ListingWithPhotosAsync(
        ApiFactory factory,
        string label,
        int photoCount)
    {
        var agent = await TestAgents.CreateAsync(label);
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, agent));
        var photos = await UploadAsync(factory, agent, listing, Enumerable.Range(0, photoCount).Select(_ => TestImages.Jpeg()).ToArray());

        return (agent, listing, photos);
    }

    internal static async Task<HttpResponseMessage> StartAsync(ApiFactory factory, Guid agent, Guid listing)
    {
        using var client = factory.CreateClientFor(agent);

        return await client.PostAsJsonAsync(PipelinePath(listing), new JsonObject());
    }

    internal static async Task<JsonObject> StatusAsync(ApiFactory factory, Guid caller, Guid listing)
    {
        using var client = factory.CreateClientFor(caller);

        return (await client.GetFromJsonAsync<JsonObject>(PipelinePath(listing)))!;
    }

    /// <summary>
    /// Polls the status endpoint until no step is queued or running. The pipeline runs as a
    /// background job, so the start call returns before any of it has happened.
    /// </summary>
    internal static async Task<JsonObject> WaitForCompletionAsync(ApiFactory factory, Guid caller, Guid listing)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        JsonObject status;

        do
        {
            status = await StatusAsync(factory, caller, listing);

            var states = status["steps"]!.AsArray().Select(step => step!["state"]!.GetValue<string>()).ToList();

            if (states.Count > 0 && states.All(state => state is "done" or "failed" or "skipped"))
            {
                return status;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"The pipeline for {listing} did not finish: {status.ToJsonString()}");
    }

    /// <summary>Starts the pipeline, asserts it was accepted, and waits for it to finish.</summary>
    internal static async Task<JsonObject> RunToCompletionAsync(ApiFactory factory, Guid agent, Guid listing)
    {
        var started = await StartAsync(factory, agent, listing);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        return await WaitForCompletionAsync(factory, agent, listing);
    }

    internal static string StepState(JsonObject status, string step) =>
        status["steps"]!.AsArray().Single(candidate => candidate!["name"]!.GetValue<string>() == step)!["state"]!.GetValue<string>();

    internal static async Task<JsonObject> PhotoAsync(ApiFactory factory, Guid caller, Guid listing, Guid photoId)
    {
        using var client = factory.CreateClientFor(caller);
        var detail = (await client.GetFromJsonAsync<JsonObject>(TestListings.Listing(listing)))!;

        return detail["photos"]!.AsArray().Single(photo => photo!["id"]!.GetValue<Guid>() == photoId)!.AsObject();
    }
}
