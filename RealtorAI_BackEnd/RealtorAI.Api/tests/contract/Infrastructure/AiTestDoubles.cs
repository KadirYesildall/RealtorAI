using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ImageMagick;
using Libs.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pipeline;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>
/// The real host with the AI layer's three outside dependencies replaced: which model is enabled,
/// what that model answers, and the vision sidecar.
///
/// <para>
/// <b>Why not enable a model in the database.</b> Every registry entry is disabled until its
/// license is recorded and its evaluation passes (VIII.4), and the suites share one database. A
/// test that enabled <c>photo.analysis</c> there would change the answer for every other test
/// running at the same moment — including the one proving that with no enabled model the
/// pipeline answers 503 (SC-010). Substituting the registry keeps each host's view to itself.
/// </para>
/// <para>
/// <b>Why a queue of its own.</b> Pipeline work runs as Hangfire jobs in a shared database, and
/// every host — other test processes, a running development API — has workers. A job on the
/// shared queue could be taken by a host that has the real registry and the real sidecar, and
/// would then run against a different configuration than the one under test. Only this host
/// serves this queue.
/// </para>
/// <para>
/// Everything else is real: the endpoints, the orchestrator and its steps, <see cref="AiService"/>
/// (prompt, schema validation, budget, usage recording, failover), storage, and the database.
/// </para>
/// </summary>
public class AiEnabledApiFactory : ApiFactory
{
    internal string Queue { get; } = "test_" + Guid.NewGuid().ToString("N");

    internal FakeModelRegistry Registry { get; } = new();

    internal FakeChatClientFactory Models { get; } = new();

    internal FakeVisionSidecar Sidecar { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Pipeline:Queue", Queue);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModelRegistry>();
            services.AddScoped<IModelRegistry>(_ => Registry);

            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(Models);

            services.RemoveAll<IVisionSidecar>();
            services.AddSingleton<IVisionSidecar>(Sidecar);
        });
    }

    /// <summary>A test entry for <paramref name="taskKey"/>, as if licensed, evaluated and enabled.</summary>
    internal static ModelRegistryEntry Entry(string taskKey, string modelId, string parameters = "{}") =>
        new(Guid.NewGuid(), taskKey, "test-provider", modelId, Endpoint: null, parameters, FallbackRegistryId: null,
            "Apache-2.0", ModelEvalStatus.Passed, Enabled: true);
}

/// <summary>A registry holding exactly the entries a test enables.</summary>
internal sealed class FakeModelRegistry : IModelRegistry
{
    private readonly ConcurrentDictionary<string, ModelRegistryEntry> entries = new(StringComparer.Ordinal);

    internal FakeModelRegistry()
    {
        Enable(AiEnabledApiFactory.Entry(AiTaskKey.PhotoAnalysis, "test/vision-model"));
    }

    internal void Enable(ModelRegistryEntry entry) => entries[entry.TaskKey] = entry;

    internal void Disable(string taskKey) => entries.TryRemove(taskKey, out _);

    public Task<ModelRegistryEntry> ResolveAsync(string taskKey, CancellationToken cancellationToken) =>
        entries.TryGetValue(taskKey, out var entry)
            ? Task.FromResult(entry)
            : throw new ModelUnavailableException(taskKey, "no entry is enabled for this task in the test registry.");

    public async Task<IReadOnlyList<ModelRegistryEntry>> ResolveChainAsync(string taskKey, CancellationToken cancellationToken) =>
        [await ResolveAsync(taskKey, cancellationToken)];
}

/// <summary>
/// A vision model that answers with a valid <c>photo.analysis</c> document, or fails for the
/// photos a test names. Records every call so a test can check what was sent.
/// </summary>
internal sealed class FakeChatClientFactory : IChatClientFactory
{
    internal ConcurrentBag<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    /// <summary>Photo ids whose analysis call fails at the provider.</summary>
    internal ConcurrentDictionary<Guid, bool> FailingPhotos { get; } = new();

