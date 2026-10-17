using System.Net;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T105 — remarks respect the MLS's configured limit, and fail closed when it is unset (FR-013,
/// FR-015; research R2).
///
/// <para>
/// The limit is not a prompt instruction the model may ignore: the model is told it, and then
/// every variant is measured. A variant over the limit is never stored, however good it reads,
/// because an approved description the MLS then truncates is a description nobody approved.
/// </para>
/// </summary>
public sealed class RemarksLimitTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public RemarksLimitTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableContentModel(factory);
    }

    /// <summary>The model is told the limit it must write to.</summary>
    [Fact]
    public async Task TheModelIsGivenTheProfilesLimit()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "limit-told", await VerifiedMlsAsync(publicLimit: 480, privateLimit: 320));
        var requests = new List<JsonObject>();
        WithAnswer(request =>
        {
            lock (requests)
            {
                requests.Add(request);
            }

            return FakeChatClientFactory.DefaultContentAnswer(request);
        });

        try
        {
            await GenerateAsync(factory, agent, listing, "public_remarks");
            await GenerateAsync(factory, agent, listing, "private_remarks");
            await GenerateAsync(factory, agent, listing, "headline");
        }
        finally
        {
            WithAnswer(FakeChatClientFactory.DefaultContentAnswer);
        }

        int LimitFor(string kind) => requests.First(request => (string?)request["kind"] == kind && (string?)request["mode"] == "text")["maxCharacters"]!.GetValue<int>();

        Assert.Equal(480, LimitFor("public_remarks"));
        Assert.Equal(320, LimitFor("private_remarks"));
        Assert.Equal(100, LimitFor("headline"));
    }

    /// <summary>Variants over the limit are dropped; the ones within it are kept, in order.</summary>
    [Fact]
    public async Task VariantsOverTheLimit_AreNeverStored()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "limit-drop", await VerifiedMlsAsync(publicLimit: 80));
        WithAnswer(_ => Variants("A short and sunny home.", new string('x', 81) + ".", "A second short home."));

        try
        {
            var drafts = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").ToList();

            Assert.Equal(["A short and sunny home.", "A second short home."], drafts.Select(draft => draft["body"]!.GetValue<string>()));
            Assert.Equal([0, 1], drafts.Select(draft => draft["variantIndex"]!.GetValue<int>()));
            Assert.All(drafts, draft => Assert.True(draft["characterCount"]!.GetValue<int>() <= 80));
        }
        finally
        {
            WithAnswer(FakeChatClientFactory.DefaultContentAnswer);
        }
    }

    /// <summary>FR-015: when nothing fits, the step fails explicitly rather than storing an overlong draft.</summary>
    [Fact]
    public async Task WhenNoVariantFits_TheStepFails_AndNothingIsStored()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "limit-none", await VerifiedMlsAsync(publicLimit: 40));
        WithAnswer(_ => Variants(new string('x', 60) + ".", new string('y', 70) + "."));

        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(factory, agent, listing, "public_remarks")).StatusCode);
            var status = await WaitForCompletionAsync(factory, agent, listing);

            Assert.Equal("failed", StepState(status, "content_generation"));
            Assert.Empty(await DraftsAsync(factory, agent, listing));
        }
        finally
        {
            WithAnswer(FakeChatClientFactory.DefaultContentAnswer);
        }
    }

    /// <summary>
    /// Research R2 inside the full pipeline: against CRMLS, whose limits are unverified, the
    /// content step is skipped with the reason — the photo steps still run, and nothing is
    /// generated to a guessed limit.
    /// </summary>
    [Fact]
    public async Task InTheFullPipeline_AnUnverifiedProfile_SkipsContentWithTheReason()
    {
        factory.Registry.Enable(AiEnabledApiFactory.Entry(Libs.Ai.AiTaskKey.PhotoAnalysis, "test/vision-model"));
        var (agent, listing) = await ListingOnMlsAsync(factory, "limit-crmls", "CRMLS");
        await UploadAsync(factory, agent, listing, TestImages.Jpeg());

        var status = await RunToCompletionAsync(factory, agent, listing);
        var step = status["steps"]!.AsArray().Single(candidate => (string?)candidate!["name"] == "content_generation")!;

        Assert.Equal("skipped", step["state"]!.GetValue<string>());
        Assert.Contains("not verified", step["failureReason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("done", StepState(status, "photo_analysis"));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.content_drafts where listing_id = $1", listing));
    }

    /// <summary>
    /// The full pipeline against a verified profile generates every kind: three public-remarks
    /// variants, and one each of the private remarks and the headline.
    /// </summary>
    [Fact]
    public async Task InTheFullPipeline_AVerifiedProfile_GeneratesEveryKind()
    {
        factory.Registry.Enable(AiEnabledApiFactory.Entry(Libs.Ai.AiTaskKey.PhotoAnalysis, "test/vision-model"));
        var (agent, listing) = await ListingOnMlsAsync(factory, "limit-full", await VerifiedMlsAsync());
        await UploadAsync(factory, agent, listing, TestImages.Jpeg());

        var status = await RunToCompletionAsync(factory, agent, listing);
        var drafts = await DraftsAsync(factory, agent, listing);

        Assert.Equal("done", StepState(status, "content_generation"));
        Assert.Equal(3, OfKind(drafts, "public_remarks").Count());
        Assert.Single(OfKind(drafts, "private_remarks"));
        Assert.Single(OfKind(drafts, "headline"));
    }

    private void WithAnswer(Func<JsonObject, JsonObject> textAnswer) =>
        factory.Models.ContentAnswer = request => (string?)request["mode"] == "structured"
            ? FakeChatClientFactory.DefaultContentAnswer(request)
            : textAnswer(request);

    private static JsonObject Variants(params string[] bodies) => new()
    {
        ["variants"] = new JsonArray(bodies
            .Select(body => (JsonNode)new JsonObject
            {
                ["sentences"] = new JsonArray(new JsonObject { ["text"] = body, ["supportingFieldIds"] = new JsonArray() }),
            })
            .ToArray()),
    };
}
