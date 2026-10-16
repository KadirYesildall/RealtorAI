using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Media;

namespace Api.Endpoints;

/// <summary>The contract's <c>PhotoUploadResult</c>, one per file in request order.</summary>
internal sealed record PhotoUploadResultDto(
    string FileName,
    bool Accepted,
    Guid? PhotoId,
    string? RejectionReason,
    Guid? DuplicateOfPhotoId);

/// <summary>Upload bounds. <c>MaxFileBytes</c> is configuration; the photo count is FR-003's.</summary>
/// <param name="MaxFileBytes">
/// Largest accepted file. Configured to match the originals bucket's own
/// <c>file_size_limit</c> (0011, 50 MiB), so a file the API accepts is one storage will too.
/// </param>
internal sealed record PhotoUploadOptions(long MaxFileBytes)
{
    /// <summary>FR-003's bound, fixed by the specification rather than configured.</summary>
    internal const int MaxPhotosPerListing = 100;

    /// <summary>
    /// Files per request. The wizard sends ten at a time, so one interrupted request loses at
    /// most ten files' progress, and a request's body stays bounded.
    /// </summary>
    internal const int MaxFilesPerRequest = 10;

    internal long MaxRequestBytes => (MaxFileBytes * MaxFilesPerRequest) + (1024 * 1024);
}

/// <summary>
/// T077 — <c>POST /api/listings/{id}/photos</c> (FR-001, FR-002, FR-003; Story 1 scenarios 1–3).
///
/// <para>
/// <b>Judged per file, bounded per listing.</b> Each file is accepted or rejected on its own,
/// with a reason written for the agent that names the file. A request that would take the
/// listing past 100 photos is refused whole with 409 before anything is stored — so a refused
/// batch leaves no orphaned originals in a bucket that can never delete them, and nothing
/// already uploaded is touched (scenario 3).
/// </para>
/// <para>
/// <b>Order of work.</b> Every file is checked first — size, what its bytes actually are (never
/// its name or declared type), and exact duplicates by SHA-256 against the listing and the rest
/// of the batch. Only then is the bound applied, and only then are originals written. The
/// listing row is locked for the request, so two uploads to one listing cannot both pass the
/// bound and land 101 photos.
/// </para>
/// <para>
/// <b>Originals</b> go to the write-once bucket exactly as received, and the checksum recorded
/// is of those bytes. The normalized copy is the only variant that ever leaves the boundary
/// (VIII.4.c), produced by non-generative operations only (VIII.6).
/// </para>
/// <para>
/// <b>Location.</b> tasks.md places this in <c>Modules.Media/Endpoints/</c>; it lives in the
/// host with the EF model (research R20), and the storage and normalization it applies are in
/// <c>Modules.Media</c>.
/// </para>
/// </summary>
internal static class PhotoEndpoints
{
    internal static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetRequiredService<PhotoUploadOptions>();

        app.MapPost("/api/listings/{listingId:guid}/photos", UploadAsync)
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Media")
            .WithName("UploadPhotos")
            // Bearer-authenticated, never cookie-authenticated, so a cross-site form post
            // carries no credential and there is nothing for an antiforgery token to protect.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(options.MaxRequestBytes));

