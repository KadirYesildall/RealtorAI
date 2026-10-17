using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T084 — <c>POST /api/listings/{id}/pipeline</c>: 202 accepted, 409 budget exhausted, 503 no
/// model (FR-053, FR-054, FR-055, FR-061, SC-010).
///
/// <para>
/// <b>What is at stake.</b> Starting analysis is the one action that spends money on a model, so
/// it is where the budget is enforced before anything is dispatched (FR-055). And it is the
/// action most likely to be unavailable — every model is disabled until it is evaluated — so the
/// refusal has to say the listing can be finished by hand, not merely fail (SC-010).
/// </para>
/// </summary>
public sealed class PipelineStartTests : IClassFixture<ApiFactory>, IClassFixture<AiEnabledApiFactory>
{
    private readonly ApiFactory plain;
    private readonly AiEnabledApiFactory ai;

    public PipelineStartTests(ApiFactory plain, AiEnabledApiFactory ai)
    {
        this.plain = plain;
        this.ai = ai;
    }

    [Fact]
    public async Task Start_Anonymous_Is401()
    {
        using var client = ai.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(PipelinePath(Guid.NewGuid()), new JsonObject());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Start_WithAnEnabledModel_Is202WithEveryPhotoQueued()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(ai, "pipeline-start", 2);

        var response = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(listing, status["listingId"]!.GetValue<Guid>());
        Assert.Equal("analyzing", status["listingStatus"]!.GetValue<string>());
        Assert.True(status["manualCompletionAvailable"]!.GetValue<bool>());
        Assert.Equal(
            ["normalize", "technical_quality", "embeddings", "photo_analysis", "cover_and_order", "content_generation"],
            status["steps"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal(
            photos.Order(),
            status["photoProgress"]!.AsArray().Select(photo => photo!["photoId"]!.GetValue<Guid>()).Order());

        await WaitForCompletionAsync(ai, agent, listing);
    }

    /// <summary>Starting again while a run is in progress answers with that run rather than starting a second.</summary>
    [Fact]
    public async Task Start_WhileARunIsInProgress_ReturnsThatRun()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(ai, "pipeline-twice", 1);

        var first = await StartAsync(ai, agent, listing);
        var second = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        await WaitForCompletionAsync(ai, agent, listing);

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.pipeline_runs where listing_id = $1",
            listing));
    }

    /// <summary>
    /// SC-010: with no enabled model — the state of every registry entry until it is evaluated —
    /// the answer is 503 with the manual path named, and nothing is queued or spent.
    /// </summary>
    [Fact]
    public async Task Start_WithNoEnabledModel_Is503AndSaysTheListingCanBeFinishedByHand()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(plain, "pipeline-no-model", 1);

        var response = await StartAsync(plain, agent, listing);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(problem["manualCompletionAvailable"]!.GetValue<bool>());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.pipeline_runs where listing_id = $1",
            listing));
        Assert.Equal("draft", await TestDatabase.ScalarAsync<string>(
            "select status::text from public.listings where id = $1",
            listing));
    }

    /// <summary>FR-055: the budget is checked before dispatch, so an exhausted listing spends nothing more.</summary>
    [Fact]
    public async Task Start_WithTheBudgetExhausted_Is409AndQueuesNothing()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(ai, "pipeline-budget", 1);

        await TestDatabase.ExecuteAsync(
            """
            insert into public.ai_usage (listing_id, task_key, model_id, tokens_in, tokens_out, image_count)
            values ($1, 'photo.analysis', 'test/vision-model', 250000, 0, 0)
            """,
            listing);

        var response = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(problem["manualCompletionAvailable"]!.GetValue<bool>());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.pipeline_runs where listing_id = $1",
            listing));
    }

    /// <summary>FR-003's lower bound: analysis needs at least one photo to analyse.</summary>
    [Fact]
    public async Task Start_WithNoPhotos_Is422()
    {
        var agent = await TestAgents.CreateAsync("pipeline-empty");
        var listing = IdOf(await CreateAsync(ai, agent));

        var response = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>
    /// FR-061, carried from T071: a pending membership does not hold back AI work on the agent's
    /// own listing.
    /// </summary>
    [Fact]
    public async Task Start_ByAnAgentWithAPendingMembership_IsAccepted()
    {
        var admin = await TestAgents.CreateAsync("pipeline-pending-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var agent = await TestAgents.CreateAsync("pipeline-pending");
        await TestBrokerages.AddMemberAsync(agent, brokerage, "pending");
        var listing = IdOf(await CreateAsync(ai, agent));
        await UploadAsync(ai, agent, listing, TestImages.Jpeg());

        var response = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForCompletionAsync(ai, agent, listing);
    }

    [Fact]
    public async Task Start_ByAnUnrelatedAgent_Is404()
    {
        var (_, listing, _) = await ListingWithPhotosAsync(ai, "pipeline-owner", 1);
        var stranger = await TestAgents.CreateAsync("pipeline-stranger");

        var response = await StartAsync(ai, stranger, listing);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Spending a listing's AI budget is the owner's decision, not their broker's.</summary>
    [Fact]
    public async Task Start_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("pipeline-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("pipeline-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(ai, member));
        await UploadAsync(ai, member, listing, TestImages.Jpeg());

        var response = await StartAsync(ai, admin, listing);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Start_OnAnArchivedListing_Is403()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(ai, "pipeline-archived", 1);
        using (var client = ai.CreateClientFor(agent))
        {
            await client.DeleteAsync(Listing(listing));
        }

        var response = await StartAsync(ai, agent, listing);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
