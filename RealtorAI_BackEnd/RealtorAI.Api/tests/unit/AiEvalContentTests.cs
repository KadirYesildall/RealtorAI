using System.Text.Json.Nodes;
using AiEval;
using Api.Data;
using Api.Endpoints;
using Libs.Ai;
using Libs.Ai.Tasks;
using Libs.Reso;
using Microsoft.Extensions.AI;
using Modules.Content;
using Modules.Media;
using Pipeline;
using Pipeline.Steps;

namespace Tests.Unit;

/// <summary>
/// T158 — the content suite measures <c>listing.content</c> on the calls a pipeline run makes,
/// writes only to verified limits, and its shipped set is well-formed.
///
/// <para>
/// No network: the model is a scripted chat client behind the real <see cref="EvalModelInvoker"/>,
/// with the repository's real <c>listing.content</c> prompt and schema. The parity test runs the
/// production <see cref="ContentGenerationStep"/> on the same listing and compares what each sent,
/// because tokens per listing measured on a different sequence of requests would describe a
/// different listing.
/// </para>
/// </summary>
public sealed class AiEvalContentTests : IDisposable
{
    private static readonly ImageNormalizer Normalizer = new(new NormalizationOptions(1280, 85, 100_000_000));

    private static readonly RegistryTarget ContentTarget = new(
        Guid.Parse("0190f6a1-0000-7000-8000-0000000000d1"),
        AiTaskKey.ListingContent,
        "eval-provider",
        "eval-org/content-model",
        "https://models.example.test/v1",
        """{"temperature":0.4}""",
        Enabled: false);