    /// <summary>
    /// The document returned for a successful call, given the call's user input (which names the
    /// photo). Replace to vary it per photo.
    /// </summary>
    internal Func<string, JsonObject> Answer { get; set; } = _ => DefaultAnswer();

    internal static JsonObject DefaultAnswer(string roomType = "living_room") => new()
    {
        ["roomType"] = roomType,
        ["confidence"] = 0.82,
        ["featureTags"] = new JsonArray("Fireplace", "Hardwood floors"),
        ["contentFlags"] = new JsonArray(),
        ["altText"] = "Living room with a fireplace and hardwood floors",
        ["coverRecommended"] = false,
        ["recommendationReason"] = "An interior room; an exterior view usually leads.",
    };

    public IChatClient Create(ModelRegistryEntry entry) => new Client(this);

    private sealed class Client(FakeChatClientFactory owner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            owner.Calls.Add(list);

            var input = string.Join("\n", list.Where(message => message.Role == ChatRole.User).Select(message => message.Text));

            if (owner.FailingPhotos.Keys.Any(photoId => input.Contains(photoId.ToString(), StringComparison.Ordinal)))
            {
                throw new HttpRequestException("The test provider is failing for this photo.");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Answer(input).ToJsonString()))
            {
                Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 120, TotalTokenCount = 1020 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A sidecar with real measurements of what it is sent — dimensions by decoding — and
/// embeddings derived from the image's mean colour, so two images of the same colour embed as
/// near-duplicates and different colours do not. That is enough to exercise the API's
/// thresholds and the near-duplicate query without the embedding model, which is not loaded.
/// </summary>
internal sealed class FakeVisionSidecar : IVisionSidecar
{
    internal const int Dimension = 768;

    /// <summary>Set to make every call fail as an unreachable sidecar.</summary>
    internal bool Unavailable { get; set; }

    /// <summary>Blur score reported for every photo; below the API's threshold flags it blurry.</summary>
    internal double BlurScore { get; set; } = 250;

    /// <summary>Every image the sidecar was sent, to prove only normalized copies leave the API.</summary>
    internal ConcurrentBag<byte[]> Received { get; } = [];

    public Task<QualityMeasurements> MeasureQualityAsync(
        ReadOnlyMemory<byte> normalizedImage,
        int minWidth,
        int minHeight,
        CancellationToken cancellationToken)
    {
        Received.Add(normalizedImage.ToArray());

        if (Unavailable)
        {
            throw new VisionSidecarUnavailableException("The test sidecar is down.");
        }

        using var image = new MagickImage(normalizedImage.Span);

        return Task.FromResult(new QualityMeasurements(
            (int)image.Width,
            (int)image.Height,
            BlurScore,
            MeanLuminance: 0.5,
            ClippedHighlightRatio: 0.01,
            ClippedShadowRatio: 0.01,
            HorizonTiltDegrees: null));
    }

    public Task<ImageEmbedding> EmbedAsync(ReadOnlyMemory<byte> normalizedImage, string modelId, CancellationToken cancellationToken)
    {
        Received.Add(normalizedImage.ToArray());

        if (Unavailable)
        {
            throw new VisionSidecarUnavailableException("The test sidecar is down.");
        }

        // The mean colour, repeated across the vector: cosine similarity between two embeddings
        // is then the cosine between the two colours, near 1 for the same colour.
        using var image = new MagickImage(normalizedImage.Span);
        var statistics = image.Statistics();
        var channels = new[] { PixelChannel.Red, PixelChannel.Green, PixelChannel.Blue }
            .Select(channel => statistics.GetChannel(channel)!.Mean / Quantum.Max)
            .ToArray();

        var vector = new float[Dimension];
        for (var index = 0; index < Dimension; index++)
        {
            vector[index] = (float)channels[index % 3] + 0.001f;
        }

        return Task.FromResult(new ImageEmbedding(modelId, Dimension, vector));
    }
}
