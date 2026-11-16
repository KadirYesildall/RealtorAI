using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace AiEval;

/// <summary>
/// A file an evaluation set refers to by where it comes from and what its bytes hash to, rather
/// than by committing it.
/// </summary>
/// <param name="Id">The item's id in the manifest, for reports.</param>
/// <param name="FileUrl">The exact URL whose bytes were hashed when the set was assembled.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of those bytes.</param>
internal sealed record RemoteEvalFile(string Id, string FileUrl, string Sha256);

/// <summary>What the cache holds for one file.</summary>
internal enum CachedFileState
{
    /// <summary>Present, and its bytes hash to the manifest's SHA-256.</summary>
    Verified,

    /// <summary>Not in the cache.</summary>
    Missing,

    /// <summary>
    /// Present, but its bytes hash to something else. It is not the photo that was labelled, so
    /// it is never measured on.
    /// </summary>
    Mismatched,
}

/// <summary>Every file of a set sorted by what the cache holds for it.</summary>
internal sealed record CacheCheck(
    IReadOnlyList<RemoteEvalFile> Verified,
    IReadOnlyList<RemoteEvalFile> Missing,
    IReadOnlyList<RemoteEvalFile> Mismatched)
{
    internal bool Complete => Missing.Count == 0 && Mismatched.Count == 0;
}

/// <summary>
/// The local cache of evaluation-set files that are not committed to the repository: the photo
/// set's images (T156).
///
/// <para>
/// <b>Why the images are not committed.</b> They are third-party photographs under CC BY, CC0 or
/// a public-domain status. Committing them would make the repository a redistributor of every
/// one, with CC BY's attribution duty attached to each copy of the repository; it would also put
/// tens of megabytes of binary history into every clone. The manifest instead records each
/// photo's source, license and SHA-256, which is enough to fetch the identical bytes and to
/// prove they are identical.
/// </para>
/// <para>
/// <b>Content-addressed.</b> A file is stored as <c>&lt;root&gt;/&lt;task&gt;/&lt;sha256&gt;&lt;ext&gt;</c>,
/// so a name can only ever stand for one content, and a file whose bytes do not hash to its name
/// is detectable as damaged or substituted. Every read re-hashes: a cached file is trusted only
/// for as long as it takes to verify it.
/// </para>
/// <para>
/// <b>Where.</b> <see cref="DirectoryEnvironmentVariable"/> when set, otherwise a directory under
/// the user's local application data, outside the repository. The fetch command and the suite
/// resolve it the same way, so what one downloads the other finds. A cache placed inside the
/// repository should be named <c>.eval-cache/</c>, which <c>.gitignore</c> excludes.
/// </para>
/// </summary>
internal sealed class EvalSetCache
{
    /// <summary>Overrides the cache location for both the fetch command and the suites.</summary>
    internal const string DirectoryEnvironmentVariable = "REALTORAI_EVAL_CACHE";

    internal EvalSetCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    internal string Root { get; }

