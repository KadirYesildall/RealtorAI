using System.Net;
using System.Security.Cryptography;
using Modules.Media;
using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T057 — the storage wrapper against the running Supabase Storage, and the write-once
/// guarantee for originals that 0014_originals_write_once.sql adds (FR-001, FR-065, R30).
///
/// <para>
/// <b>Why the guarantee is tested around the wrapper, not through it.</b> The wrapper has no
/// overwrite or delete for originals, so testing through it would prove only that a method
/// does not exist. The guarantee that matters is that the storage layer refuses those
/// operations from the very credential the API holds, so these tests call the Storage REST API
/// and the database directly with that credential, and then check the bytes are still there.
/// </para>
/// <para>
/// <b>Local residue, by design.</b> Originals written here cannot be deleted — that is what is
/// being tested — so each run leaves a few small objects under random listing ids in the local
/// stack. <c>supabase db reset</c> clears them.
/// </para>
/// </summary>
[Collection(TableLockingTests.Name)]
public sealed class StorageServiceTests : IDisposable
{
    private const string StorageApi = "http://127.0.0.1:54321/storage/v1";
    private const string Originals = "listing-photos-original";
    private const string Normalized = "listing-photos-normalized";

    /// <summary>
    /// The Supabase CLI's fixed local-development secret key, printed by <c>supabase status</c>;
    /// it is the same on every machine and grants nothing outside the local stack. Overridable
    /// for a stack started with different keys.
    /// </summary>
    private static readonly string ServiceKey =
        Environment.GetEnvironmentVariable("REALTORAI_TEST_SUPABASE_SECRET_KEY")
        ?? "sb_secret_N7UND0UgjKTVK-Uodkm0Hg_xSvEMPvz";

    private readonly HttpClient http = new();
    private readonly StorageService storage;

    public StorageServiceTests()
    {
        storage = new StorageService(
            http,
            new StorageOptions(new Uri(StorageApi), ServiceKey, Originals, Normalized, TimeSpan.FromSeconds(60)));
    }

    public void Dispose() => http.Dispose();

    private static string NewKey() => PhotoStorageKeys.Original(Guid.NewGuid(), Guid.NewGuid(), "jpg");

    private static byte[] Bytes(string text) => [0xFF, 0xD8, 0xFF, 0xE0, .. System.Text.Encoding.UTF8.GetBytes(text)];

    private async Task<HttpStatusCode> RawStatusAsync(HttpMethod method, string path, HttpContent? content = null, bool upsert = false)
    {
        using var request = new HttpRequestMessage(method, $"{StorageApi}/{path}") { Content = content };
        request.Headers.Add("apikey", ServiceKey);

        if (upsert)
        {
            request.Headers.Add("x-upsert", "true");
        }

        using var response = await http.SendAsync(request);

        return response.StatusCode;
    }

    // ═════════════════════════════ Originals ═════════════════════════════

