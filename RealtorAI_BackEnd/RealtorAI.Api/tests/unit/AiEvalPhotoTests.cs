using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AiEval;
using ImageMagick;
using Libs.Ai;
using Microsoft.Extensions.AI;
using Modules.Media;

namespace Tests.Unit;

/// <summary>
/// T156 — the photo suite scores room types against the labelled set, builds its latency sample
/// deterministically, and refuses to measure on anything but the exact, verified photos the set
/// names; the fetch command downloads them into the cache and refuses bytes that do not match.
///
/// <para>
/// No network. The model is a fake behind the real <see cref="EvalModelInvoker"/> and the
/// repository's own prompts, and it tells photos apart by the width of the tiny image it is sent
/// (photo <c>i</c> is <c>40 + i</c> pixels wide, which normalization leaves alone). HTTP is a
/// fake handler.
/// </para>
/// </summary>
public sealed class AiEvalPhotoTests : IDisposable
{
    private const int BaseWidth = 40;

    private static readonly ImageNormalizer Normalizer = new(new NormalizationOptions(1280, 85, 100_000_000));

    private static readonly RegistryTarget VisionTarget = new(
        Guid.Parse("0190f6a1-0000-7000-8000-0000000000a1"),
        AiTaskKey.PhotoAnalysis,
        "eval-provider",
        "eval-org/vision-model",
        "https://models.example.test/v1",
        """{"temperature":0.2}""",
        Enabled: false);

    private static readonly Lazy<IPromptRegistry> RepositoryPrompts = new(() =>
        PromptRegistry.Load(Path.Combine(
            Path.GetDirectoryName(EvalEnvironment.FindApiDirectory(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("The API host directory was not found above the test binaries."))!,
            "prompts")));

    private readonly string root = Path.Combine(Path.GetTempPath(), "aieval-photo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ───────────────────────────── Test doubles ─────────────────────────────

    /// <summary>A vision model that answers each photo by its index (read from the image width), or throws.</summary>
    private sealed class ScriptedVision(Func<int, string> answer) : IChatClientFactory
    {
        internal List<int> Seen { get; } = [];

        internal Exception? Throw { get; init; }

        public IChatClient Create(ModelRegistryEntry entry) => new Client(this);

        private sealed class Client(ScriptedVision owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var image = messages.Last(message => message.Role == ChatRole.User).Contents.OfType<DataContent>().Single();
                var index = (int)new MagickImageInfo(image.Data.ToArray()).Width - BaseWidth;
                owner.Seen.Add(index);

                if (owner.Throw is not null)
                {
                    throw owner.Throw;
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Respond(index)))
                {
                    Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 80 },
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

        private string Respond(int index) => answer(index);
    }

    /// <summary>An HTTP source that answers from a table of URL to response, recording every request.</summary>
    private sealed class FakeSource(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static EvalModelInvoker Invoker(IChatClientFactory model) =>
        new(VisionTarget, RepositoryPrompts.Value, model, Normalizer);

    /// <summary>A schema-valid photo.analysis v2 answer with <paramref name="roomType"/>.</summary>
    private static string Answer(string roomType) =>
        new JsonObject
        {
            ["roomType"] = roomType,
            ["confidence"] = 0.8,
            ["featureTags"] = new JsonArray(),
            ["contentFlags"] = new JsonArray(),
            ["altText"] = "A room.",
            ["coverRecommended"] = false,
        }.ToJsonString();

    /// <summary>Photo <paramref name="index"/>: a tiny JPEG whose width identifies it.</summary>
    private static byte[] PhotoBytes(int index)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)(BaseWidth + index), 30);
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonObject Item(
        string id,
        string label,
        string sha256,
        string labelConfidence = PhotoEval.Clear,
        string license = "CC0-1.0",
        string fileUrl = "https://upload.example.test/photo.jpg") => new()
        {
            ["id"] = id,
            ["label"] = label,
            ["labelConfidence"] = labelConfidence,
            ["sourcePage"] = "https://commons.example.test/wiki/File:" + id,
            ["fileUrl"] = fileUrl,
            ["license"] = license,
            ["licenseUrl"] = "http://creativecommons.org/publicdomain/zero/1.0/",
            ["author"] = "Test author",
            ["title"] = "Test photo " + id,
            ["sha256"] = sha256,
            ["notes"] = null,
        };

    private EvalSet WriteSet(params JsonObject[] items)
    {
        var folder = Path.Combine(root, AiTaskKey.PhotoAnalysis);
        Directory.CreateDirectory(folder);

        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = "photo-rooms-test",
            ["task"] = AiTaskKey.PhotoAnalysis,
            ["version"] = 1,
            ["description"] = "Photos generated for this test.",
            ["provenance"] = new JsonObject
            {
                ["source"] = "Generated in memory.",
                ["rights"] = "Test data.",
                ["labelledBy"] = "Test",
                ["labellingMethod"] = "By construction.",
                ["createdOn"] = "2026-11-16",
            },
            ["review"] = new JsonObject { ["reviewed"] = false, ["reviewer"] = null, ["reviewedAt"] = null, ["notes"] = null },
            ["items"] = new JsonArray([.. items]),
        };

        var path = Path.Combine(folder, "photo-rooms-test.json");
        File.WriteAllText(path, manifest.ToJsonString());

        return EvalSet.Load(path);
    }