    /// <summary>The cache location when none is given: the environment variable, else local application data.</summary>
    internal static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RealtorAI",
                "AiEval",
                "eval-set-cache")
            : configured;
    }

    /// <summary>Where <paramref name="file"/> is kept for <paramref name="taskKey"/>.</summary>
    internal string PathFor(string taskKey, RemoteEvalFile file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        ArgumentNullException.ThrowIfNull(file);

        return Path.Combine(Root, taskKey, file.Sha256 + ExtensionOf(file.FileUrl));
    }

    /// <summary>
    /// Reads <paramref name="file"/> from the cache and verifies it. <paramref name="content"/> is
    /// set only when the result is <see cref="CachedFileState.Verified"/>, so a caller cannot
    /// measure on bytes that failed the check.
    /// </summary>
    internal CachedFileState TryRead(string taskKey, RemoteEvalFile file, out byte[]? content)
    {
        content = null;
        var path = PathFor(taskKey, file);

        if (!File.Exists(path))
        {
            return CachedFileState.Missing;
        }

        var bytes = File.ReadAllBytes(path);

        if (!HashMatches(bytes, file.Sha256))
        {
            return CachedFileState.Mismatched;
        }

        content = bytes;
        return CachedFileState.Verified;
    }

    /// <summary>Sorts <paramref name="files"/> by what the cache holds for each.</summary>
    internal CacheCheck Check(string taskKey, IReadOnlyList<RemoteEvalFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var verified = new List<RemoteEvalFile>();
        var missing = new List<RemoteEvalFile>();
        var mismatched = new List<RemoteEvalFile>();

        foreach (var file in files)
        {
            switch (TryRead(taskKey, file, out _))
            {
                case CachedFileState.Verified:
                    verified.Add(file);
                    break;
                case CachedFileState.Missing:
                    missing.Add(file);
                    break;
                default:
                    mismatched.Add(file);
                    break;
            }
        }

        return new CacheCheck(verified, missing, mismatched);
    }

    internal static bool HashMatches(ReadOnlySpan<byte> content, string expectedSha256) =>
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), expectedSha256, StringComparison.Ordinal);

    /// <summary>
    /// The URL path's extension, so a cached file opens in an image viewer when a reviewer looks
    /// at it. It has no bearing on how the file is read: the normalizer detects the format from
    /// the bytes.
    /// </summary>
    private static string ExtensionOf(string fileUrl)
    {
        var extension = Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()
            : string.Empty;

        return extension.Length is > 1 and <= 5 && extension[1..].All(char.IsAsciiLetterOrDigit)
            ? extension
            : ".bin";
    }
}

/// <summary>What one fetch did.</summary>
/// <param name="AlreadyPresent">Files the cache already held with the right hash; not downloaded again.</param>
/// <param name="Downloaded">Files downloaded and verified in this run.</param>
/// <param name="Failed">Files the cache still lacks after this run, and why.</param>
internal sealed record FetchResult(
    IReadOnlyList<RemoteEvalFile> AlreadyPresent,
    IReadOnlyList<RemoteEvalFile> Downloaded,
    IReadOnlyList<(RemoteEvalFile File, string Problem)> Failed)
{
    internal int Verified => AlreadyPresent.Count + Downloaded.Count;
}

/// <summary>
/// T156 — <c>aieval --fetch-eval-sets</c>: downloads a set's files into the
/// <see cref="EvalSetCache"/> and keeps only bytes that hash to the manifest's SHA-256.
///
/// <para>
/// <b>A hash mismatch is refused, not tolerated.</b> A source can replace a file (Commons keeps
/// file history, and re-renders thumbnails), and a CDN can serve an error page with a 200. Either
/// way the bytes are not the photo that was labelled, so they are not written. The item stays
/// missing and the suite reports the evaluation incomplete until the set is re-versioned with
/// the new file and a reviewed label.
/// </para>
/// <para>
/// <b>Safe to run again.</b> Files already present with the right hash are not downloaded again;
/// a present file with the wrong hash is downloaded again and replaced only if the new bytes
/// verify. Each file is written to a temporary name and moved into place, so an interrupted run
/// never leaves a partial file under a hash name.
/// </para>
/// <para>
/// <b>Polite to the sources.</b> One request at a time, with a descriptive
/// <see cref="UserAgent"/> naming the project, as the Wikimedia User-Agent policy requires
/// (requests without one are blocked). A 429 or 503 is retried after the server's Retry-After,
/// a bounded number of times; other failures are reported for that file and the run goes on.
/// </para>
/// </summary>
internal static class EvalSetFetcher
{
    /// <summary>
    /// Identifies the harness and how to reach the project, per the Wikimedia User-Agent policy.
    /// The repository URL is the contact; no personal address is sent.
    /// </summary>
    internal const string UserAgent =
        "RealtorAI-AiEval/1.0 (+https://github.com/KadirYesildall/RealtorAI; evaluation-set fetcher)";

