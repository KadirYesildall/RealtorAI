using Libs.Ai;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T090 — near-duplicate detection (FR-009; spec Edge Cases).
///
/// <para>
/// Exact duplicates never reach this point: upload refuses identical bytes. This is the case
/// upload cannot see — two different files of the same view, such as two exposures of the living
/// room. Their embeddings are close, pgvector finds them, and the later one is flagged
/// <c>near_duplicate</c> for the agent to decide about. Nothing is removed (FR-011).
/// </para>
/// <para>
/// <b>The pair.</b> The seeded living-room pair the task names does not exist: seed.sql does not
/// synthesise the labelled photo set (a generated image cannot genuinely be a photo of a room).
/// Two distinct files of one colour stand in for it, embedded by the test sidecar from their
/// colour — the API side, which is what this verifies, is the real query and threshold.
/// </para>
/// </summary>
public sealed class NearDuplicateTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public NearDuplicateTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        factory.Registry.Enable(AiEnabledApiFactory.Entry(AiTaskKey.PhotoEmbedding, "test/embedding-model", """{"dimension": 768}"""));
    }

    private static IEnumerable<string> QualityFlags(System.Text.Json.Nodes.JsonObject photo) =>
        photo["analysis"]!["qualityFlags"]!.AsArray().Select(flag => flag!.GetValue<string>());

    [Fact]
    public async Task TwoShotsOfTheSameView_FlagTheLaterOne_AndKeepBoth()
    {
        var agent = await TestAgents.CreateAsync("near-duplicate");
        var listing = IdOf(await CreateAsync(factory, agent));
        var photos = await UploadAsync(
            factory,
            agent,
            listing,
            TestImages.JpegOfColour(180, 140, 100),
            TestImages.JpegOfColour(30, 60, 200),
            TestImages.JpegOfColour(181, 141, 99));

        var status = await RunToCompletionAsync(factory, agent, listing);
        Assert.Equal("done", StepState(status, "embeddings"));

        var first = await PhotoAsync(factory, agent, listing, photos[0]);
        var different = await PhotoAsync(factory, agent, listing, photos[1]);
        var later = await PhotoAsync(factory, agent, listing, photos[2]);

        Assert.Contains("near_duplicate", QualityFlags(later));
        Assert.DoesNotContain("near_duplicate", QualityFlags(first));
        Assert.DoesNotContain("near_duplicate", QualityFlags(different));

        // A flag, never a deletion.
        Assert.Equal(3L, await TestDatabase.ScalarAsync<long>("select count(*) from public.photos where listing_id = $1", listing));
        Assert.Equal(3L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.photo_embeddings e join public.photos p on p.id = e.photo_id where p.listing_id = $1",
            listing));
    }

    /// <summary>Similar photos on two different listings are not each other's duplicates.</summary>
    [Fact]
    public async Task TheSameViewOnAnotherListing_IsNotADuplicate()
    {
        var agent = await TestAgents.CreateAsync("near-duplicate-cross");
        var one = IdOf(await CreateAsync(factory, agent));
        var two = IdOf(await CreateAsync(factory, agent));
        await UploadAsync(factory, agent, one, TestImages.JpegOfColour(90, 200, 90));
        var photo = (await UploadAsync(factory, agent, two, TestImages.JpegOfColour(91, 199, 90)))[0];

        await RunToCompletionAsync(factory, agent, one);
        await RunToCompletionAsync(factory, agent, two);

        Assert.DoesNotContain("near_duplicate", QualityFlags(await PhotoAsync(factory, agent, two, photo)));
    }
}