    private EvalSetCache Cache() => new(Path.Combine(root, "cache"));

    /// <summary>
    /// A set of <paramref name="labels"/>.Length generated photos, each cached under its hash
    /// unless its index is in <paramref name="leaveOut"/>.
    /// </summary>
    private (EvalSet Set, EvalSetCache Cache) CachedSet(
        (string Label, string Confidence)[] labels,
        params int[] leaveOut)
    {
        var cache = Cache();
        var items = new List<JsonObject>();

        for (var index = 0; index < labels.Length; index++)
        {
            var bytes = PhotoBytes(index);
            var sha = Sha(bytes);
            var id = $"photo-{index:00}";
            var url = $"https://upload.example.test/{id}.jpg";

            items.Add(Item(id, labels[index].Label, sha, labels[index].Confidence, fileUrl: url));

            if (!leaveOut.Contains(index))
            {
                var path = cache.PathFor(AiTaskKey.PhotoAnalysis, new RemoteEvalFile(id, url, sha));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
        }

        return (WriteSet([.. items]), cache);
    }

    private static RoomPhoto Photo(string id, string label, bool ambiguous = false) =>
        new(id, label, ambiguous ? PhotoEval.Ambiguous : PhotoEval.Clear, "https://s.test/" + id, "https://f.test/" + id + ".jpg",
            "CC0-1.0", "https://l.test", "Author", "Title", new string('a', 64), null);

    private static RoomPhotoResult Result(
        string label,
        string? predicted,
        bool ambiguous = false,
        EvalCallOutcome outcome = EvalCallOutcome.ValidFirstAttempt) =>
        new(
            Photo(Guid.NewGuid().ToString("N"), label, ambiguous),
            Pass: 1,
            new EvalCall(
                predicted is null ? null : JsonNode.Parse(Answer(predicted)),
                new EvalCallRecord(outcome, TimeSpan.FromSeconds(2), 900, 80, UsageReported: true, ImagesSent: 1,
                    outcome == EvalCallOutcome.ValidFirstAttempt ? 1 : 2,
                    outcome == EvalCallOutcome.ProviderFailed ? "503 from provider" : null)));

    // ───────────────────────────── The shipped set ─────────────────────────────

    private static EvalSet Shipped() => EvalSet.LoadShipped(AiTaskKey.PhotoAnalysis, PhotoEval.SetFileName);

    [Fact]
    public void ShippedSet_LoadsAndEveryItemPassesValidation()
    {
        var set = Shipped();

        Assert.Equal(AiTaskKey.PhotoAnalysis, set.TaskKey);
        Assert.Equal(set.Items.Count, PhotoEval.ReadPhotos(set).Count);
    }

    /// <summary>Labelled by AI research agents: it must stay unreviewed until a person records a review.</summary>
    [Fact]
    public void ShippedSet_IsUnreviewed()
    {
        var set = Shipped();

        Assert.False(set.IsReviewed);
        Assert.Null(set.Review.Reviewer);
        Assert.Null(set.Review.ReviewedAt);
    }

    /// <summary>Every label is an answer the active prompt lets the model give, and every room type is represented.</summary>
    [Fact]
    public void ShippedSet_LabelsAreTheActiveSchemasRoomTypes_AndCoverEveryOne()
    {
        var photos = PhotoEval.ReadPhotos(Shipped());
        var roomTypes = PhotoEval.RoomTypesOf(RepositoryPrompts.Value.GetActive(AiTaskKey.PhotoAnalysis).OutputSchema);

        Assert.All(photos, photo => Assert.Contains(photo.Label, roomTypes));
        Assert.Equal(roomTypes.Order(StringComparer.Ordinal), photos.Select(photo => photo.Label).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ShippedSet_LicensesAreAllowed_AndHashesAndIdsAreUnique()
    {
        var photos = PhotoEval.ReadPhotos(Shipped());

        Assert.All(photos, photo => Assert.True(PhotoEval.IsAllowedLicense(photo.License), $"{photo.Id}: {photo.License}"));
        Assert.Equal(photos.Count, photos.Select(photo => photo.Sha256).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(photos.Count, photos.Select(photo => photo.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Ambiguous items exist and say why — they are scored, so a reviewer must be able to judge them.</summary>
    [Fact]
    public void ShippedSet_AmbiguousItemsCarryTheirReason()
    {
        var ambiguous = PhotoEval.ReadPhotos(Shipped()).Where(photo => photo.IsAmbiguous).ToList();

        Assert.NotEmpty(ambiguous);
        Assert.All(ambiguous, photo => Assert.False(string.IsNullOrWhiteSpace(photo.Notes), photo.Id));
    }

    /// <summary>CC BY requires title, author, source and license for every photo; ATTRIBUTION.md must list each one.</summary>
    [Fact]
    public void ShippedSet_EveryItemIsAttributed()
    {
        var set = Shipped();
        var attribution = File.ReadAllText(Path.Combine(Path.GetDirectoryName(set.ManifestPath)!, "ATTRIBUTION.md"));

        Assert.All(PhotoEval.ReadPhotos(set), photo =>
        {
            Assert.Contains($"`{photo.Id}`", attribution, StringComparison.Ordinal);
            Assert.Contains($"<{photo.SourcePage}>", attribution, StringComparison.Ordinal);
            Assert.Contains($"]({photo.LicenseUrl})", attribution, StringComparison.Ordinal);
        });
    }

    // ───────────────────────────── Manifest validation ─────────────────────────────

    [Theory]
    [InlineData("CC-BY-NC-2.0")]
    [InlineData("CC-BY-SA-4.0")]
    [InlineData("All rights reserved")]
    [InlineData("Public-Domain")]
    public void ReadPhotos_RefusesALicenseOutsideTheAllowedSet(string license)
    {
        var set = WriteSet(Item("photo-00", "kitchen", new string('a', 64), license: license));

        var error = Assert.Throws<InvalidDataException>(() => PhotoEval.ReadPhotos(set));
        Assert.Contains("license", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CC0-1.0")]
    [InlineData("CC-BY-2.0")]
    [InlineData("CC-BY-2.5")]
    [InlineData("CC-BY-3.0")]
    [InlineData("CC-BY-4.0")]
    [InlineData("Public-Domain (PD-self)")]
    public void ReadPhotos_AcceptsTheAllowedLicenses(string license)
    {
        var set = WriteSet(Item("photo-00", "kitchen", new string('a', 64), license: license));

        Assert.Single(PhotoEval.ReadPhotos(set));
    }

    [Fact]
    public void ReadPhotos_RefusesTheSameBytesUnderTwoItems()
    {
        var set = WriteSet(
            Item("photo-00", "kitchen", new string('a', 64)),
            Item("photo-01", "bathroom", new string('a', 64)));

        Assert.Throws<InvalidDataException>(() => PhotoEval.ReadPhotos(set));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ReadPhotos_RefusesAMalformedHash(string sha256)
    {
        var set = WriteSet(Item("photo-00", "kitchen", sha256));

        Assert.Throws<InvalidDataException>(() => PhotoEval.ReadPhotos(set));
    }

    [Fact]
    public void ReadPhotos_RefusesAnUnknownLabelConfidence()
    {
        var set = WriteSet(Item("photo-00", "kitchen", new string('a', 64), labelConfidence: "probably"));

        Assert.Throws<InvalidDataException>(() => PhotoEval.ReadPhotos(set));
    }

    [Fact]
    public void ReadPhotos_RefusesAPlainHttpFileUrl()
    {
        var set = WriteSet(Item("photo-00", "kitchen", new string('a', 64), fileUrl: "http://upload.example.test/a.jpg"));

        Assert.Throws<InvalidDataException>(() => PhotoEval.ReadPhotos(set));
    }

    [Fact]
    public void RequireLabelsInSchema_RefusesALabelTheModelCannotGive()
    {
        var schema = RepositoryPrompts.Value.GetActive(AiTaskKey.PhotoAnalysis).OutputSchema;

        var error = Assert.Throws<InvalidDataException>(() =>
            PhotoEval.RequireLabelsInSchema([Photo("p1", "kitchen"), Photo("p2", "dining_room")], schema, "test v1"));

        Assert.Contains("dining_room", error.Message, StringComparison.Ordinal);
    }

    // ───────────────────────────── Accuracy ─────────────────────────────

    /// <summary>
    /// Ambiguous items count against their one label: a model that misses only the ambiguous
    /// photo scores 3 of 4, not 3 of 3.
    /// </summary>
    [Fact]
    public void AcceptedRate_CountsAmbiguousLabels_RatherThanDroppingThem()
    {
        var rate = PhotoEval.AcceptedRate(
        [
            Result("kitchen", "kitchen"),
            Result("bathroom", "bathroom"),
            Result("pool", "pool"),
            Result("primary_bedroom", "bedroom", ambiguous: true),
        ]);

        Assert.Equal(0.75, rate);
    }

    [Fact]
    public void AcceptedRate_AnAmbiguousLabelTheModelMatchesCountsAsAccepted()
    {
        Assert.Equal(1.0, PhotoEval.AcceptedRate([Result("kitchen", "kitchen"), Result("primary_bedroom", "primary_bedroom", ambiguous: true)]));
    }

    /// <summary>A photo with no valid answer is classified by hand in production: that is a change, not an acceptance.</summary>
    [Fact]
    public void AcceptedRate_AnAnswerThatFailedValidationIsNotAccepted()
    {
        var rate = PhotoEval.AcceptedRate(
        [
            Result("kitchen", "kitchen"),
            Result("bathroom", predicted: null, outcome: EvalCallOutcome.InvalidAfterRetry),
        ]);

        Assert.Equal(0.5, rate);
    }

    [Fact]
    public void AcceptedRate_OfNothingIsUndefined()
    {
        Assert.Throws<ArgumentException>(() => PhotoEval.AcceptedRate([]));
    }

    [Fact]
    public void Measure_AProviderFailureWithholdsEveryMetric()
    {
        var measurement = PhotoEval.Measure(
            [Result("kitchen", "kitchen"), Result("bathroom", null, outcome: EvalCallOutcome.ProviderFailed)],
            []);

        Assert.Empty(measurement.Values);
        Assert.Contains(measurement.Unmeasured, reason => reason.Contains(MetricCatalog.RoomClassificationAccepted, StringComparison.Ordinal));
    }

    [Fact]
    public void Measure_ReportsOnlyKeysTheTaskDefines()
    {
        var results = Enumerable.Range(0, MetricCatalog.LatencyListingPhotoCount).Select(_ => Result("kitchen", "kitchen")).ToList();
        var listing = new EvalListingRun([.. results.Select(result => result.Call.Record)], TimeSpan.FromSeconds(60));

        var measurement = PhotoEval.Measure(results, [listing]);
        var catalog = new MetricCatalog(EvalBudget.Default).For(AiTaskKey.PhotoAnalysis).Select(metric => metric.Key);

        Assert.Equal(catalog.Order(StringComparer.Ordinal), measurement.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1.0, measurement.Values[MetricCatalog.RoomClassificationAccepted]);
        Assert.Equal(60, measurement.Values[MetricCatalog.Latency30PhotosP90Seconds]);
    }

    // ───────────────────────────── Listing grouping ─────────────────────────────

    [Theory]
    [InlineData(131, 3)]
    [InlineData(300, 1)]
    [InlineData(299, 2)]
    [InlineData(4, 75)]
    public void PassesFor_ReachesTheMinimumListingCount(int photos, int expectedPasses)
    {
        var passes = PhotoEval.PassesFor(photos, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);

        Assert.Equal(expectedPasses, passes);
        Assert.True(passes * photos / MetricCatalog.LatencyListingPhotoCount >= PhotoEval.MinimumLatencyListings);
    }

    /// <summary>The shipped set's sample: 3 passes, 393 analyses, 13 complete listings.</summary>
    [Fact]
    public void Schedule_OfTheShippedSet_GivesThirteenListings()
    {
        var photos = PhotoEval.ReadPhotos(Shipped());
        var schedule = PhotoEval.Schedule(photos, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);

        Assert.Equal(131, photos.Count);
        Assert.Equal(393, schedule.Count);
        Assert.Equal(13, schedule.Count / MetricCatalog.LatencyListingPhotoCount);
    }

    [Fact]
    public void Schedule_EveryPassContainsEveryPhotoOnce_InItsOwnOrder()
    {
        var photos = Enumerable.Range(0, 7).Select(index => Photo($"p{index}", "kitchen")).ToList();

        var schedule = PhotoEval.Schedule(photos, listingSize: 3, minimumListings: 4);

        Assert.Equal(2, schedule.Max(slot => slot.Pass));
        Assert.Equal(14, schedule.Count);

        var first = schedule.Where(slot => slot.Pass == 1).Select(slot => slot.PhotoIndex).ToList();
        var second = schedule.Where(slot => slot.Pass == 2).Select(slot => slot.PhotoIndex).ToList();

        Assert.Equal(Enumerable.Range(0, 7), first.Order());
        Assert.Equal(Enumerable.Range(0, 7), second.Order());
        Assert.NotEqual(first, second);

        // Passes are consecutive: all of pass 1, then all of pass 2.
        Assert.Equal(schedule.Select(slot => slot.Pass).Order(), schedule.Select(slot => slot.Pass));
    }

    [Fact]
    public void Schedule_IsDeterministic()
    {
        var photos = PhotoEval.ReadPhotos(Shipped());

        var first = PhotoEval.Schedule(photos, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);
        var second = PhotoEval.Schedule(photos, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);

        Assert.Equal(first, second);
    }

    /// <summary>The manifest groups photos by room; a listing must not be thirty kitchens.</summary>
    [Fact]
    public void Schedule_MixesRoomTypesWithinAListing()
    {
        var photos = PhotoEval.ReadPhotos(Shipped());
        var schedule = PhotoEval.Schedule(photos, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);

        var firstListing = schedule.Take(MetricCatalog.LatencyListingPhotoCount).Select(slot => photos[slot.PhotoIndex].Label).Distinct().Count();

        Assert.True(firstListing >= 6, $"The first listing has only {firstListing} room types.");
    }

    // ───────────────────────────── The suite ─────────────────────────────

    [Fact]
    public async Task Run_ScoresEveryPassOfEveryPhoto_AndReportsAllFiveMetrics()
    {
        var (set, cache) = CachedSet(
        [
            ("kitchen", PhotoEval.Clear),
            ("bathroom", PhotoEval.Clear),
            ("garage", PhotoEval.Ambiguous),
            ("primary_bedroom", PhotoEval.Ambiguous),
        ]);

        // Right on everything except the ambiguous primary bedroom, which it calls a bedroom.
        string[] answers = ["kitchen", "bathroom", "garage", "bedroom"];
        var model = new ScriptedVision(index => Answer(answers[index]));
        var report = new StringWriter();

        var run = await new PhotoEval(() => set, cache, report).RunAsync(Invoker(model), default);

        var passes = PhotoEval.PassesFor(4, MetricCatalog.LatencyListingPhotoCount, PhotoEval.MinimumLatencyListings);
        Assert.Equal(passes * 4, model.Seen.Count);
        Assert.All(Enumerable.Range(0, 4), index => Assert.Equal(passes, model.Seen.Count(seen => seen == index)));

        Assert.Equal(0.75, run.Measurements[MetricCatalog.RoomClassificationAccepted]);
        Assert.Equal(1.0, run.Measurements[MetricCatalog.SchemaValidityRate]);
        Assert.Equal(MetricCatalog.LatencyListingPhotoCount, run.Measurements[MetricCatalog.ImagesPerListing]);
        Assert.Equal(MetricCatalog.LatencyListingPhotoCount * 980, run.Measurements[MetricCatalog.TokensPerListing]);
        Assert.True(run.Measurements.ContainsKey(MetricCatalog.Latency30PhotosP90Seconds));
        Assert.Empty(run.Unmeasured);
        Assert.Same(set, Assert.Single(run.EvalSets));

        var text = report.ToString();
        Assert.Contains($"Clear labels {passes * 2}/{passes * 2}, ambiguous labels {passes}/{passes * 2}", text, StringComparison.Ordinal);
        Assert.Contains("photo-03 (primary_bedroom, ambiguous → bedroom", text, StringComparison.Ordinal);
    }

    /// <summary>The set is unreviewed, so even a perfect run is Incomplete (the foundation's gate).</summary>
    [Fact]
    public async Task Run_OnAnUnreviewedSet_IsIncomplete()
    {
        var (set, cache) = CachedSet([("kitchen", PhotoEval.Clear)]);

        var run = await new PhotoEval(() => set, cache, TextWriter.Null)
            .RunAsync(Invoker(new ScriptedVision(_ => Answer("kitchen"))), default);

        var outcome = EvalVerdict.Evaluate(new MetricCatalog(EvalBudget.Default).For(AiTaskKey.PhotoAnalysis), run, AiTaskKey.PhotoAnalysis);

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
    }

    [Fact]
    public async Task Run_WithAPhotoMissingFromTheCache_CallsNoModel_AndIsIncomplete()
    {
        var (set, cache) = CachedSet([("kitchen", PhotoEval.Clear), ("bathroom", PhotoEval.Clear), ("pool", PhotoEval.Clear)], leaveOut: 1);
        var model = new ScriptedVision(_ => Answer("kitchen"));

        var run = await new PhotoEval(() => set, cache, TextWriter.Null).RunAsync(Invoker(model), default);

        Assert.Empty(model.Seen);
        Assert.Empty(run.Measurements);

        var reason = Assert.Single(run.Unmeasured);
        Assert.Contains("1 missing (photo-01)", reason, StringComparison.Ordinal);
        Assert.Contains("--fetch-eval-sets", reason, StringComparison.Ordinal);

        var outcome = EvalVerdict.Evaluate(new MetricCatalog(EvalBudget.Default).For(AiTaskKey.PhotoAnalysis), run, AiTaskKey.PhotoAnalysis);
        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.All(outcome.Results, result => Assert.Equal(MetricOutcome.NotMeasured, result.Outcome));
    }

    [Fact]
    public async Task Run_WithACachedPhotoWhoseBytesChanged_CallsNoModel_AndIsIncomplete()
    {
        var (set, cache) = CachedSet([("kitchen", PhotoEval.Clear), ("bathroom", PhotoEval.Clear)]);
        var photo = PhotoEval.ReadPhotos(set)[0];
        File.WriteAllBytes(cache.PathFor(AiTaskKey.PhotoAnalysis, photo.File), PhotoBytes(9));

        var model = new ScriptedVision(_ => Answer("kitchen"));
        var run = await new PhotoEval(() => set, cache, TextWriter.Null).RunAsync(Invoker(model), default);

        Assert.Empty(model.Seen);
        Assert.Empty(run.Measurements);
        Assert.Contains("do not match the manifest's SHA-256 (photo-00)", Assert.Single(run.Unmeasured), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenTheProviderFails_ReportsNothing()
    {
        var (set, cache) = CachedSet([("kitchen", PhotoEval.Clear)]);
        var model = new ScriptedVision(_ => Answer("kitchen")) { Throw = new HttpRequestException("503 from provider") };

        var run = await new PhotoEval(() => set, cache, TextWriter.Null).RunAsync(Invoker(model), default);

        Assert.Empty(run.Measurements);
        Assert.NotEmpty(run.Unmeasured);
    }

    [Fact]
    public async Task Run_RefusesAModelOfAnotherTask()
    {
        var (set, cache) = CachedSet([("kitchen", PhotoEval.Clear)]);
        var judge = VisionTarget with { TaskKey = AiTaskKey.ComplianceJudge };
        var invoker = new EvalModelInvoker(judge, RepositoryPrompts.Value, new ScriptedVision(_ => "{}"), Normalizer);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhotoEval(() => set, cache, TextWriter.Null).RunAsync(invoker, default));
    }

    // ───────────────────────────── Fetching ─────────────────────────────

    private static RemoteEvalFile Remote(int index, string? sha = null)
    {
        var bytes = PhotoBytes(index);
        return new RemoteEvalFile($"photo-{index:00}", $"https://upload.example.test/photo-{index:00}.jpg", sha ?? Sha(bytes));
    }

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static int IndexOf(HttpRequestMessage request) =>
        int.Parse(request.RequestUri!.AbsolutePath.Split('-')[^1].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Fetch_DownloadsAndVerifies_WithADescriptiveUserAgent()
    {
        var cache = Cache();
        using var source = new FakeSource(request => Ok(PhotoBytes(IndexOf(request))));
        using var http = EvalSetFetcher.CreateClient(source);

        var result = await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)], TextWriter.Null);

        Assert.Equal(2, result.Downloaded.Count);
        Assert.Empty(result.Failed);
        Assert.True(cache.Check(AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)]).Complete);
        Assert.All(source.Requests, request =>
            Assert.Contains("RealtorAI-AiEval", request.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_RefusesBytesThatDoNotMatchTheManifest_AndWritesNothing()
    {
        var cache = Cache();
        var file = Remote(0, sha: Sha(PhotoBytes(5)));
        using var source = new FakeSource(_ => Ok(PhotoBytes(0)));
        using var http = EvalSetFetcher.CreateClient(source);

        var result = await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [file], TextWriter.Null);

        var (failed, problem) = Assert.Single(result.Failed);
        Assert.Equal(file, failed);
        Assert.StartsWith("refused", problem, StringComparison.Ordinal);
        Assert.False(File.Exists(cache.PathFor(AiTaskKey.PhotoAnalysis, file)));
        Assert.Empty(Directory.GetFiles(Path.Combine(cache.Root, AiTaskKey.PhotoAnalysis)));
    }

    [Fact]
    public async Task Fetch_RunAgain_SkipsFilesAlreadyVerified()
    {
        var cache = Cache();
        using var source = new FakeSource(request => Ok(PhotoBytes(IndexOf(request))));
        using var http = EvalSetFetcher.CreateClient(source);

        await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)], TextWriter.Null);
        source.Requests.Clear();

        var again = await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)], TextWriter.Null);

        Assert.Empty(source.Requests);
        Assert.Equal(2, again.AlreadyPresent.Count);
        Assert.Equal(2, again.Verified);
    }

    [Fact]
    public async Task Fetch_ReplacesACachedFileWhoseBytesChanged()
    {
        var cache = Cache();
        var file = Remote(0);
        var path = cache.PathFor(AiTaskKey.PhotoAnalysis, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PhotoBytes(3));

        using var source = new FakeSource(_ => Ok(PhotoBytes(0)));
        using var http = EvalSetFetcher.CreateClient(source);

        var result = await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [file], TextWriter.Null);

        Assert.Single(result.Downloaded);
        Assert.Equal(CachedFileState.Verified, cache.TryRead(AiTaskKey.PhotoAnalysis, file, out _));
    }

    [Fact]
    public async Task Fetch_ReportsAFileTheSourceNoLongerHas()
    {
        var cache = Cache();
        using var source = new FakeSource(request => IndexOf(request) == 1
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Ok(PhotoBytes(IndexOf(request))));
        using var http = EvalSetFetcher.CreateClient(source);

        var result = await EvalSetFetcher.FetchAsync(http, cache, AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)], TextWriter.Null);

        Assert.Single(result.Downloaded);
        var (failed, problem) = Assert.Single(result.Failed);
        Assert.Equal("photo-01", failed.Id);
        Assert.Contains("404", problem, StringComparison.Ordinal);

        var check = cache.Check(AiTaskKey.PhotoAnalysis, [Remote(0), Remote(1)]);
        Assert.False(check.Complete);
        Assert.Equal("photo-01", Assert.Single(check.Missing).Id);
    }

    [Fact]
    public async Task Fetch_WaitsOutThrottling_AndThenSucceeds()
    {
        var cache = Cache();
        var calls = 0;
        using var source = new FakeSource(_ =>
        {
            calls++;

            if (calls == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return throttled;
            }

            return Ok(PhotoBytes(0));
        });
        using var http = EvalSetFetcher.CreateClient(source);
        var waits = new List<TimeSpan>();

        var result = await EvalSetFetcher.FetchAsync(
            http,
            cache,
            AiTaskKey.PhotoAnalysis,
            [Remote(0)],
            TextWriter.Null,
            (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            });

        Assert.Single(result.Downloaded);
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }
}
