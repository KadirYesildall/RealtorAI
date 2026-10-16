using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using Modules.Media;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T070 — what an agent uploads is what is stored, byte for byte, and stays that way
/// (FR-001, FR-002, VIII.4.c; Story 1 independent test).
///
/// <para>
/// Crosses three systems: the API receives the upload, Supabase Storage holds the bytes, and
/// Postgres records the checksum. Each is checked against the others rather than against
/// itself — the bytes read back from storage are hashed here, independently of the checksum the
/// API recorded, so a bug that recorded the wrong hash and stored the wrong bytes consistently
/// would still fail.
/// </para>
/// <para>
/// Requires <c>SUPABASE_SECRET_KEY</c> (quickstart.md §1); it is never hard-coded.
/// </para>
/// </summary>
public sealed class OriginalImmutabilityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public OriginalImmutabilityTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private async Task<(Guid Listing, JsonArray Results, (string Name, byte[] Content, string Type)[] Files)> UploadMixedAsync()
    {
        var agent = await TestAgents.CreateAsync("immutability");
        var listing = IdOf(await CreateAsync(factory, agent));

        (string, byte[], string)[] files =
        [
            ("phone.jpg", TestImages.JpegWithGps(), "image/jpeg"),
            ("drone.png", TestImages.Png(1800, 1200), "image/png"),
            ("export.webp", TestImages.WebP(2000, 1500), "image/webp"),
        ];

        using var client = factory.CreateClientFor(agent);
        using var form = Files(files);
        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        var results = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.All(results, result => Assert.True(result!["accepted"]!.GetValue<bool>()));

        return (listing, results, files);
    }

    private static async Task<string> StorageKeyAsync(string column, Guid photoId) =>
        (await TestDatabase.ScalarAsync<string>($"select {column} from public.photos where id = $1", photoId))!;

    [Fact]
    public async Task Originals_ReadBackFromStorage_AreByteIdenticalToTheUpload()
    {
        var (_, results, files) = await UploadMixedAsync();
        var storage = factory.Services.GetRequiredService<IPhotoStorage>();

        for (var index = 0; index < files.Length; index++)
        {
            var photoId = results[index]!["photoId"]!.GetValue<Guid>();
            var stored = await storage.GetOriginalAsync(await StorageKeyAsync("original_storage_key", photoId), CancellationToken.None);

            Assert.Equal(files[index].Content, stored);
            Assert.Equal(Sha256(files[index].Content), Sha256(stored));
            Assert.Equal(
                Sha256(stored),
                await TestDatabase.ScalarAsync<string>("select checksum_sha256 from public.photos where id = $1", photoId));
        }
    }

    /// <summary>
    /// The copy that leaves the boundary is a different file — resized, re-encoded, and without
    /// the GPS block that is the seller's address in coordinates — while the original keeps it.
    /// </summary>
    [Fact]
    public async Task NormalizedCopies_AreDerivedAndStripped_WhileTheOriginalKeepsItsMetadata()
    {
        var (_, results, files) = await UploadMixedAsync();
        var storage = factory.Services.GetRequiredService<IPhotoStorage>();
        var photoId = results[0]!["photoId"]!.GetValue<Guid>();

        var original = await storage.GetOriginalAsync(await StorageKeyAsync("original_storage_key", photoId), CancellationToken.None);
        var normalized = await storage.GetNormalizedAsync(await StorageKeyAsync("normalized_storage_key", photoId), CancellationToken.None);

        Assert.NotEqual(files[0].Content, normalized);

        using var originalImage = new MagickImage(original);
        using var normalizedImage = new MagickImage(normalized);

        Assert.NotNull(originalImage.GetExifProfile()?.GetValue(ExifTag.GPSLatitude));
        Assert.Null(normalizedImage.GetExifProfile());
        Assert.Equal(1280u, Math.Max(normalizedImage.Width, normalizedImage.Height));
        Assert.Equal(MagickFormat.Jpeg, normalizedImage.Format);
    }

    /// <summary>
    /// Write-once holds after the upload too: the original's key cannot be written again, even
    /// with the privileged credential the API itself holds, and the bytes survive the attempt.
    /// </summary>
    [Fact]
    public async Task Originals_CannotBeOverwrittenAfterUpload()
    {
        var (_, results, files) = await UploadMixedAsync();
        var storage = factory.Services.GetRequiredService<IPhotoStorage>();
        var photoId = results[1]!["photoId"]!.GetValue<Guid>();
        var key = await StorageKeyAsync("original_storage_key", photoId);

        await Assert.ThrowsAsync<OriginalAlreadyExistsException>(() =>
            storage.PutOriginalAsync(key, TestImages.Png(), "image/png", CancellationToken.None));

        Assert.Equal(files[1].Content, await storage.GetOriginalAsync(key, CancellationToken.None));
    }

    /// <summary>
    /// Story 1's independent test at its stated size: thirty mixed-format photos, every original
    /// verified by checksum, the draft intact on return.
    /// </summary>
    [Fact]
    public async Task ThirtyMixedPhotos_AllVerifyByChecksumAndTheDraftResumesIntact()
    {
        var agent = await TestAgents.CreateAsync("immutability-thirty");
        var listing = IdOf(await CreateAsync(factory, agent));

        var files = Enumerable.Range(0, 30)
            .Select(index => (index % 3) switch
            {
                0 => ($"photo-{index:00}.jpg", TestImages.Jpeg(1200, 900), "image/jpeg"),
                1 => ($"photo-{index:00}.png", TestImages.Png(900, 700), "image/png"),
                _ => ($"photo-{index:00}.webp", TestImages.WebP(1000, 750), "image/webp"),
            })
            .ToArray();

        using var client = factory.CreateClientFor(agent);

        // In batches of ten, as the wizard uploads them.
        foreach (var batch in files.Chunk(10))
        {
            using var form = Files(batch);
            Assert.Equal(HttpStatusCode.MultiStatus, (await client.PostAsync(Photos(listing), form)).StatusCode);
        }

        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(listing)))!;
        var photos = detail["photos"]!.AsArray();

        Assert.Equal(30, photos.Count);
        Assert.Equal(Enumerable.Range(0, 30), photos.Select(photo => photo!["orderIndex"]!.GetValue<int>()));
        Assert.Equal(
            files.Select(file => Sha256(file.Item2)),
            photos.Select(photo => photo!["checksumSha256"]!.GetValue<string>()));
        Assert.Equal("1420 Crestline Drive", detail["addressLine1"]!.GetValue<string>());
    }
}
