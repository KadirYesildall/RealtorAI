using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T085 — <c>GET /api/listings/{id}/pipeline</c> and what a run leaves behind (FR-009, FR-010,
/// FR-049, FR-053, FR-054; Story 2).
///
/// <para>
/// <b>What is at stake.</b> The status endpoint is the record of what analysis did — the SignalR
/// channel is a hint to refresh it, never the source — so it has to be right after every outcome:
/// success, a photo that failed while the others did not (Story 2 scenario 8), and a total
/// failure, which must hand the listing back to the agent as a draft (SC-010).
/// </para>
/// </summary>
public sealed class PipelineStatusTests : IClassFixture<ApiFactory>, IClassFixture<AiEnabledApiFactory>
{
    private readonly ApiFactory plain;
    private readonly AiEnabledApiFactory ai;

    public PipelineStatusTests(ApiFactory plain, AiEnabledApiFactory ai)
    {
        this.plain = plain;
        this.ai = ai;
    }

    [Fact]
    public async Task Status_BeforeAnyRun_IsEmptyButSaysTheListingCanBeFinishedByHand()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(plain, "status-none", 1);

        var status = await StatusAsync(plain, agent, listing);

        Assert.Equal(listing, status["listingId"]!.GetValue<Guid>());
        Assert.Equal("draft", status["listingStatus"]!.GetValue<string>());
        Assert.Empty(status["steps"]!.AsArray());
        Assert.Empty(status["photoProgress"]!.AsArray());
        Assert.True(status["manualCompletionAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_AfterASuccessfulRun_ShowsEveryStepAndPhotoDoneAndTheListingReadyForReview()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-done", 2);

        var status = await RunToCompletionAsync(ai, agent, listing);

        Assert.Equal("done", StepState(status, "normalize"));
        Assert.Equal("done", StepState(status, "technical_quality"));
        Assert.Equal("done", StepState(status, "photo_analysis"));
        Assert.Equal("done", StepState(status, "cover_and_order"));
        Assert.All(status["photoProgress"]!.AsArray(), photo => Assert.Equal("done", photo!["state"]!.GetValue<string>()));
        Assert.Equal("ready_for_review", status["listingStatus"]!.GetValue<string>());

        // FR-009: each photo carries what the model saw, traceable to the model and prompt.
        var photo = await PhotoAsync(ai, agent, listing, photos[0]);
        var analysis = photo["analysis"]!.AsObject();
        Assert.Equal("living_room", analysis["roomType"]!.GetValue<string>());
        Assert.Equal(["Fireplace", "Hardwood floors"], analysis["featureTags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal("test/vision-model", analysis["modelId"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(analysis["promptVersion"]!.GetValue<string>()));
        Assert.False(analysis["failed"]!.GetValue<bool>());

        // FR-010: the alt text becomes an editable caption.
        Assert.Equal("Living room with a fireplace and hardwood floors", photo["caption"]!.GetValue<string>());
    }

    /// <summary>
    /// The photo.embedding model is disabled here, as it is everywhere until evaluated: the step is
    /// skipped with a reason, not failed, and nothing else depends on it.
    /// </summary>
    [Fact]
    public async Task Status_WithNoEmbeddingModel_SkipsEmbeddingsWithAReason()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(ai, "status-skip", 1);

        var status = await RunToCompletionAsync(ai, agent, listing);

        var embeddings = status["steps"]!.AsArray().Single(step => step!["name"]!.GetValue<string>() == "embeddings")!;
        Assert.Equal("skipped", embeddings["state"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(embeddings["failureReason"]!.GetValue<string>()));
    }

    /// <summary>FR-049 / VIII.5: every model call is audited with the model and the exact prompt version.</summary>
    [Fact]
    public async Task Run_AuditsEachModelCallWithModelAndPromptVersion()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-audit", 2);

        await RunToCompletionAsync(ai, agent, listing);

        Assert.Equal((long)photos.Count, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'ai_call' and listing_id = $1 and actor_id = $2
              and model_id = 'test/vision-model' and prompt_version is not null and prompt_hash is not null
            """,
            listing,
            agent));
        Assert.Equal((long)photos.Count, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.ai_usage where listing_id = $1 and task_key = 'photo.analysis'",
            listing));
    }

    /// <summary>
    /// VIII.4.c: the model is sent the normalized copy as an image, alongside the prompt — never the
    /// original, and never with the agent's private notes.
    /// </summary>
    [Fact]
    public async Task Run_SendsTheModelTheNormalizedCopyAndNotTheAgentsNotes()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-boundary", 1);

        await RunToCompletionAsync(ai, agent, listing);

        var call = ai.Models.Calls.Single(messages =>
            messages.Any(message => message.Text.Contains(photos[0].ToString(), StringComparison.Ordinal)));
        var image = call.SelectMany(message => message.Contents).OfType<DataContent>().Single();
        var normalizedKey = (await TestDatabase.ScalarAsync<string>("select normalized_storage_key from public.photos where id = $1", photos[0]))!;
        var storage = ai.Services.GetService(typeof(Modules.Media.IPhotoStorage)) as Modules.Media.IPhotoStorage;

        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(await storage!.GetNormalizedAsync(normalizedKey, CancellationToken.None), image.Data.ToArray());
        Assert.DoesNotContain(call, message => message.Text.Contains("Seller prefers weekend showings", StringComparison.Ordinal));
    }

    /// <summary>Story 2 scenario 8: one photo failing fails that photo, with a reason, and no other.</summary>
    [Fact]
    public async Task Run_WhenOnePhotoFails_RecordsThatFailureAndFinishesTheRest()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-isolation", 3);
        ai.Models.FailingPhotos[photos[1]] = true;

        try
        {
            var status = await RunToCompletionAsync(ai, agent, listing);

            var progress = status["photoProgress"]!.AsArray().ToDictionary(
                photo => photo!["photoId"]!.GetValue<Guid>(),
                photo => photo!.AsObject());

            Assert.Equal("failed", progress[photos[1]]["state"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(progress[photos[1]]["failureReason"]!.GetValue<string>()));
            Assert.Equal("done", progress[photos[0]]["state"]!.GetValue<string>());
            Assert.Equal("done", progress[photos[2]]["state"]!.GetValue<string>());
            Assert.Equal("ready_for_review", status["listingStatus"]!.GetValue<string>());

            var failed = (await PhotoAsync(ai, agent, listing, photos[1]))["analysis"]!.AsObject();
            Assert.True(failed["failed"]!.GetValue<bool>());
        }
        finally
        {
            ai.Models.FailingPhotos.TryRemove(photos[1], out _);
        }
    }

    /// <summary>SC-010: when every photo fails, the listing goes back to a draft the agent can finish by hand.</summary>
    [Fact]
    public async Task Run_WhenEveryPhotoFails_ReturnsTheListingToDraft()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-total-failure", 2);
        foreach (var photo in photos)
        {
            ai.Models.FailingPhotos[photo] = true;
        }

        try
        {
            var status = await RunToCompletionAsync(ai, agent, listing);

            Assert.Equal("failed", StepState(status, "photo_analysis"));
            Assert.Equal("draft", status["listingStatus"]!.GetValue<string>());
            Assert.True(status["manualCompletionAvailable"]!.GetValue<bool>());
        }
        finally
        {
            foreach (var photo in photos)
            {
                ai.Models.FailingPhotos.TryRemove(photo, out _);
            }
        }
    }

    /// <summary>Story 2 scenario 8's retry: running again analyses only what has not succeeded.</summary>
    [Fact]
    public async Task Run_Again_AnalysesOnlyThePhotosThatFailed()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-retry", 2);
        ai.Models.FailingPhotos[photos[0]] = true;
        await RunToCompletionAsync(ai, agent, listing);
        ai.Models.FailingPhotos.TryRemove(photos[0], out _);

        var callsBefore = await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'ai_call' and listing_id = $1",
            listing);

        var status = await RunToCompletionAsync(ai, agent, listing);

        Assert.All(status["photoProgress"]!.AsArray(), photo => Assert.Equal("done", photo!["state"]!.GetValue<string>()));
        Assert.Equal(callsBefore + 1, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'ai_call' and listing_id = $1",
            listing));
    }

    /// <summary>
    /// FR-010: a suggested cover and a recommended order, each with a stated reason — the exterior
    /// first, then a walk through the house.
    /// </summary>
    [Fact]
    public async Task Run_SuggestsTheExteriorAsCoverAndOrdersAWalkThroughWithReasons()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-order", 3);
        var rooms = new Dictionary<Guid, string> { [photos[0]] = "bathroom", [photos[1]] = "exterior_front", [photos[2]] = "kitchen" };
        ai.Models.Answer = input =>
            FakeChatClientFactory.DefaultAnswer(rooms.First(room => input.Contains(room.Key.ToString(), StringComparison.Ordinal)).Value);

        try
        {
            await RunToCompletionAsync(ai, agent, listing);
        }
        finally
        {
            ai.Models.Answer = _ => FakeChatClientFactory.DefaultAnswer();
        }

        var analyses = new Dictionary<Guid, JsonObject>();
        foreach (var photo in photos)
        {
            analyses[photo] = (await PhotoAsync(ai, agent, listing, photo))["analysis"]!.AsObject();
        }

        Assert.True(analyses[photos[1]]["coverRecommended"]!.GetValue<bool>());
        Assert.False(analyses[photos[0]]["coverRecommended"]!.GetValue<bool>());
        Assert.False(analyses[photos[2]]["coverRecommended"]!.GetValue<bool>());

        Assert.Equal(0, analyses[photos[1]]["orderRecommendation"]!.GetValue<int>());
        Assert.Equal(1, analyses[photos[2]]["orderRecommendation"]!.GetValue<int>());
        Assert.Equal(2, analyses[photos[0]]["orderRecommendation"]!.GetValue<int>());
        Assert.All(analyses.Values, analysis => Assert.False(string.IsNullOrWhiteSpace(analysis["recommendationReason"]!.GetValue<string>())));

        // A suggestion only: nothing is reordered until the agent says so (FR-011).
        Assert.Equal(1, await TestDatabase.ScalarAsync<int>("select order_index from public.photos where id = $1", photos[1]));
        Assert.False(await TestDatabase.ScalarAsync<bool>("select is_cover from public.photos where id = $1", photos[1]));
    }

    /// <summary>FR-053: progress reaches the owner per photo, over the hub, as it happens.</summary>
    [Fact]
    public async Task Run_PushesPhotoProgressToTheSubscribedOwner()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-push", 2);
        var done = new ConcurrentDictionary<Guid, bool>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(ai.Server.BaseAddress, "hubs/pipeline"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => ai.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(TestTokens.For(agent));
                })
            .Build();

        connection.On<JsonElement>("PhotoProgress", message =>
        {
            if (message.GetProperty("state").GetString() == "done")
            {
                done[message.GetProperty("photoId").GetGuid()] = true;

                if (photos.All(done.ContainsKey))
                {
                    allDone.TrySetResult();
                }
            }
        });

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToListing", listing);

        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(ai, agent, listing)).StatusCode);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForCompletionAsync(ai, agent, listing);
    }

    /// <summary>The poll is the broker admin's view of progress: read-only, approved members only.</summary>
    [Fact]
    public async Task Status_ForTheApprovedBrokerAdmin_Is200_AndForAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("status-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("status-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(plain, member));
        var stranger = await TestAgents.CreateAsync("status-stranger");

        using var adminClient = plain.CreateClientFor(admin);
        using var strangerClient = plain.CreateClientFor(stranger);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(PipelinePath(listing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(PipelinePath(listing))).StatusCode);
    }

    [Fact]
    public async Task Status_Anonymous_Is401()
    {
        using var client = plain.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PipelinePath(Guid.NewGuid()))).StatusCode);
    }

    /// <summary>The quality step's flags come from the API's own thresholds, applied to the sidecar's measurements.</summary>
    [Fact]
    public async Task Run_FlagsBlurFromTheSidecarsMeasurement()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-blur", 1);
        ai.Sidecar.BlurScore = 12;

        try
        {
            await RunToCompletionAsync(ai, agent, listing);
        }
        finally
        {
            ai.Sidecar.BlurScore = 250;
        }

        var analysis = (await PhotoAsync(ai, agent, listing, photos[0]))["analysis"]!.AsObject();
        Assert.Contains("blurry", analysis["qualityFlags"]!.AsArray().Select(flag => flag!.GetValue<string>()));
    }

    /// <summary>A sidecar outage fails the quality step only; analysis still runs (contracts/vision-sidecar.md).</summary>
    [Fact]
    public async Task Run_WithTheSidecarDown_FailsQualityAndStillAnalyses()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(ai, "status-sidecar-down", 1);
        ai.Sidecar.Unavailable = true;

        JsonObject status;
        try
        {
            status = await RunToCompletionAsync(ai, agent, listing);
        }
        finally
        {
            ai.Sidecar.Unavailable = false;
        }

        Assert.Equal("failed", StepState(status, "technical_quality"));
        Assert.Equal("done", StepState(status, "photo_analysis"));
    }

    /// <summary>The model's room type must be one the schema allows; a valid-shape answer is trusted no further than that.</summary>
    [Fact]
    public async Task Run_WithAModelAnswerOutsideTheSchema_FailsThatPhotoAfterOneRetry()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "status-schema", 1);
        ai.Models.Answer = _ => FakeChatClientFactory.DefaultAnswer("ballroom");

        JsonObject status;
        try
        {
            status = await RunToCompletionAsync(ai, agent, listing);
        }
        finally
        {
            ai.Models.Answer = _ => FakeChatClientFactory.DefaultAnswer();
        }

        Assert.Equal("failed", status["photoProgress"]!.AsArray().Single()!["state"]!.GetValue<string>());
        Assert.Equal(2, ai.Models.Calls.Count(messages => messages.Any(message => message.Text.Contains(photos[0].ToString(), StringComparison.Ordinal))));
    }
}