    /// <summary>Attempts per file when the server asks the client to slow down (429, 503).</summary>
    internal const int AttemptsWhenThrottled = 3;

    /// <summary>
    /// The largest response accepted. The set's files are photographs of a few megabytes; a body
    /// past this is not one of them, and buffering it would only be a way to exhaust memory.
    /// </summary>
    internal const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The wait before retrying a throttled request that gave no usable Retry-After.</summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The longest Retry-After honoured; a longer one is reported as a failure instead.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>An HTTP client configured for fetching: the User-Agent and the response size limit.</summary>
    internal static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.MaxResponseContentBufferSize = MaxFileBytes;
        client.Timeout = TimeSpan.FromMinutes(2);

        return client;
    }

    /// <param name="http">A client from <see cref="CreateClient"/>.</param>
    /// <param name="cache">Where verified files are kept.</param>
    /// <param name="taskKey">The task whose set the files belong to (the cache's subdirectory).</param>
    /// <param name="files">The set's files.</param>
    /// <param name="report">Progress and per-file problems.</param>
    /// <param name="delay">Waits between throttled attempts. Tests pass one that does not sleep.</param>
    /// <param name="cancellationToken">Stops the run; files already verified stay.</param>
    internal static async Task<FetchResult> FetchAsync(
        HttpClient http,
        EvalSetCache cache,
        string taskKey,
        IReadOnlyList<RemoteEvalFile> files,
        TextWriter report,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);

        delay ??= Task.Delay;

        var present = new List<RemoteEvalFile>();
        var downloaded = new List<RemoteEvalFile>();
        var failed = new List<(RemoteEvalFile, string)>();

        Directory.CreateDirectory(Path.Combine(cache.Root, taskKey));

        foreach (var file in files)
        {
            if (cache.TryRead(taskKey, file, out _) == CachedFileState.Verified)
            {
                present.Add(file);
                continue;
            }

            var problem = await DownloadAsync(http, cache, taskKey, file, delay, cancellationToken);

            if (problem is null)
            {
                downloaded.Add(file);
                report.WriteLine($"  fetched  {file.Id}");
            }
            else
            {
                failed.Add((file, problem));
                report.WriteLine($"  FAILED   {file.Id}: {problem}");
            }
        }

        return new FetchResult(present, downloaded, failed);
    }

    /// <returns>Null when the file was downloaded and verified; otherwise why it was not.</returns>
    private static async Task<string?> DownloadAsync(
        HttpClient http,
        EvalSetCache cache,
        string taskKey,
        RemoteEvalFile file,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        byte[] content;

        try
        {
            var attempt = 0;

            while (true)
            {
                attempt++;

                using var response = await http.GetAsync(file.FileUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    && attempt < AttemptsWhenThrottled)
                {
                    var wait = RetryDelay(response);

                    if (wait > MaxRetryDelay)
                    {
                        return $"HTTP {(int)response.StatusCode}; the server asked to wait {wait.TotalSeconds:0}s. Run the fetch again later.";
                    }

                    await delay(wait, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return "HTTP 404: the source no longer has this file. The set needs a new version without it.";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}.";
                }

                content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                break;
            }
        }
        catch (HttpRequestException exception)
        {
            return $"request failed: {exception.Message}";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "the request timed out.";
        }

        if (!EvalSetCache.HashMatches(content, file.Sha256))
        {
            return $"refused: the downloaded bytes hash to {Convert.ToHexStringLower(SHA256.HashData(content))}, not the "
                + $"manifest's {file.Sha256}. The source has changed the file, so it is not the photo that was labelled; "
                + "nothing was written.";
        }

        var target = cache.PathFor(taskKey, file);
        var temporary = target + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return null;
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var until && until > TimeSpan.Zero ? until : TimeSpan.Zero,
            _ => DefaultRetryDelay,
        };
}
