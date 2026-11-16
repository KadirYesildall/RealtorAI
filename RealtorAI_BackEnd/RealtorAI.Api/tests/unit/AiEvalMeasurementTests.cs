using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AiEval;
using ImageMagick;
using Libs.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Modules.Media;

namespace Tests.Unit;

/// <summary>
/// T158 — the harness calls an unenabled model exactly as production would, and turns those calls
/// into the catalog's schema-validity, latency and per-listing consumption metrics.
///
/// <para>
/// No network: the chat client is a scripted fake behind the real
/// <see cref="EvalModelInvoker"/>, which is the code under test. What matters most here is what
/// cannot be seen in a real run — that a schema retry is not counted as a first-attempt success,
/// that a provider outage is not scored as bad output, that missing usage does not read as zero
/// tokens, and that the only model a suite can reach is the registry row under evaluation.
/// </para>
/// </summary>
public sealed class AiEvalMeasurementTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": ["roomType"],
          "properties": { "roomType": { "type": "string" } }
        }
        """;

    private const string Valid = """{"roomType":"kitchen"}""";

    private const string Invalid = """{"room":"kitchen"}""";

    private static readonly ImageNormalizer Normalizer = new(new NormalizationOptions(1280, 85, 100_000_000));

    private static readonly RegistryTarget Target = new(
        Guid.Parse("0190f6a1-0000-7000-8000-000000000001"),
        AiTaskKey.PhotoAnalysis,
        "eval-provider",
        "eval-org/vision-model",
        "https://models.example.test/v1",
        """{"temperature":0.2}""",
        Enabled: false);

    // ───────────────────────────── Test doubles ─────────────────────────────

    private sealed class FakePrompts(string schema) : IPromptRegistry
    {
        internal PromptDefinition Prompt { get; } =
            new(AiTaskKey.PhotoAnalysis, 3, "You classify rooms.", schema, "abc123");

        public PromptDefinition GetActive(string taskKey) => Prompt;

        public PromptDefinition GetVersion(string taskKey, int version) => Prompt;

        public IReadOnlyList<PromptDefinition> All() => [Prompt];

        public Task VerifyAgainstRecordedHashesAsync(IPromptVersionStore store, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>One scripted reply: response text with optional usage, or an exception.</summary>
    private sealed record Reply(string? Text = null, int? In = 100, int? Out = 20, Exception? Throw = null);

    private sealed class ScriptedModel : IChatClientFactory
    {
        private readonly ConcurrentQueue<Reply> replies;
        private int inFlight;

        internal ScriptedModel(params Reply[] replies) => this.replies = new ConcurrentQueue<Reply>(replies);

        /// <summary>Answers every request with this when the script is exhausted.</summary>
        internal Reply? Default { get; init; }

        internal List<ModelRegistryEntry> Entries { get; } = [];

        internal List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

        internal int MaxConcurrent { get; private set; }

        public IChatClient Create(ModelRegistryEntry entry)
        {
            Entries.Add(entry);
            return new Client(this);
        }

        private sealed class Client(ScriptedModel owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var concurrent = Interlocked.Increment(ref owner.inFlight);
                owner.MaxConcurrent = Math.Max(owner.MaxConcurrent, concurrent);

                try
                {
                    // Yield so overlapping calls, if the runner made any, would actually overlap.
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();

                    lock (owner.Requests)
                    {
                        owner.Requests.Add(([.. messages], options));
                    }

                    var reply = owner.replies.TryDequeue(out var next)
                        ? next
                        : owner.Default ?? throw new InvalidOperationException("The script ran out of replies.");

                    if (reply.Throw is not null)
                    {
                        throw reply.Throw;
                    }

                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply.Text))
                    {
                        Usage = reply.In is null && reply.Out is null
                            ? null
                            : new UsageDetails { InputTokenCount = reply.In, OutputTokenCount = reply.Out },
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref owner.inFlight);
                }
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

    private static EvalModelInvoker Invoker(ScriptedModel model, string schema = Schema) =>
        new(Target, new FakePrompts(schema), model, Normalizer);

    private static byte[] Photo(uint width = 2400, uint height = 1600)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(34, 1), new Rational(3, 1), new Rational(1524, 100)]);
        image.SetProfile(exif);
        image.Format = MagickFormat.Jpeg;

        return image.ToByteArray();
    }

    private static EvalCallRecord Call(
        EvalCallOutcome outcome,
        int tokensIn = 100,
        int tokensOut = 20,
        int images = 1,
        bool usage = true) =>
        new(outcome, TimeSpan.FromSeconds(1), tokensIn, tokensOut, usage, images, outcome == EvalCallOutcome.ValidFirstAttempt ? 1 : 2,
            outcome == EvalCallOutcome.ProviderFailed ? "503 from provider" : null);

    private static EvalListingRun Listing(double seconds, params EvalCallRecord[] calls) =>
        new(calls, TimeSpan.FromSeconds(seconds));

    // ───────────────────────────── Invocation: first-attempt validity ─────────────────────────────

    [Fact]
    public async Task Invoke_AValidFirstAnswer_IsCountedAsFirstAttemptValid()
    {
        var model = new ScriptedModel(new Reply(Valid, In: 120, Out: 30));

        var call = await Invoker(model).InvokeAsync("input", [], default);

        Assert.Equal(EvalCallOutcome.ValidFirstAttempt, call.Record.Outcome);
        Assert.True(call.Record.FirstAttemptValid);
        Assert.Equal(1, call.Record.Attempts);
        Assert.Equal(120, call.Record.InputTokens);
        Assert.Equal(30, call.Record.OutputTokens);
        Assert.Equal("kitchen", call.Document!["roomType"]!.GetValue<string>());
    }

    /// <summary>
    /// A response rescued by FR-015's one retry is a success for the listing but not for the
    /// metric: the model did not produce the shape when first asked.
    /// </summary>
    [Fact]
    public async Task Invoke_AnAnswerRescuedByTheRetry_IsNotFirstAttemptValid_AndBothAttemptsAreCharged()
    {
        var model = new ScriptedModel(new Reply(Invalid, In: 100, Out: 10), new Reply(Valid, In: 140, Out: 12));
        var invoker = Invoker(model);

        var call = await invoker.InvokeAsync("input", [invoker.PrepareImage(Photo())], default);

        Assert.Equal(EvalCallOutcome.ValidAfterRetry, call.Record.Outcome);
        Assert.False(call.Record.FirstAttemptValid);
        Assert.Equal(2, call.Record.Attempts);
        Assert.Equal(240, call.Record.InputTokens);
        Assert.Equal(22, call.Record.OutputTokens);

        // The retry re-sends the image, so two images crossed the boundary.
        Assert.Equal(2, call.Record.ImagesSent);

        // And it is production's retry: the second request carries the validation feedback.
        Assert.Equal(3, model.Requests[1].Messages.Count);
        Assert.Contains("did not match the required schema", model.Requests[1].Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_TwoInvalidAnswers_AreInvalidAfterRetry_WithNoDocument()
    {
        var model = new ScriptedModel(new Reply(Invalid), new Reply("not json"));

        var call = await Invoker(model).InvokeAsync("input", [], default);

        Assert.Equal(EvalCallOutcome.InvalidAfterRetry, call.Record.Outcome);
        Assert.Null(call.Document);
        Assert.Equal(2, call.Record.Attempts);
    }

    /// <summary>An outage is not bad output: it is recorded apart, with the provider's message.</summary>
    [Fact]
    public async Task Invoke_AProviderFailure_IsRecordedAsSuch_NotAsInvalidOutput()
    {
        var model = new ScriptedModel(new Reply(Throw: new HttpRequestException("503 Service Unavailable")));

        var call = await Invoker(model).InvokeAsync("input", [], default);

        Assert.Equal(EvalCallOutcome.ProviderFailed, call.Record.Outcome);
        Assert.Equal("503 Service Unavailable", call.Record.ProviderError);
        Assert.Null(call.Document);
    }

    /// <summary>
    /// A provider timeout is an outage too. The AI HTTP client's resilience pipeline reports it as
    /// Polly's <see cref="Polly.Timeout.TimeoutRejectedException"/>, which is not a
    /// <see cref="TimeoutException"/>; unclassified, one slow call would crash a long photo run
    /// instead of being counted and reported.
    /// </summary>
    [Fact]
    public async Task Invoke_AProviderTimeout_IsRecordedAsAProviderFailure()
    {
        var timeout = new Polly.Timeout.TimeoutRejectedException("The operation did not complete within the allowed timeout.");
        var model = new ScriptedModel(new Reply(Throw: timeout));

        var call = await Invoker(model).InvokeAsync("input", [], default);

        Assert.Equal(EvalCallOutcome.ProviderFailed, call.Record.Outcome);
        Assert.Equal(timeout.Message, call.Record.ProviderError);
        Assert.Null(call.Document);
    }

    [Fact]
    public async Task Invoke_CancellingTheEvaluation_PropagatesInsteadOfRecordingAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var model = new ScriptedModel(new Reply(Valid));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoker(model).InvokeAsync("input", [], cancellation.Token));
    }

    [Fact]
    public async Task Invoke_AResponseWithoutUsage_IsFlagged()
    {
        var model = new ScriptedModel(new Reply(Valid, In: null, Out: null));

        var call = await Invoker(model).InvokeAsync("input", [], default);

        Assert.False(call.Record.UsageReported);
        Assert.Equal(0, call.Record.Tokens);
    }

    /// <summary>Our own broken schema is a deployment fault, found before any model is called.</summary>
    [Fact]
    public void Invoker_RefusesABrokenSchema_BeforeCallingAnyModel()
    {
        var model = new ScriptedModel();

        Assert.Throws<StructuredOutputException>(() => Invoker(model, "{ not json"));
        Assert.Empty(model.Entries);
    }

    // ───────────────────────────── Invocation: identity and request shape ─────────────────────────────

    /// <summary>
    /// The only model a suite can reach is the registry row under evaluation: the factory is given
    /// that row's provider, model id, endpoint and parameters, with no fallback, and with the VIII.4
    /// gate fields at their not-cleared values.
    /// </summary>
    [Fact]
    public async Task Invoke_ReachesOnlyTheRegistryTargetsModel()
    {
        var model = new ScriptedModel(new Reply(Invalid), new Reply(Valid));

        await Invoker(model).InvokeAsync("input", [], default);

        var entry = Assert.Single(model.Entries);
        Assert.Equal(Target.Id, entry.Id);
        Assert.Equal(Target.TaskKey, entry.TaskKey);
        Assert.Equal(Target.Provider, entry.Provider);
        Assert.Equal(Target.ModelId, entry.ModelId);
        Assert.Equal(Target.Endpoint, entry.Endpoint);
        Assert.Equal(Target.Parameters, entry.Parameters);
        Assert.Null(entry.FallbackRegistryId);
        Assert.Null(entry.License);
        Assert.Equal(ModelEvalStatus.Unverified, entry.EvalStatus);
    }

    /// <summary>
    /// The request is production's: the prompt as the system message, the input and the image in
    /// one user message, a JSON response format and the row's own temperature (FR-024).
    /// </summary>
    [Fact]
    public async Task Invoke_SendsTheProductionRequest()
    {
        var model = new ScriptedModel(new Reply(Valid));
        var invoker = Invoker(model);

        await invoker.InvokeAsync("""{"photoId":"x"}""", [invoker.PrepareImage(Photo())], default);

        var (messages, options) = Assert.Single(model.Requests);

        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("You classify rooms.", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("""{"photoId":"x"}""", messages[1].Text);
        Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
        Assert.Equal(0.2f, options.Temperature);
    }

    /// <summary>VIII.4.c: what reaches the model is the normalized copy — downscaled, GPS gone.</summary>
    [Fact]
    public async Task Invoke_SendsTheNormalizedCopy_NeverTheOriginal()
    {
        var original = Photo(2400, 1600);
        var model = new ScriptedModel(new Reply(Valid));
        var invoker = Invoker(model);

        await invoker.InvokeAsync("input", [invoker.PrepareImage(original)], default);

        var sent = Assert.Single(model.Requests[0].Messages[1].Contents.OfType<DataContent>());

        Assert.Equal("image/jpeg", sent.MediaType);
        Assert.NotEqual(original, sent.Data.ToArray());

        using var image = new MagickImage(sent.Data.ToArray());
        Assert.Equal(1280u, Math.Max(image.Width, image.Height));
        Assert.Null(image.GetExifProfile());
    }

    // ───────────────────────────── Listings, the way the pipeline runs them ─────────────────────────────

    /// <summary>
    /// Photos are analysed one after another, in listings of SC-005's thirty; a partial final
    /// group is analysed but forms no listing.
    /// </summary>
    [Fact]
    public async Task Runner_AnalysesListingsOfThirty_Sequentially()
    {
        var model = new ScriptedModel { Default = new Reply(Valid) };
        var invoker = Invoker(model);
        var image = invoker.PrepareImage(Photo(64, 48));

        var run = await PhotoListingRunner.RunAsync(invoker, [.. Enumerable.Repeat(image, 65)], default);

        Assert.Equal(65, run.Photos.Count);
        Assert.Equal(65, run.Calls.Count);
        Assert.Equal(Enumerable.Range(0, 65), run.Photos.Select(photo => photo.Index));
        Assert.Equal(2, run.Listings.Count);
        Assert.All(run.Listings, listing => Assert.Equal(MetricCatalog.LatencyListingPhotoCount, listing.Calls.Count));
        Assert.Equal(1, model.MaxConcurrent);
    }

    /// <summary>Each call carries one photo and the pipeline's own input text, under a fresh id.</summary>
    [Fact]
    public async Task Runner_SendsEachPhotoAloneWithThePipelinesInput()
    {
        var model = new ScriptedModel { Default = new Reply(Valid) };
        var invoker = Invoker(model);
        var image = invoker.PrepareImage(Photo(64, 48));

        await PhotoListingRunner.RunAsync(invoker, [image, image], default);

        var ids = new HashSet<Guid>();

        foreach (var (messages, _) in model.Requests)
        {
            Assert.Single(messages[1].Contents.OfType<DataContent>());

            var input = JsonNode.Parse(messages[1].Text)!;
            var photoId = input["photoId"]!.GetValue<Guid>();

            Assert.Equal(Libs.Ai.Tasks.PhotoAnalysisTask.BuildInput(photoId), messages[1].Text);
            Assert.True(ids.Add(photoId));
        }
    }

    // ───────────────────────────── Schema validity ─────────────────────────────

    [Fact]
    public void SchemaValidity_CountsOnlyFirstAttemptSuccesses()
    {
        var rate = CallMetrics.SchemaValidityRate(
        [
            Call(EvalCallOutcome.ValidFirstAttempt),
            Call(EvalCallOutcome.ValidFirstAttempt),
            Call(EvalCallOutcome.ValidAfterRetry),
            Call(EvalCallOutcome.InvalidAfterRetry),
        ]);

        Assert.Equal(0.5, rate);
    }

    [Fact]
    public void SchemaValidity_RefusesAProviderFailure_AndAnEmptySample()
    {
        Assert.Throws<ArgumentException>(() => CallMetrics.SchemaValidityRate(
            [Call(EvalCallOutcome.ValidFirstAttempt), Call(EvalCallOutcome.ProviderFailed)]));
        Assert.Throws<ArgumentException>(() => CallMetrics.SchemaValidityRate([]));
    }

    // ───────────────────────────── Percentile ─────────────────────────────

    /// <summary>
    /// Nearest rank, ⌈p·n/100⌉. Small samples round up to an observed value: with fewer than ten
    /// listings, p90 is the slowest.
    /// </summary>
    [Theory]
    [InlineData(new[] { 42.0 }, 42.0)]
    [InlineData(new[] { 10.0, 20.0 }, 20.0)]
    [InlineData(new[] { 50.0, 10.0, 40.0, 20.0, 30.0 }, 50.0)]
    [InlineData(new[] { 1.0, 2, 3, 4, 5, 6, 7 }, 7.0)]
    [InlineData(new[] { 10.0, 1, 9, 2, 8, 3, 7, 4, 6, 5 }, 9.0)]
    [InlineData(new[] { 1.0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, 10.0)]
    public void Percentile90_IsNearestRank(double[] values, double expected)
    {
        Assert.Equal(expected, CallMetrics.Percentile(values, 90));
    }

    /// <summary>At exact boundaries the rank must not drift by floating-point error.</summary>
    [Theory]
    [InlineData(10, 9)]
    [InlineData(20, 18)]
    [InlineData(30, 27)]
    [InlineData(70, 63)]
    [InlineData(100, 90)]
    public void Percentile90_HitsExactRanks(int count, int expectedRank)
    {
        var values = Enumerable.Range(1, count).Select(value => (double)value).ToList();

        Assert.Equal(expectedRank, CallMetrics.Percentile(values, 90));
    }

    [Fact]
    public void Percentile_DoesNotReorderItsInput()
    {
        double[] values = [3, 1, 2];

        _ = CallMetrics.Percentile(values, 90);

        Assert.Equal(new[] { 3.0, 1, 2 }, values);
    }

    [Fact]
    public void Percentile_RejectsUndefinedInputs()
    {
        Assert.Throws<ArgumentException>(() => CallMetrics.Percentile([], 90));
        Assert.Throws<ArgumentOutOfRangeException>(() => CallMetrics.Percentile([1.0], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CallMetrics.Percentile([1.0], 101));
        Assert.Throws<ArgumentException>(() => CallMetrics.Percentile([1.0, double.NaN], 90));
    }

    [Fact]
    public void Latency_IsTheP90OfListingWallTimes()
    {
        var listings = Enumerable.Range(1, 10)
            .Select(seconds => Listing(seconds * 10, Call(EvalCallOutcome.ValidFirstAttempt)))
            .ToList();

        Assert.Equal(90, CallMetrics.LatencyP90Seconds(listings));
    }

    // ───────────────────────────── Per-listing consumption ─────────────────────────────

    [Fact]
    public void TokensAndImages_AreMeansPerListing()
    {
        IReadOnlyList<EvalListingRun> listings =
        [
            Listing(1, Call(EvalCallOutcome.ValidFirstAttempt, 100, 20, 1), Call(EvalCallOutcome.ValidAfterRetry, 200, 40, 2)),
            Listing(1, Call(EvalCallOutcome.ValidFirstAttempt, 60, 0, 1)),
        ];

        // (120 + 240) and 60 → 210; (1 + 2) and 1 → 2.
        Assert.Equal(210, CallMetrics.TokensPerListing(listings));
        Assert.Equal(2, CallMetrics.ImagesPerListing(listings));
    }

    [Fact]
    public void Measure_ReportsTheRequestedMetrics()
    {
        var calls = new[] { Call(EvalCallOutcome.ValidFirstAttempt), Call(EvalCallOutcome.ValidAfterRetry) };

        var measured = CallMetrics.Measure(
            calls,
            [Listing(75, calls)],
            [MetricCatalog.SchemaValidityRate, MetricCatalog.Latency30PhotosP90Seconds, MetricCatalog.TokensPerListing, MetricCatalog.ImagesPerListing]);

        Assert.Empty(measured.Unmeasured);
        Assert.Equal(0.5, measured.Values[MetricCatalog.SchemaValidityRate]);
        Assert.Equal(75, measured.Values[MetricCatalog.Latency30PhotosP90Seconds]);
        Assert.Equal(240, measured.Values[MetricCatalog.TokensPerListing]);
        Assert.Equal(2, measured.Values[MetricCatalog.ImagesPerListing]);
    }

    /// <summary>
    /// One outage withholds every call-level metric, with the reason: a sample missing those calls
    /// would not describe the model.
    /// </summary>
    [Fact]
    public void Measure_WithAProviderFailure_WithholdsEverything_AndSaysWhy()
    {
        var calls = new[] { Call(EvalCallOutcome.ValidFirstAttempt), Call(EvalCallOutcome.ProviderFailed) };

        var measured = CallMetrics.Measure(calls, [Listing(60, calls)], [MetricCatalog.SchemaValidityRate, MetricCatalog.TokensPerListing]);

        Assert.Empty(measured.Values);
        Assert.Contains("1 of 2 model calls failed at the provider", Assert.Single(measured.Unmeasured), StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_WithoutACompleteListing_WithholdsThePerListingMetricsOnly()
    {
        var calls = new[] { Call(EvalCallOutcome.ValidFirstAttempt) };

        var measured = CallMetrics.Measure(
            calls,
            [],
            [MetricCatalog.SchemaValidityRate, MetricCatalog.Latency30PhotosP90Seconds, MetricCatalog.ImagesPerListing]);

        Assert.Equal(new[] { MetricCatalog.SchemaValidityRate }, measured.Values.Keys);
        Assert.Contains("no complete listing", Assert.Single(measured.Unmeasured), StringComparison.Ordinal);
    }

    /// <summary>Missing usage must not read as zero tokens — that would understate consumption.</summary>
    [Fact]
    public void Measure_WithUnreportedUsage_WithholdsTokensOnly()
    {
        var calls = new[] { Call(EvalCallOutcome.ValidFirstAttempt), Call(EvalCallOutcome.ValidFirstAttempt, usage: false) };

        var measured = CallMetrics.Measure(calls, [Listing(10, calls)], [MetricCatalog.TokensPerListing, MetricCatalog.ImagesPerListing]);

        Assert.False(measured.Values.ContainsKey(MetricCatalog.TokensPerListing));
        Assert.Equal(2, measured.Values[MetricCatalog.ImagesPerListing]);
        Assert.Contains(MetricCatalog.TokensPerListing, Assert.Single(measured.Unmeasured), StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_RejectsANonCallMetric()
    {
        Assert.Throws<ArgumentException>(() => CallMetrics.Measure([], [], [MetricCatalog.FairHousingRecall]));
    }

    /// <summary>The measurement wiring must not have moved a bar: R27's null stays null.</summary>
    [Fact]
    public void MeasuredSchemaValidity_StillHasNoThresholdToPass()
    {
        var catalog = new MetricCatalog(EvalBudget.Default);
        var calls = new[] { Call(EvalCallOutcome.ValidFirstAttempt) };
        var measured = CallMetrics.Measure(calls, [], [MetricCatalog.SchemaValidityRate]);

        var (verdict, results) = EvalVerdict.Evaluate(
            catalog.For(AiTaskKey.ListingContent).Where(metric => metric.Key == MetricCatalog.SchemaValidityRate).ToList(),
            measured.Values);

        Assert.Equal(Verdict.Incomplete, verdict);
        Assert.Equal(MetricOutcome.NoThreshold, Assert.Single(results).Outcome);
    }

    // ───────────────────────────── Host configuration ─────────────────────────────

    private static string ApiDirectory() =>
        EvalEnvironment.FindApiDirectory(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("The API host directory was not found above the test binaries.");

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> HostSettings() => new()
    {
        [AiTransportOptions.DefaultBaseUrlKey] = "https://router.example.test/v1",
        [NormalizationOptions.LongEdgePxKey] = "1280",
        [NormalizationOptions.JpegQualityKey] = "85",
        [NormalizationOptions.MaxSourcePixelsKey] = "100000000",
    };

    /// <summary>
    /// The harness builds clients with the host's factory from the host's keys: the configured
    /// default base URL, overridden by a row's own endpoint; the token variable defaults as the
    /// host's does; and the prompts are the repository's, resolved as the host resolves them.
    /// </summary>
    [Fact]
    public void Environment_UsesTheHostsKeysAndClientConstruction()
    {
        using var environment = EvalEnvironment.Create(Configuration(HostSettings()), ApiDirectory());

        Assert.Equal(AiTransportOptions.DefaultApiKeyEnvironmentVariable, environment.Transport.ApiKeyEnvironmentVariable);

        using var defaultClient = environment.Clients.Create(EvalModelInvoker.ToRegistryEntry(Target with { Endpoint = null }));
        using var ownEndpoint = environment.Clients.Create(EvalModelInvoker.ToRegistryEntry(Target));

        Assert.Equal(new Uri("https://router.example.test/v1/"), defaultClient.GetService<ChatClientMetadata>()!.ProviderUri);
        Assert.Equal(new Uri("https://models.example.test/v1/"), ownEndpoint.GetService<ChatClientMetadata>()!.ProviderUri);
        Assert.Equal(Target.ModelId, ownEndpoint.GetService<ChatClientMetadata>()!.DefaultModelId);

        var expectedPrompts = PromptRegistry.ResolveDirectory(null, ApiDirectory());
        Assert.Equal(expectedPrompts, environment.PromptsDirectory);
        Assert.Contains(environment.Prompts.All(), prompt => prompt.TaskKey == AiTaskKey.PhotoAnalysis);
    }

    [Theory]
    [InlineData("Ai:HuggingFace:RouterBaseUrl")]
    [InlineData("Storage:NormalizedLongEdgePx")]
    public void Environment_RefusesAMissingHostSetting(string key)
    {
        var settings = HostSettings();
        settings.Remove(key);

        var exception = Assert.Throws<InvalidOperationException>(
            () => EvalEnvironment.Create(Configuration(settings), ApiDirectory()));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }
}
