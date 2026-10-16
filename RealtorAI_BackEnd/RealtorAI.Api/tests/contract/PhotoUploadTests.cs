using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// The host with a small per-file limit, so the oversize rejection can be exercised without
/// generating a 50 MiB image.
/// </summary>
public sealed class SmallUploadLimitApiFactory : ApiFactory
{
    internal const int LimitBytes = 150_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:MaxUploadBytes", LimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// T068 — <c>POST /api/listings/{id}/photos</c> (FR-001, FR-002, FR-003; Story 1 scenarios 1–3).
///
/// <para>
/// <b>What is at stake.</b> The upload is judged per file. One unreadable or oversized photo in
/// a batch of thirty must be named with its reason while the other twenty-nine still land, and
/// a batch that would take the listing past 100 photos must be refused whole — without
/// discarding a single photo already uploaded.
/// </para>
/// <para>
/// Requires the Supabase secret key in <c>SUPABASE_SECRET_KEY</c>, exactly as the running API
/// does (quickstart.md §1). It is never hard-coded here.
/// </para>
/// </summary>
public sealed class PhotoUploadTests : IClassFixture<ApiFactory>, IClassFixture<SmallUploadLimitApiFactory>
{
    private readonly ApiFactory factory;
    private readonly SmallUploadLimitApiFactory smallLimitFactory;

    public PhotoUploadTests(ApiFactory factory, SmallUploadLimitApiFactory smallLimitFactory)
    {
        this.factory = factory;
        this.smallLimitFactory = smallLimitFactory;
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<JsonArray> UploadAsync(ApiFactory host, Guid caller, Guid listingId, params (string, byte[], string)[] files)
    {
        using var client = host.CreateClientFor(caller);
        using var form = Files(files);

        var response = await client.PostAsync(Photos(listingId), form);

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonArray>())!;
    }

    private static Task<long> PhotoCountAsync(Guid listingId) =>
        TestDatabase.ScalarAsync<long>("select count(*) from public.photos where listing_id = $1", listingId);

    [Fact]
    public async Task Upload_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();
        using var form = Files(("a.jpg", TestImages.Jpeg(), "image/jpeg"));

        var response = await client.PostAsync(Photos(Guid.NewGuid()), form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Scenario 1: every accepted format lands, original byte-for-byte, with a normalized copy.</summary>
    [Fact]
    public async Task Upload_AcceptedFormats_StoresEachWithItsChecksumAndANormalizedCopy()
    {
        var agent = await TestAgents.CreateAsync("upload");
        var listing = IdOf(await CreateAsync(factory, agent));
        var jpeg = TestImages.Jpeg(1600, 1200);
        var png = TestImages.Png(1200, 900);
        var webp = TestImages.WebP(1400, 1000);

        var results = await UploadAsync(
            factory,
            agent,
            listing,
            ("kitchen.jpg", jpeg, "image/jpeg"),
            ("living.png", png, "image/png"),
            ("yard.webp", webp, "image/webp"));

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result!["accepted"]!.GetValue<bool>()));
        Assert.Equal(["kitchen.jpg", "living.png", "yard.webp"], results.Select(result => result!["fileName"]!.GetValue<string>()));

        var expected = new[] { (jpeg, "image/jpeg", 1600, 1200), (png, "image/png", 1200, 900), (webp, "image/webp", 1400, 1000) };

        for (var index = 0; index < expected.Length; index++)
        {
            var (content, mimeType, width, height) = expected[index];
            var photoId = results[index]!["photoId"]!.GetValue<Guid>();

            Assert.Equal(Sha256(content), await TestDatabase.ScalarAsync<string>("select checksum_sha256 from public.photos where id = $1", photoId));
            Assert.Equal(mimeType, await TestDatabase.ScalarAsync<string>("select mime_type from public.photos where id = $1", photoId));
            Assert.Equal((long)content.Length, await TestDatabase.ScalarAsync<long>("select bytes from public.photos where id = $1", photoId));
            Assert.Equal(width, await TestDatabase.ScalarAsync<int>("select width from public.photos where id = $1", photoId));
            Assert.Equal(height, await TestDatabase.ScalarAsync<int>("select height from public.photos where id = $1", photoId));
            Assert.Equal(index, await TestDatabase.ScalarAsync<int>("select order_index from public.photos where id = $1", photoId));
            Assert.True(await TestDatabase.ScalarAsync<bool>(
                "select normalized_storage_key is not null and is_cover = false and altered = false from public.photos where id = $1",
                photoId));
        }
    }