    private static readonly Lazy<IPromptRegistry> RepositoryPrompts = new(() =>
        PromptRegistry.Load(Path.Combine(
            Path.GetDirectoryName(EvalEnvironment.FindApiDirectory(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("The API host directory was not found above the test binaries."))!,
            "prompts")));

    private static readonly EvalContentProfile Verified = new(new ContentProfile(true, 1000, 500), ResoLookupMapping.None);

    private readonly string root = Path.Combine(Path.GetTempPath(), "aieval-content-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ───────────────────────────── Test doubles ─────────────────────────────

    /// <summary>A model that answers each request from its text.</summary>
    private sealed class ScriptedModel(Func<JsonNode, string> answer) : IChatClientFactory
    {
        internal Func<JsonNode, string> Respond { get; } = answer;

        internal List<string> Inputs { get; } = [];

        public IChatClient Create(ModelRegistryEntry entry) => new Client(this);

        private sealed class Client(ScriptedModel owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                // The request is the first user message; a retry appends a plain-text correction after it.
                var users = messages.Where(message => message.Role == ChatRole.User).ToList();
                var input = users[0].Text;

                if (users.Count == 1)
                {
                    owner.Inputs.Add(input);
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Respond(JsonNode.Parse(input)!)))
                {
                    Usage = new UsageDetails { InputTokenCount = 300, OutputTokenCount = 40 },
                });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The harness never streams.");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Production's side of the parity check: an <see cref="IAiService"/> answering the same way.</summary>
    private sealed class ScriptedAiService(Func<JsonNode, string> answer) : IAiService
    {
        internal List<string> Inputs { get; } = [];

        public Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            Inputs.Add(request.Input);
            return Task.FromResult(new AiResult(JsonNode.Parse(answer(JsonNode.Parse(request.Input)!))!, "eval-org/content-model", 1, "hash", FromCache: false));
        }
    }

    /// <summary>
    /// A content store holding one listing. Saving structured values stores them as production
    /// does — unconfirmed AI-detected facts, never over a sourced one — so the step's second read
    /// returns what the copy may cite.
    /// </summary>
    private sealed class OneListingStore(ContentSources sources) : IContentStore
    {
        private ContentSources current = sources;

        public Task<ContentSources> LoadSourcesAsync(Guid listingId, CancellationToken cancellationToken) => Task.FromResult(current);

        public Task SaveStructuredAsync(PipelineRunContext run, IReadOnlyDictionary<string, IReadOnlyList<string>> values, AiResult aiCall, CancellationToken cancellationToken)
        {
            var facts = current.Facts.ToList();

            foreach (var (key, proposed) in values.Where(value => facts.All(fact => fact.Key != value.Key)))
            {
                facts.Add(new ContentFact(Guid.NewGuid(), key, new JsonArray([.. proposed.Select(value => (JsonNode)JsonValue.Create(value))]), ListingContentEval.AiDetectedFromPhoto, Confirmed: false));
            }

            current = current with { Facts = facts };
            return Task.CompletedTask;
        }

        public Task SaveGenerationAsync(PipelineRunContext run, KindGeneration generation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AuditUnusedCallAsync(PipelineRunContext run, string kind, AiResult aiCall, string outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static EvalModelInvoker Invoker(IChatClientFactory model) =>
        new(ContentTarget, RepositoryPrompts.Value, model, Normalizer);

    /// <summary>
    /// A schema-valid answer: structured values for a structured request, one cited sentence per
    /// requested variant for a text request.
    /// </summary>
    private static string Answer(JsonNode request) => request["mode"]!.GetValue<string>() == "structured"
        ? """{"variants":[],"structured":{"Appliances":["Dishwasher","NotALookupValue"],"Flooring":["Hardwood"]}}"""
        : new JsonObject
        {
            ["variants"] = new JsonArray([.. Enumerable.Range(0, request["variantCount"]!.GetValue<int>()).Select(_ => (JsonNode)new JsonObject
            {
                ["sentences"] = new JsonArray(new JsonObject { ["text"] = "A bright home.", ["supportingFieldIds"] = new JsonArray() }),
            })]),
        }.ToJsonString();

    private static ContentListingItem Listing(string id = "lc-test", IReadOnlyList<ContentListingFact>? facts = null) => new(
        id,
        "CRMLS",
        "Sacramento",
        "CA",
        "SingleFamilyResidence",
        489000,
        3,
        2,
        "New roof in 2022.",
        facts ?? [new ContentListingFact("LivingArea", JsonValue.Create(1412), "public_record", true)],
        [new PhotoObservation("kitchen", ["gas range"]), new PhotoObservation("living_room", ["fireplace"])]);

    /// <summary>A request with the ids that legitimately differ between runs removed.</summary>
    private static string Comparable(string input)
    {
        var request = JsonNode.Parse(input)!.AsObject();
        request.Remove("run");

        foreach (var fact in request["facts"]!.AsArray())
        {
            fact!.AsObject().Remove("fieldId");
        }

        return request.ToJsonString();
    }

    private EvalSet WriteSet(params JsonObject[] items)
    {
        var folder = Path.Combine(root, AiTaskKey.ListingContent);
        Directory.CreateDirectory(folder);

        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = "content-listings-test",
            ["task"] = AiTaskKey.ListingContent,
            ["version"] = 1,
            ["description"] = "Listings written for this test.",
            ["provenance"] = new JsonObject
            {
                ["source"] = "Written for this test.",
                ["rights"] = "Test data.",
                ["labelledBy"] = "Not labelled.",
                ["labellingMethod"] = "By hand.",
                ["createdOn"] = "2026-11-16",
            },
            ["review"] = new JsonObject { ["reviewed"] = false, ["reviewer"] = null, ["reviewedAt"] = null, ["notes"] = null },
            ["items"] = new JsonArray([.. items]),
        };

        var path = Path.Combine(folder, "content-listings-test.json");
        File.WriteAllText(path, manifest.ToJsonString());

        return EvalSet.Load(path);
    }

    private static JsonObject Item(string id, string provenance = "public_record", bool confirmed = true, string targetMlsId = "CRMLS") => new()
    {
        ["id"] = id,
        ["targetMlsId"] = targetMlsId,
        ["city"] = "Sacramento",
        ["state"] = "CA",
        ["propertyType"] = "SingleFamilyResidence",
        ["listPrice"] = 489000,
        ["bedrooms"] = 3,
        ["bathrooms"] = 2,
        ["agentNotes"] = null,
        ["facts"] = new JsonArray(new JsonObject { ["key"] = "LivingArea", ["value"] = 1412, ["provenance"] = provenance, ["confirmed"] = confirmed }),
        ["photos"] = new JsonArray(new JsonObject { ["roomType"] = "kitchen", ["features"] = new JsonArray("gas range") }),
    };

    // ───────────────────────────── Parity with production ─────────────────────────────

    [Fact]
    public async Task Generate_SendsTheRequestsContentGenerationStepSends_InTheSameOrder()
    {
        var item = Listing();
        var model = new ScriptedModel(Answer);

        await ListingContentEval.GenerateAsync(Invoker(model), item, Verified, default);

        var production = new ScriptedAiService(Answer);
        var sources = new ContentSources(
            new ContentBasics(item.City, item.State, item.PropertyType, item.ListPrice, item.Bedrooms, item.Bathrooms, item.AgentNotes),
            [.. item.Facts.Select(fact => new ContentFact(Guid.NewGuid(), fact.Key, fact.Value?.DeepClone(), fact.Provenance, fact.Confirmed))],
            item.Photos,
            Verified.Profile,
            Verified.Lookups,
            ContentRepresentation.Label(Tone.Professional));
        var run = new PipelineRunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [], null, null, [PipelineStep.ContentGeneration], null, null, null, null, null);

        var outcome = await new ContentGenerationStep(new OneListingStore(sources), production, new ContentGenerationOptions(4000)).ExecuteAsync(run, default);

        Assert.Equal(StepOutcome.Done, outcome);
        Assert.Equal(production.Inputs.Select(Comparable), model.Inputs.Select(Comparable));
        Assert.Equal(1 + ContentKinds.All.Count, model.Inputs.Count);
    }

    [Fact]
    public void Tone_IsTheHostsDefaultToneLabel() =>
        Assert.Equal(ListingContentEval.Tone, ContentRepresentation.Label(Tone.Professional));

    // ───────────────────────────── Generation ─────────────────────────────

    [Fact]
    public async Task Generate_PermittedProposals_BecomeUnconfirmedAiDetectedFacts_InTheTextRequests()
    {
        var model = new ScriptedModel(Answer);

        await ListingContentEval.GenerateAsync(Invoker(model), Listing(), Verified, default);

        var text = JsonNode.Parse(model.Inputs[1])!;
        var appliances = text["facts"]!.AsArray().Single(fact => fact!["key"]!.GetValue<string>() == "Appliances")!;

        // A value outside the RESO lookups is dropped, as production drops it.
        Assert.Equal("""["Dishwasher"]""", appliances["value"]!.ToJsonString());
        Assert.Equal(ListingContentEval.AiDetectedFromPhoto, appliances["provenance"]!.GetValue<string>());
        Assert.False(appliances["confirmed"]!.GetValue<bool>());
    }

    [Fact]
    public void WithProposals_NeverOverwritesAFactTheListingAlreadyHolds()
    {
        var item = Listing(facts: [new ContentListingFact("Flooring", new JsonArray("Carpet"), "agent_entered", true)]);
        var facts = new[] { new ContentFact(Guid.NewGuid(), "Flooring", new JsonArray("Carpet"), "agent_entered", true) };

        var merged = ListingContentEval.WithProposals(
            item,
            facts,
            new Dictionary<string, IReadOnlyList<string>> { ["Flooring"] = ["Hardwood"] },
            ResoLookupMapping.None);

        var flooring = Assert.Single(merged);
        Assert.Equal("""["Carpet"]""", flooring.Value!.ToJsonString());
    }

    [Fact]
    public async Task Generate_AKindWithNoVerifiedLimit_IsNotGenerated()
    {
        var model = new ScriptedModel(Answer);
        var noPrivateLimit = new EvalContentProfile(new ContentProfile(true, 1000, null), ResoLookupMapping.None);

        var listing = await ListingContentEval.GenerateAsync(Invoker(model), Listing(), noPrivateLimit, default);

        Assert.Equal(3, listing.Calls.Count);
        Assert.DoesNotContain(model.Inputs, input => input.Contains("\"kind\":\"private_remarks\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generate_WritesEachKindToItsLimit_WithTheProductionVariantCount()
    {
        var model = new ScriptedModel(Answer);

        await ListingContentEval.GenerateAsync(Invoker(model), Listing(), Verified, default);

        var texts = model.Inputs.Skip(1).Select(input => JsonNode.Parse(input)!).ToList();

        Assert.Equal(ContentKinds.All, texts.Select(text => text["kind"]!.GetValue<string>()));
        Assert.Equal([1000, 500, ContentRules.HeadlineMaxChars], texts.Select(text => text["maxCharacters"]!.GetValue<int>()));
        Assert.Equal([ContentRules.DefaultPublicVariants, 1, 1], texts.Select(text => text["variantCount"]!.GetValue<int>()));
        Assert.All(texts, text => Assert.Equal(ListingContentEval.Tone, text["tone"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Generate_AStructuredAnswerInvalidTwice_LeavesTheFacts_AndCountsAsInvalid()
    {
        var model = new ScriptedModel(request => request["mode"]!.GetValue<string>() == "structured" ? """{"structured":{}}""" : Answer(request));

        var listing = await ListingContentEval.GenerateAsync(Invoker(model), Listing(), Verified, default);

        Assert.Equal(EvalCallOutcome.InvalidAfterRetry, listing.Calls[0].Outcome);
        Assert.Single(JsonNode.Parse(model.Inputs[^1])!["facts"]!.AsArray());
    }

    // ───────────────────────────── The suite ─────────────────────────────

    [Fact]
    public async Task Run_MeasuresSchemaValidity_AndTokensSummedOverEveryCallOfAListing()
    {
        var set = WriteSet(Item("lc-a"), Item("lc-b"));
        var suite = new ListingContentEval(() => set, (_, _) => Task.FromResult<EvalContentProfile?>(Verified), TextWriter.Null);

        var run = await suite.RunAsync(Invoker(new ScriptedModel(Answer)), default);

        Assert.Empty(run.Unmeasured);
        Assert.Equal(1.0, run.Measurements[MetricCatalog.SchemaValidityRate]);

        // Four calls per listing (structured, then three kinds), 340 tokens each.
        Assert.Equal(4 * 340.0, run.Measurements[MetricCatalog.TokensPerListing]);
    }

    [Fact]
    public async Task Run_AnUnverifiedProfile_CallsNoModel_AndLeavesBothMetricsUnmeasured()
    {
        var set = WriteSet(Item("lc-a"));
        var model = new ScriptedModel(Answer);
        var unverified = new EvalContentProfile(new ContentProfile(false, null, null), ResoLookupMapping.None);
        var suite = new ListingContentEval(() => set, (_, _) => Task.FromResult<EvalContentProfile?>(unverified), TextWriter.Null);

        var run = await suite.RunAsync(Invoker(model), default);

        Assert.Empty(model.Inputs);
        Assert.Empty(run.Measurements);
        var reason = Assert.Single(run.Unmeasured);
        Assert.Contains(MetricCatalog.SchemaValidityRate, reason, StringComparison.Ordinal);
        Assert.Contains(MetricCatalog.TokensPerListing, reason, StringComparison.Ordinal);
        Assert.Contains("T160", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AMissingProfile_CallsNoModel_AndSaysWhichProfile()
    {
        var set = WriteSet(Item("lc-a", targetMlsId: "NOPE"));
        var model = new ScriptedModel(Answer);
        var suite = new ListingContentEval(() => set, (_, _) => Task.FromResult<EvalContentProfile?>(null), TextWriter.Null);

        var run = await suite.RunAsync(Invoker(model), default);

        Assert.Empty(model.Inputs);
        Assert.Contains("'NOPE'", Assert.Single(run.Unmeasured), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AnUnverifiedProfile_IsIncomplete_NotAFailure()
    {
        var set = WriteSet(Item("lc-a"));
        var unverified = new EvalContentProfile(new ContentProfile(false, null, null), ResoLookupMapping.None);
        var suite = new ListingContentEval(() => set, (_, _) => Task.FromResult<EvalContentProfile?>(unverified), TextWriter.Null);

        var run = await suite.RunAsync(Invoker(new ScriptedModel(Answer)), default);
        var outcome = EvalVerdict.Evaluate(new MetricCatalog(EvalBudget.Default).For(AiTaskKey.ListingContent), run, AiTaskKey.ListingContent);

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
    }

    [Theory]
    [InlineData("ai_detected_from_photo", true)]
    [InlineData("agent_entered", false)]
    public void ReadItems_RejectsAFactThatIsNotSourcedAndConfirmed(string provenance, bool confirmed)
    {
        var set = WriteSet(Item("lc-a", provenance, confirmed));

        Assert.Throws<InvalidDataException>(() => ListingContentEval.ReadItems(set));
    }

    [Fact]
    public void ReadItems_RejectsARepeatedId()
    {
        var set = WriteSet(Item("lc-a"), Item("lc-a"));

        Assert.Throws<InvalidDataException>(() => ListingContentEval.ReadItems(set));
    }

    // ───────────────────────────── The shipped set ─────────────────────────────

    private static EvalSet Shipped() => EvalSet.LoadShipped(AiTaskKey.ListingContent, ListingContentEval.SetFileName);

    [Fact]
    public void ShippedSet_IsUnreviewed()
    {
        var set = Shipped();

        Assert.False(set.IsReviewed);
    }

    [Fact]
    public void ShippedSet_IsWellFormed_AndEveryListingTargetsAProfileTheRepositorySeeds()
    {
        var items = ListingContentEval.ReadItems(Shipped());

        Assert.Equal(12, items.Count);
        Assert.All(items, item => Assert.Equal("CRMLS", item.TargetMlsId));
        Assert.All(items, item => Assert.True(ResoPropertySubType.IsResidential(item.PropertyType), item.PropertyType));
    }

    [Fact]
    public void ShippedSuites_IncludeTheContentSuite() =>
        Assert.IsType<ListingContentEval>(EvalSuites.Shipped(null)[AiTaskKey.ListingContent]);
}