    [Fact]
    public async Task PutOriginal_StoresExactBytes_AndReturnsTheirChecksum()
    {
        var key = NewKey();
        var content = Bytes("original " + key);

        var stored = await storage.PutOriginalAsync(key, content, "image/jpeg", default);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), stored.ChecksumSha256);
        Assert.Equal(content.Length, stored.Bytes);
        Assert.Equal(content, await storage.GetOriginalAsync(key, default));
    }

    [Fact]
    public async Task PutOriginal_AtAnExistingKey_IsRefused_AndKeepsTheFirstBytes()
    {
        var key = NewKey();
        var first = Bytes("first");

        await storage.PutOriginalAsync(key, first, "image/jpeg", default);

        await Assert.ThrowsAsync<OriginalAlreadyExistsException>(
            () => storage.PutOriginalAsync(key, Bytes("second"), "image/jpeg", default));

        Assert.Equal(first, await storage.GetOriginalAsync(key, default));
    }

    /// <summary>
    /// The R30 gap, closed: before 0014 this exact request, with this exact key, returned 200
    /// and replaced the original.
    /// </summary>
    [Fact]
    public async Task AnUpsertOverAnOriginal_WithTheServiceKey_IsRefused()
    {
        var key = NewKey();
        var original = Bytes("keep me");
        await storage.PutOriginalAsync(key, original, "image/jpeg", default);

        using var replacement = new ByteArrayContent(Bytes("replaced"));
        replacement.Headers.ContentType = new("image/jpeg");

        var status = await RawStatusAsync(HttpMethod.Post, $"object/{Originals}/{key}", replacement, upsert: true);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal(original, await storage.GetOriginalAsync(key, default));
    }

    /// <summary>
    /// Before 0014 this returned 200 and the object was gone. Checking the bytes afterwards is
    /// the point: it proves Storage removes the row before the file, so refusing the row
    /// refuses the deletion — rather than assuming that order.
    /// </summary>
    [Fact]
    public async Task DeletingAnOriginal_WithTheServiceKey_IsRefused_AndTheBytesRemain()
    {
        var key = NewKey();
        var original = Bytes("still here");
        await storage.PutOriginalAsync(key, original, "image/jpeg", default);

        var status = await RawStatusAsync(HttpMethod.Delete, $"object/{Originals}/{key}");

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal(original, await storage.GetOriginalAsync(key, default));
    }

    [Fact]
    public async Task MovingAnOriginal_WithTheServiceKey_IsRefused()
    {
        var key = NewKey();
        await storage.PutOriginalAsync(key, Bytes("stay"), "image/jpeg", default);

        using var body = JsonContent($$"""
            {"bucketId":"{{Originals}}","sourceKey":"{{key}}","destinationKey":"moved/{{key}}"}
            """);

        var status = await RawStatusAsync(HttpMethod.Post, "object/move", body);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal(Bytes("stay"), await storage.GetOriginalAsync(key, default));
    }

    /// <summary>
    /// At the database, as the superuser and with Supabase's own direct-delete guard switched
    /// off — so the refusal can only be 0014's trigger.
    /// </summary>
    [Fact]
    public async Task DeletingAnOriginalsRowInSql_IsRefused_EvenAsSuperuser()
    {
        var key = NewKey();
        await storage.PutOriginalAsync(key, Bytes("row"), "image/jpeg", default);

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SupabaseDatabase.ExecuteAsync(connection, transaction, "set local storage.allow_delete_query = 'true'");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            "delete from storage.objects where bucket_id = $1 and name = $2",
            Originals,
            key));

        Assert.Contains("write-once", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingAnOriginalsRowInSql_IsRefused_EvenAsSuperuser()
    {
        var key = NewKey();
        await storage.PutOriginalAsync(key, Bytes("row"), "image/jpeg", default);

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => SupabaseDatabase.ExecuteAsync(
            connection,
            null,
            "update storage.objects set name = name || '.moved' where bucket_id = $1 and name = $2",
            Originals,
            key));

        Assert.Contains("write-once", exception.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Row triggers do not fire on TRUNCATE (R18); the statement guard must.</summary>
    [Fact]
    public async Task TruncatingStorageObjects_IsRefused_WhileOriginalsExist()
    {
        await storage.PutOriginalAsync(NewKey(), Bytes("truncate"), "image/jpeg", default);

        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => SupabaseDatabase.ExecuteAsync(
            connection,
            transaction,
            "truncate storage.objects"));

        Assert.Contains("write-once", exception.MessageText, StringComparison.Ordinal);

        await transaction.RollbackAsync();
    }

    /// <summary>The guard is scoped to originals; derived variants stay replaceable and deletable.</summary>
    [Fact]
    public async Task NormalizedVariants_RemainDeletable()
    {
        var key = PhotoStorageKeys.Normalized(Guid.NewGuid(), Guid.NewGuid(), "jpg");
        await storage.PutNormalizedAsync(key, Bytes("variant"), "image/jpeg", default);

        var status = await RawStatusAsync(HttpMethod.Delete, $"object/{Normalized}/{key}");

        Assert.Equal(HttpStatusCode.OK, status);
        await Assert.ThrowsAsync<StorageObjectNotFoundException>(() => storage.GetNormalizedAsync(key, default));
    }

    // ═════════════════════════════ Normalized variants ═════════════════════════════

    [Fact]
    public async Task PutNormalized_ReplacesAnExistingVariant()
    {
        var key = PhotoStorageKeys.Normalized(Guid.NewGuid(), Guid.NewGuid(), "jpg");

        await storage.PutNormalizedAsync(key, Bytes("v1"), "image/jpeg", default);
        await storage.PutNormalizedAsync(key, Bytes("v2"), "image/jpeg", default);

        Assert.Equal(Bytes("v2"), await storage.GetNormalizedAsync(key, default));

        await RawStatusAsync(HttpMethod.Delete, $"object/{Normalized}/{key}");
    }

    /// <summary>
    /// A signed URL is a bearer credential: fetched with no key at all, it returns the image.
    /// </summary>
    [Fact]
    public async Task SignNormalized_ReturnsAUrlThatServesTheImageWithoutCredentials()
    {
        var key = PhotoStorageKeys.Normalized(Guid.NewGuid(), Guid.NewGuid(), "jpg");
        var content = Bytes("signed");
        await storage.PutNormalizedAsync(key, content, "image/jpeg", default);

        var url = await storage.SignNormalizedAsync(key, default);

        using var anonymous = new HttpClient();
        var fetched = await anonymous.GetByteArrayAsync(url);

        Assert.Equal(content, fetched);
        Assert.Contains("token=", url.Query, StringComparison.Ordinal);

        await RawStatusAsync(HttpMethod.Delete, $"object/{Normalized}/{key}");
    }

    [Fact]
    public async Task ReadingAMissingObject_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<StorageObjectNotFoundException>(
            () => storage.GetOriginalAsync(NewKey(), default));
    }

    // ═════════════════════════════ Keys ═════════════════════════════

    [Theory]
    [InlineData("../other-bucket/x.jpg")]
    [InlineData("not-a-listing/photo.jpg")]
    [InlineData("11111111-1111-4111-8111-111111111111/../../x.jpg")]
    [InlineData("11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222")]
    public async Task AKeyNotBuiltByPhotoStorageKeys_IsRejectedBeforeAnyRequest(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.PutOriginalAsync(key, Bytes("x"), "image/jpeg", default));
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");
}
