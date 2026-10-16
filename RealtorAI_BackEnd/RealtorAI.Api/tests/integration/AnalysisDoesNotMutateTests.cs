using System.Security.Cryptography;
using Libs.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Modules.Media;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T091 — a full analysis run changes no original (FR-001, VIII.4.c, VIII.6).
///
/// <para>
/// Analysis reads photos in three places — the quality measurement, the embedding, the vision
/// model — and writes results in several more. This proves, after every step has run, that each
/// original is byte-identical to what was uploaded and still matches its recorded checksum, and
/// that nothing leaving the API was an original: the sidecar and the model were sent only the
/// normalized copies.
/// </para>
/// </summary>
public sealed class AnalysisDoesNotMutateTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public AnalysisDoesNotMutateTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        factory.Registry.Enable(AiEnabledApiFactory.Entry(AiTaskKey.PhotoEmbedding, "test/embedding-model", """{"dimension": 768}"""));
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [Fact]
    public async Task AFullRun_LeavesEveryOriginalByteIdentical_AndSendsOnlyNormalizedCopies()
    {
        var agent = await TestAgents.CreateAsync("does-not-mutate");
        var listing = IdOf(await CreateAsync(factory, agent));
        var uploaded = new[] { TestImages.JpegWithGps(), TestImages.Png(1800, 1200), TestImages.WebP(1600, 1200) };
        var photos = await UploadAsync(factory, agent, listing, uploaded);

        var status = await RunToCompletionAsync(factory, agent, listing);
        Assert.Equal("done", StepState(status, "photo_analysis"));
        Assert.Equal("done", StepState(status, "embeddings"));

        var storage = factory.Services.GetRequiredService<IPhotoStorage>();
        var originals = new List<byte[]>();

        for (var index = 0; index < photos.Count; index++)
        {
            var key = (await TestDatabase.ScalarAsync<string>("select original_storage_key from public.photos where id = $1", photos[index]))!;
            var stored = await storage.GetOriginalAsync(key, CancellationToken.None);
            originals.Add(stored);

            Assert.Equal(uploaded[index], stored);
            Assert.Equal(
                Sha256(uploaded[index]),
                await TestDatabase.ScalarAsync<string>("select checksum_sha256 from public.photos where id = $1", photos[index]));
        }

        var originalHashes = originals.Select(Sha256).ToHashSet();

        Assert.DoesNotContain(factory.Sidecar.Received, image => originalHashes.Contains(Sha256(image)));
        Assert.DoesNotContain(
            factory.Models.Calls.SelectMany(messages => messages).SelectMany(message => message.Contents).OfType<DataContent>(),
            image => originalHashes.Contains(Sha256(image.Data.ToArray())));
    }
}
