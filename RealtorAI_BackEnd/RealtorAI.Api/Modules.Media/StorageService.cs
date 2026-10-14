using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modules.Media;

/// <summary>Storage settings. All server-only.</summary>
/// <param name="StorageApiBase">
/// The Storage REST root, e.g. <c>http://127.0.0.1:54321/storage/v1</c> locally.
/// </param>
/// <param name="ServiceKey">
/// The Supabase secret key. Read from an environment variable named in configuration, never
/// from a settings file, and never exposed to the browser.
/// </param>
/// <param name="SignedUrlLifetime">
/// How long a signed URL for a normalized variant stays valid. Short, because a signed URL is a
/// bearer credential for the image: anyone holding it can fetch the photo until it expires.
/// </param>
public sealed record StorageOptions(
    Uri StorageApiBase,
    string ServiceKey,
    string OriginalsBucket,
    string NormalizedBucket,
    TimeSpan SignedUrlLifetime);

/// <summary>An original already exists at the key. Originals are never replaced (FR-001).</summary>
public sealed class OriginalAlreadyExistsException : Exception
{
    public OriginalAlreadyExistsException(string key)
        : base($"An original already exists at '{key}'. Originals are write-once and are never replaced.")
    {
        Key = key;
    }

    public OriginalAlreadyExistsException()
    {
        Key = string.Empty;
    }

    public OriginalAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
        Key = string.Empty;
    }

    public string Key { get; }
}

/// <summary>No object exists at the key.</summary>
public sealed class StorageObjectNotFoundException : Exception
{
    public StorageObjectNotFoundException(string bucket, string key)
        : base($"No object exists at '{bucket}/{key}'.")
    {
    }

    public StorageObjectNotFoundException()
    {
    }

    public StorageObjectNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Storage refused or failed a request for a reason other than existence.</summary>
public sealed class StorageRequestException : Exception
{
    public StorageRequestException(string message)
        : base(message)
    {
    }

    public StorageRequestException()
    {
    }

    public StorageRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A stored original: where it is and the SHA-256 of exactly the bytes stored.</summary>
public sealed record StoredOriginal(string Key, string ChecksumSha256, long Bytes);

/// <summary>Object keys. The first path segment is the listing id, which 0011's policies join on.</summary>
public static partial class PhotoStorageKeys
{
    public static string Original(Guid listingId, Guid photoId, string extension) =>
        $"{listingId:D}/{photoId:D}.{NormalizeExtension(extension)}";

    public static string Normalized(Guid listingId, Guid photoId, string extension) =>
        $"{listingId:D}/{photoId:D}.{NormalizeExtension(extension)}";

    /// <summary>
    /// Keys are only ever built by this class; anything else reaching the service is a bug or
    /// an attempt to address an object outside a listing's prefix.
    /// </summary>
    internal static void EnsureWellFormed(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                $"'{key}' is not a photo storage key. Keys are '{{listingId}}/{{photoId}}.{{ext}}'.",
                nameof(key));
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.TrimStart('.').ToLowerInvariant();

        return ExtensionPattern().IsMatch(normalized)
            ? normalized
            : throw new ArgumentException($"'{extension}' is not a file extension.", nameof(extension));
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.[a-z0-9]{2,5}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z0-9]{2,5}$")]
    private static partial Regex ExtensionPattern();
}

/// <summary>Photo storage operations the API needs (T057).</summary>
public interface IPhotoStorage
{
    /// <summary>
    /// Stores an original exactly as uploaded and returns its checksum. Write-once: if anything
    /// already exists at the key, nothing is written and
    /// <see cref="OriginalAlreadyExistsException"/> is thrown.
    /// </summary>
    Task<StoredOriginal> PutOriginalAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>Reads an original's exact bytes, for checksum verification and normalization.</summary>
    Task<byte[]> GetOriginalAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores or replaces a normalized variant. Variants are derived data and replaceable.</summary>
    Task PutNormalizedAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);