    /// <summary>
    /// Scenario 2: the bad file is named with a reason, and the good files around it still
    /// upload. The client's declared type is not trusted — a text file sent as image/jpeg is
    /// still a text file.
    /// </summary>
    [Fact]
    public async Task Upload_WithAnUnsupportedFile_RejectsOnlyThatFileWithAReason()
    {
        var agent = await TestAgents.CreateAsync("upload-mixed");
        var listing = IdOf(await CreateAsync(factory, agent));

        var results = await UploadAsync(
            factory,
            agent,
            listing,
            ("front.jpg", TestImages.Jpeg(), "image/jpeg"),
            ("notes.jpg", Encoding.UTF8.GetBytes("not a photo at all"), "image/jpeg"),
            ("floorplan.gif", Gif(), "image/gif"),
            ("back.png", TestImages.Png(), "image/png"));

        Assert.Equal(4, results.Count);
        Assert.True(results[0]!["accepted"]!.GetValue<bool>());
        Assert.True(results[3]!["accepted"]!.GetValue<bool>());

        foreach (var rejected in new[] { results[1]!, results[2]! })
        {
            Assert.False(rejected["accepted"]!.GetValue<bool>());
            Assert.Null(rejected["photoId"]);
            Assert.False(string.IsNullOrWhiteSpace(rejected["rejectionReason"]!.GetValue<string>()));
        }

        Assert.Equal("notes.jpg", results[1]!["fileName"]!.GetValue<string>());
        Assert.Equal("floorplan.gif", results[2]!["fileName"]!.GetValue<string>());
        Assert.Equal("unsupported_format", results[1]!["rejectionCode"]!.GetValue<string>());
        Assert.Equal("unsupported_format", results[2]!["rejectionCode"]!.GetValue<string>());
        Assert.Null(results[0]!["rejectionCode"]);
        Assert.Equal(2L, await PhotoCountAsync(listing));
    }