        return app;
    }

    private sealed record Candidate(int Index, string FileName, byte[] Content, string MimeType, string Checksum);

    private static async Task<Results<JsonHttpResult<List<PhotoUploadResultDto>>, ProblemHttpResult, UnauthorizedHttpResult>> UploadAsync(
        Guid listingId,
        HttpRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IPhotoStorage storage,
        ImageNormalizer normalizer,
        PhotoUploadOptions options,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(
                authorization,
                "Only the listing's agent can add photos to it. A broker admin's access is read-only.");
        }

        if (!request.HasFormContentType)
        {
            return ApiProblems.Validation("files", "Send photos as multipart/form-data under the name 'files'.");
        }

        IFormCollection form;

        try
        {
            form = await request.ReadFormAsync(
                new FormOptions { MultipartBodyLengthLimit = options.MaxRequestBytes },
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            // The body as a whole is past the request bound, so its files cannot be read one by
            // one to be judged individually. Ten photos within the per-file limit always fit.
            return TypedResults.Problem(new ProblemDetails
            {
                Type = ApiProblems.ValidationType,
                Title = "This upload is too large",
                Status = StatusCodes.Status413PayloadTooLarge,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Upload at most {PhotoUploadOptions.MaxFilesPerRequest} photos of up to {Megabytes(options.MaxFileBytes)} MB each per request."),
            });
        }

        var files = form.Files.GetFiles("files");

        if (files.Count == 0)
        {
            return ApiProblems.Validation("files", "Choose at least one photo. A listing needs 1 to 100 photos (FR-003).");
        }

        if (files.Count > PhotoUploadOptions.MaxFilesPerRequest)
        {
            return ApiProblems.Validation(
                "files",
                $"Send at most {PhotoUploadOptions.MaxFilesPerRequest} photos per request; the rest can follow in the next one.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Held until commit: one upload at a time per listing, so the bound below is checked
        // against a count no concurrent upload can change underneath it.
        await context.Database.ExecuteSqlAsync(
            $"select 1 from public.listings where id = {listingId} for update",
            cancellationToken);

        var existing = await context.Photos
            .AsNoTracking()
            .Where(photo => photo.ListingId == listingId)
            .Select(photo => new { photo.Id, photo.ChecksumSha256, photo.OrderIndex })
            .ToListAsync(cancellationToken);

        var knownChecksums = existing.ToDictionary(photo => photo.ChecksumSha256, photo => photo.Id, StringComparer.Ordinal);

        // ── 1. Judge each file, storing nothing ──
        var results = new PhotoUploadResultDto?[files.Count];
        var candidates = new List<Candidate>();
        var batchChecksums = new Dictionary<string, int>(StringComparer.Ordinal);
        var batchDuplicates = new List<(int Index, int TwinIndex)>();

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var fileName = Path.GetFileName(file.FileName);

            if (file.Length > options.MaxFileBytes)
            {
                results[index] = Rejected(
                    fileName,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{fileName} is {Megabytes(file.Length)} MB, over the {Megabytes(options.MaxFileBytes)} MB limit per photo. Export it at a smaller size and upload it again."));
                continue;
            }

            var content = await ReadAsync(file, cancellationToken);
            string mimeType;

            try
            {
                mimeType = normalizer.DetectMimeType(content);
            }
            catch (UnsupportedImageException exception)
            {
                results[index] = Rejected(fileName, $"{fileName} was not accepted: {exception.Message}");
                continue;
            }

            var checksum = Convert.ToHexStringLower(SHA256.HashData(content));

            if (knownChecksums.TryGetValue(checksum, out var duplicateOf))
            {
                results[index] = new PhotoUploadResultDto(
                    fileName,
                    Accepted: false,
                    PhotoId: null,
                    $"{fileName} is identical to a photo already on this listing.",
                    duplicateOf);
                continue;
            }

            if (batchChecksums.TryGetValue(checksum, out var earlierIndex))
            {
                // Pointed at the earlier file's photo once that is stored, below.
                batchDuplicates.Add((index, earlierIndex));
                results[index] = new PhotoUploadResultDto(
                    fileName,
                    Accepted: false,
                    PhotoId: null,
                    $"{fileName} is identical to {Path.GetFileName(files[earlierIndex].FileName)} in this upload.",
                    DuplicateOfPhotoId: null);
                continue;
            }

            batchChecksums[checksum] = index;
            candidates.Add(new Candidate(index, fileName, content, mimeType, checksum));
        }

        // ── 2. The bound, before anything is written ──
        if (existing.Count + candidates.Count > PhotoUploadOptions.MaxPhotosPerListing)
        {
            var room = Math.Max(0, PhotoUploadOptions.MaxPhotosPerListing - existing.Count);

            return ApiProblems.Conflict(
                "Too many photos",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A listing holds 1 to {PhotoUploadOptions.MaxPhotosPerListing} photos (FR-003). This one has {existing.Count}, so {room} more can be added; this upload has {candidates.Count}. Nothing was uploaded, and every photo already on the listing is kept."));
        }

        // ── 3. Store originals and normalized copies ──
        var nextOrder = existing.Count == 0 ? 0 : existing.Max(photo => photo.OrderIndex) + 1;
        var storedByIndex = new Dictionary<int, Guid>();

        foreach (var candidate in candidates)
        {
            var photoId = Guid.CreateVersion7();
            NormalizedImage normalized;

            try
            {
                normalized = normalizer.Normalize(candidate.Content);
            }
            catch (UnsupportedImageException exception)
            {
                results[candidate.Index] = Rejected(candidate.FileName, $"{candidate.FileName} was not accepted: {exception.Message}");
                continue;
            }

            var originalKey = PhotoStorageKeys.Original(listingId, photoId, Extension(candidate.MimeType));
            var normalizedKey = PhotoStorageKeys.Normalized(listingId, photoId, normalized.Extension);

            try
            {
                var stored = await storage.PutOriginalAsync(originalKey, candidate.Content, candidate.MimeType, cancellationToken);

                if (stored.ChecksumSha256 != candidate.Checksum)
                {
                    throw new StorageRequestException("The stored original's checksum does not match the uploaded bytes.");
                }

                await storage.PutNormalizedAsync(normalizedKey, normalized.Content, normalized.ContentType, cancellationToken);
            }
            catch (Exception exception) when (exception is StorageRequestException or OriginalAlreadyExistsException or HttpRequestException)
            {
                results[candidate.Index] = Rejected(
                    candidate.FileName,
                    $"{candidate.FileName} could not be stored just now. Upload it again; the other photos are unaffected.");
                continue;
            }

            context.Photos.Add(new Photo
            {
                Id = photoId,
                ListingId = listingId,
                OriginalStorageKey = originalKey,
                NormalizedStorageKey = normalizedKey,
                ChecksumSha256 = candidate.Checksum,
                Width = normalized.SourceWidth,
                Height = normalized.SourceHeight,
                Bytes = candidate.Content.LongLength,
                MimeType = candidate.MimeType,
                OrderIndex = nextOrder++,
            });

            storedByIndex[candidate.Index] = photoId;
            results[candidate.Index] = new PhotoUploadResultDto(candidate.FileName, Accepted: true, photoId, RejectionReason: null, DuplicateOfPhotoId: null);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Duplicates within the batch point at the photo their twin became.
        foreach (var (index, twinIndex) in batchDuplicates)
        {
            if (storedByIndex.TryGetValue(twinIndex, out var twinPhoto))
            {
                results[index] = results[index]! with { DuplicateOfPhotoId = twinPhoto };
            }
        }

        return TypedResults.Json(results.Select(result => result!).ToList(), statusCode: StatusCodes.Status207MultiStatus);
    }

    private static PhotoUploadResultDto Rejected(string fileName, string reason) =>
        new(fileName, Accepted: false, PhotoId: null, reason, DuplicateOfPhotoId: null);

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream(checked((int)file.Length));
        await stream.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Extension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/heic" => "heic",
        "image/heif" => "heif",
        "image/webp" => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(mimeType), mimeType, "Not an accepted upload type."),
    };
}