    Task<byte[]> GetNormalizedAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// A short-lived URL for a normalized variant — for display, and for a vision model that
    /// fetches by URL. There is deliberately no equivalent for originals: an original never
    /// leaves the API (VIII.4.c), so there is no reason for a URL to one to exist.
    /// </summary>
    Task<Uri> SignNormalizedAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// T057 — Supabase Storage over its REST API.
///
/// <para>
/// <b>Write-once originals, at two layers.</b> This class exposes no way to overwrite, move or
/// delete an original — uploads to the originals bucket send <c>x-upsert: false</c>. That is a
/// convention, and on its own it would be the only thing protecting originals, because the
/// service key this class uses bypasses storage RLS. The guarantee itself is
/// <c>0014_originals_write_once.sql</c>: a trigger refusing any update or delete of an original's
/// row for every role. Research R30 records how the gap was found.
/// </para>
/// <para>
/// <b>Why the REST API rather than the Supabase C# SDK.</b> Every call here was exercised
/// against the running stack before it was written — upload, duplicate rejection, download,
/// signing, and the shapes of the errors. The SDK would put an unverified layer of method
/// signatures and error mapping between this code and that verified behaviour, for no gain in
/// the five operations needed.
/// </para>
/// </summary>
public sealed class StorageService : IPhotoStorage
{
    private readonly HttpClient http;
    private readonly StorageOptions options;

    public StorageService(HttpClient http, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceKey);

        this.http = http;
        this.options = options;
    }

    public async Task<StoredOriginal> PutOriginalAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        PhotoStorageKeys.EnsureWellFormed(key);

        // Hashed here, from the bytes being sent, so the recorded checksum describes exactly
        // what was stored — not what the client claimed or what a later read returns.
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content.Span));

        await UploadAsync(options.OriginalsBucket, key, content, contentType, upsert: false, cancellationToken);

        return new StoredOriginal(key, checksum, content.Length);
    }

    public Task<byte[]> GetOriginalAsync(string key, CancellationToken cancellationToken)
    {
        PhotoStorageKeys.EnsureWellFormed(key);

        return DownloadAsync(options.OriginalsBucket, key, cancellationToken);
    }

    public Task PutNormalizedAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        PhotoStorageKeys.EnsureWellFormed(key);

        return UploadAsync(options.NormalizedBucket, key, content, contentType, upsert: true, cancellationToken);
    }

    public Task<byte[]> GetNormalizedAsync(string key, CancellationToken cancellationToken)
    {
        PhotoStorageKeys.EnsureWellFormed(key);

        return DownloadAsync(options.NormalizedBucket, key, cancellationToken);
    }

    public async Task<Uri> SignNormalizedAsync(string key, CancellationToken cancellationToken)
    {
        PhotoStorageKeys.EnsureWellFormed(key);

        using var request = CreateRequest(HttpMethod.Post, $"object/sign/{options.NormalizedBucket}/{key}");
        request.Content = JsonContent.Create(new
        {
            expiresIn = (int)options.SignedUrlLifetime.TotalSeconds,
        });

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, options.NormalizedBucket, key, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // Storage returns the path relative to its own root, e.g. "/object/sign/...?token=...".
        var relative = body.TryGetProperty("signedURL", out var signed) ? signed.GetString() : null;

        if (string.IsNullOrEmpty(relative))
        {
            throw new StorageRequestException("Storage returned no signed URL.");
        }

        return new Uri(StorageRoot() + relative.TrimStart('/'));
    }

    // ───────────────────────────── Transport ─────────────────────────────

    private async Task UploadAsync(
        string bucket,
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        bool upsert,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"object/{bucket}/{key}");
        request.Headers.Add("x-upsert", upsert ? "true" : "false");
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, bucket, key, cancellationToken);
    }

    private async Task<byte[]> DownloadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"object/{bucket}/{key}");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, bucket, key, cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(StorageRoot() + relative));
        request.Headers.Add("apikey", options.ServiceKey);

        return request;
    }

    private string StorageRoot()
    {
        var root = options.StorageApiBase.ToString();
        return root.EndsWith('/') ? root : root + "/";
    }

    /// <summary>
    /// Storage reports failures as HTTP 400 with the real status in the body's
    /// <c>statusCode</c> field — verified against the running stack — so the body, not the HTTP
    /// status, decides which exception is thrown.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string? statusCode = null;
        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                statusCode = root.TryGetProperty("statusCode", out var code) ? code.ToString() : null;
                message = root.TryGetProperty("message", out var text2) ? text2.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the generic failure with the raw status.
        }

        throw statusCode switch
        {
            "409" => new OriginalAlreadyExistsException(key),
            "404" => new StorageObjectNotFoundException(bucket, key),
            _ => new StorageRequestException(string.Create(
                CultureInfo.InvariantCulture,
                $"Storage refused {bucket}/{key}: HTTP {(int)response.StatusCode}, "
                + $"status {statusCode ?? "unknown"}, {message ?? "no message"}.")),
        };
    }
}