    /// <summary>Scenario 2's size limit: rejected by name, the rest unaffected.</summary>
    [Fact]
    public async Task Upload_AFileOverTheSizeLimit_IsRejectedByName()
    {
        var agent = await TestAgents.CreateAsync("upload-oversize");
        var listing = IdOf(await CreateAsync(smallLimitFactory, agent));
        // Over the per-file limit, but within the request bound (ten files' worth), so the
        // request is read and the file judged on its own.
        var large = TestImages.Png(600, 400);
        var small = TestImages.Jpeg(400, 300);

        Assert.True(large.Length > SmallUploadLimitApiFactory.LimitBytes);
        Assert.True(large.Length < SmallUploadLimitApiFactory.LimitBytes * 10);
        Assert.True(small.Length < SmallUploadLimitApiFactory.LimitBytes);

        var results = await UploadAsync(
            smallLimitFactory,
            agent,
            listing,
            ("huge.png", large, "image/png"),
            ("small.jpg", small, "image/jpeg"));

        Assert.False(results[0]!["accepted"]!.GetValue<bool>());
        Assert.Contains("huge.png", results[0]!["rejectionReason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("too_large", results[0]!["rejectionCode"]!.GetValue<string>());
        Assert.True(results[1]!["accepted"]!.GetValue<bool>());
        Assert.Equal(1L, await PhotoCountAsync(listing));
    }

    /// <summary>An exact duplicate is rejected with a pointer to the photo it duplicates.</summary>
    [Fact]
    public async Task Upload_AnExactDuplicate_IsRejectedReferencingTheExistingPhoto()
    {
        var agent = await TestAgents.CreateAsync("upload-duplicate");
        var listing = IdOf(await CreateAsync(factory, agent));
        var photo = TestImages.Jpeg();

        var first = await UploadAsync(factory, agent, listing, ("one.jpg", photo, "image/jpeg"));
        var second = await UploadAsync(factory, agent, listing, ("one-again.jpg", photo, "image/jpeg"));

        Assert.False(second[0]!["accepted"]!.GetValue<bool>());
        Assert.Equal(first[0]!["photoId"]!.GetValue<Guid>(), second[0]!["duplicateOfPhotoId"]!.GetValue<Guid>());
        Assert.Equal("duplicate", second[0]!["rejectionCode"]!.GetValue<string>());
        Assert.Equal(1L, await PhotoCountAsync(listing));
    }

    [Fact]
    public async Task Upload_TheSamePhotoTwiceInOneBatch_KeepsTheFirst()
    {
        var agent = await TestAgents.CreateAsync("upload-batch-duplicate");
        var listing = IdOf(await CreateAsync(factory, agent));
        var photo = TestImages.Jpeg();

        var results = await UploadAsync(
            factory,
            agent,
            listing,
            ("a.jpg", photo, "image/jpeg"),
            ("b.jpg", photo, "image/jpeg"));

        Assert.True(results[0]!["accepted"]!.GetValue<bool>());
        Assert.False(results[1]!["accepted"]!.GetValue<bool>());
        Assert.Equal(results[0]!["photoId"]!.GetValue<Guid>(), results[1]!["duplicateOfPhotoId"]!.GetValue<Guid>());
    }

    /// <summary>Later uploads are ordered after what is already there.</summary>
    [Fact]
    public async Task Upload_InTwoBatches_ContinuesTheOrder()
    {
        var agent = await TestAgents.CreateAsync("upload-order");
        var listing = IdOf(await CreateAsync(factory, agent));

        await UploadAsync(factory, agent, listing, ("1.jpg", TestImages.Jpeg(), "image/jpeg"), ("2.jpg", TestImages.Jpeg(), "image/jpeg"));
        var later = await UploadAsync(factory, agent, listing, ("3.jpg", TestImages.Jpeg(), "image/jpeg"));

        Assert.Equal(2, await TestDatabase.ScalarAsync<int>(
            "select order_index from public.photos where id = $1",
            later[0]!["photoId"]!.GetValue<Guid>()));
    }

    /// <summary>
    /// Scenario 3: a batch that would exceed 100 is refused whole, with nothing already uploaded
    /// discarded — and the photo that fits exactly is still accepted afterwards.
    /// </summary>
    [Fact]
    public async Task Upload_PastOneHundredPhotos_Is409AndKeepsEveryExistingPhoto()
    {
        var agent = await TestAgents.CreateAsync("upload-bound");
        var listing = IdOf(await CreateAsync(factory, agent));

        // 99 photo rows arranged directly: the bound is about the count, and uploading 99 real
        // images would make this one test slower than the rest of the suite together.
        await TestDatabase.ExecuteAsync(
            """
            insert into public.photos (listing_id, original_storage_key, checksum_sha256, mime_type, order_index)
            select $1, $1::text || '/' || gen_random_uuid()::text || '.jpg', md5(random()::text) || i, 'image/jpeg', i
            from generate_series(0, 98) as i
            """,
            listing);

        using (var client = factory.CreateClientFor(agent))
        using (var form = Files(("100.jpg", TestImages.Jpeg(), "image/jpeg"), ("101.jpg", TestImages.Jpeg(), "image/jpeg")))
        {
            var refused = await client.PostAsync(Photos(listing), form);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = (await refused.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Contains("100", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal(99L, await PhotoCountAsync(listing));

        var fits = await UploadAsync(factory, agent, listing, ("100.jpg", TestImages.Jpeg(), "image/jpeg"));

        Assert.True(fits[0]!["accepted"]!.GetValue<bool>());
        Assert.Equal(100L, await PhotoCountAsync(listing));
    }

    /// <summary>Scenario 3's lower bound: a request carrying no photos is refused rather than answered with an empty list.</summary>
    [Fact]
    public async Task Upload_WithNoFiles_Is422()
    {
        var agent = await TestAgents.CreateAsync("upload-empty");
        var listing = IdOf(await CreateAsync(factory, agent));
        using var client = factory.CreateClientFor(agent);
        using var form = new MultipartFormDataContent { { new StringContent("x"), "note" } };

        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ByAnUnrelatedAgent_Is404()
    {
        var owner = await TestAgents.CreateAsync("upload-owner");
        var stranger = await TestAgents.CreateAsync("upload-stranger");
        var listing = IdOf(await CreateAsync(factory, owner));
        using var client = factory.CreateClientFor(stranger);
        using var form = Files(("a.jpg", TestImages.Jpeg(), "image/jpeg"));

        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, await PhotoCountAsync(listing));
    }

    [Fact]
    public async Task Upload_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("upload-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("upload-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        using var client = factory.CreateClientFor(admin);
        using var form = Files(("a.jpg", TestImages.Jpeg(), "image/jpeg"));

        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await PhotoCountAsync(listing));
    }

    [Fact]
    public async Task Upload_ToAnArchivedListing_Is403()
    {
        var agent = await TestAgents.CreateAsync("upload-archived");
        var listing = IdOf(await CreateAsync(factory, agent));
        using var client = factory.CreateClientFor(agent);
        await client.DeleteAsync(Listing(listing));
        using var form = Files(("a.jpg", TestImages.Jpeg(), "image/jpeg"));

        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The uploaded photo appears on the listing with a short-lived URL for its normalized copy.</summary>
    [Fact]
    public async Task Upload_ThenRead_ShowsThePhotoWithASignedNormalizedUrl()
    {
        var agent = await TestAgents.CreateAsync("upload-read");
        var listing = IdOf(await CreateAsync(factory, agent));
        var content = TestImages.Jpeg(2400, 1800);
        var uploaded = await UploadAsync(factory, agent, listing, ("wide.jpg", content, "image/jpeg"));

        using var client = factory.CreateClientFor(agent);
        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(listing)))!;
        var photo = detail["photos"]!.AsArray().Single()!.AsObject();

        Assert.Equal(uploaded[0]!["photoId"]!.GetValue<Guid>(), photo["id"]!.GetValue<Guid>());
        Assert.Equal(Sha256(content), photo["checksumSha256"]!.GetValue<string>());
        Assert.Equal(0, photo["orderIndex"]!.GetValue<int>());
        Assert.False(photo["altered"]!.GetValue<bool>());
        Assert.Null(photo["analysis"]);

        var url = new Uri(photo["normalizedUrl"]!.GetValue<string>());
        Assert.Contains("token=", url.Query, StringComparison.Ordinal);

        using var storage = new HttpClient();
        var normalized = await storage.GetByteArrayAsync(url);
        Assert.NotEqual(content, normalized);
        Assert.Equal([0xFF, 0xD8], normalized[..2]);
    }

    /// <summary>A body past the request bound is answered 413 with the bound stated, not a 500.</summary>
    [Fact]
    public async Task Upload_ABodyPastTheRequestBound_Is413()
    {
        var agent = await TestAgents.CreateAsync("upload-body-bound");
        var listing = IdOf(await CreateAsync(smallLimitFactory, agent));
        using var client = smallLimitFactory.CreateClientFor(agent);
        using var form = Files(("huge.bin", new byte[(SmallUploadLimitApiFactory.LimitBytes * 10) + (2 * 1024 * 1024)], "image/jpeg"));

        var response = await client.PostAsync(Photos(listing), form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await PhotoCountAsync(listing));
    }

    /// <summary>GIF is a real image format, just not one FR-003 accepts.</summary>
    private static byte[] Gif()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 64, 64);
        image.Format = ImageMagick.MagickFormat.Gif;

        return image.ToByteArray();
    }
}
