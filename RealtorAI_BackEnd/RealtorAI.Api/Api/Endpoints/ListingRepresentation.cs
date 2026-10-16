using System.Text.Json;
using Api.Authorization;
using Api.Concurrency;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Modules.Listings;
using Modules.Media;
using ListingEntity = Api.Data.Entities.Listing;

namespace Api.Endpoints;

/// <summary>The basics, as <c>CreateListingRequest</c> in the contract. Also the autosave body.</summary>
internal sealed record ListingBasicsRequest(
    string? AddressLine1,
    string? City,
    string? State,
    string? PostalCode,
    string? PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes)
{
    internal ListingBasicsInput ToInput() =>
        new(AddressLine1, City, State, PostalCode, PropertyType, ListPrice, Bedrooms, Bathrooms, AgentNotes);
}

/// <summary>The contract's <c>Listing</c>.</summary>
internal sealed record ListingDto(
    Guid Id,
    string Status,
    string AddressLine1,
    string City,
    string State,
    string PostalCode,
    string PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes,
    string? TargetMlsId,
    Guid? CoverPhotoId,
    int RowVersion,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The contract's <c>ListingDetail</c>, as far as Slice A's first story fills it.
///
/// <para>
/// <c>missingRequiredFieldKeys</c> is a contract addition (research R33): FR-008 requires the
/// public-record fields to be "visibly marked as required" while they are empty, and an empty
/// field has no row — so no <c>ListingField</c> to carry <c>requiredBeforeApproval</c> — until
/// someone enters it. <c>contentDrafts</c>, <c>findings</c> and <c>approvalBlockers</c> join this
/// shape with the stories that produce them (US3–US5); until then nothing can create a draft or
/// a finding, and an empty list would claim a check had run.
/// </para>
/// </summary>
internal sealed record ListingDetailDto(
    Guid Id,
    string Status,
    string AddressLine1,
    string City,
    string State,
    string PostalCode,
    string PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes,
    string? TargetMlsId,
    Guid? CoverPhotoId,
    int RowVersion,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ListingFieldDto> Fields,
    IReadOnlyList<string> MissingRequiredFieldKeys,
    IReadOnlyList<PhotoDto> Photos);

/// <summary>The contract's <c>ListingField</c>.</summary>
internal sealed record ListingFieldDto(
    Guid Id,
    string ResoFieldKey,
    JsonElement Value,
    string Provenance,
    bool Confirmed,
    DateTimeOffset? ConfirmedAt,
    string? SourceNote,
    bool RequiredBeforeApproval,
    int RowVersion);

/// <summary>The contract's <c>Photo</c>.</summary>
internal sealed record PhotoDto(
    Guid Id,
    int OrderIndex,
    bool IsCover,
    string? Caption,
    int? Width,
    int? Height,
    string MimeType,
    string ChecksumSha256,
    string? NormalizedUrl,
    bool Altered,
    string? AlterationType,
    string? DisclosureLabel,
    Guid? PairedOriginalId,
    PhotoAnalysisDto? Analysis);

/// <summary>The contract's <c>PhotoAnalysis</c>.</summary>
internal sealed record PhotoAnalysisDto(
    string? RoomType,
    decimal? RoomTypeConfidence,
    IReadOnlyList<string> FeatureTags,
    IReadOnlyList<string> QualityFlags,
    IReadOnlyList<string> ContentFlags,
    string? AltText,
    bool CoverRecommended,
    int? OrderRecommendation,
    string? RecommendationReason,
    string? ModelId,
    string? PromptVersion,
    bool Failed,
    string? FailureReason);

/// <summary>
/// Reading and representing listings, shared by the listing, field and photo endpoints so each
/// answers with the same shape for the same row.
/// </summary>
internal static class ListingRepresentation
{
    internal static ListingDto ToDto(ListingEntity listing) => new(
        listing.Id,
        FormatStatus(listing.Status),
        listing.AddressLine1,
        listing.City,
        listing.State.Trim(),
        listing.PostalCode,
        listing.PropertyType,
        listing.ListPrice,
        listing.Bedrooms,
        listing.Bathrooms,
        listing.AgentNotes,
        listing.TargetMlsId,
        listing.CoverPhotoId,
        listing.RowVersion,
        listing.DeletedAt,
        listing.CreatedAt,
        listing.UpdatedAt);

    internal static ListingFieldDto ToDto(ListingField field, IReadOnlySet<string> requiredKeys) => new(
        field.Id,
        field.ResoFieldKey,
        ParseJson(field.Value),
        ListingFieldService.Format(field.Provenance),
        field.Confirmed,
        field.ConfirmedAt,
        field.SourceNote,
        requiredKeys.Contains(field.ResoFieldKey),
        field.RowVersion);

    internal static string FormatStatus(ListingStatus status) => status switch
    {
        ListingStatus.Draft => "draft",
        ListingStatus.Analyzing => "analyzing",
        ListingStatus.ReadyForReview => "ready_for_review",
        ListingStatus.Approved => "approved",
        ListingStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// The answer to a denied listing request: 404 where the caller has no relationship to the
    /// listing (so an id cannot be probed for existence), 403 where they may read it but not do
    /// this — a broker admin, or the owner of an archived listing.
    /// </summary>
    internal static ProblemHttpResult Denied(ListingAuthorization authorization, string forbiddenDetail) =>
        authorization.ShouldReportAsNotFound
            ? ApiProblems.NotFound("No listing with that id is available to you.")
            : ApiProblems.Forbidden(authorization.Reason == AuthorizationReason.ListingArchived
                ? "This listing is archived. It stays readable with its history but can no longer be changed (FR-067)."
                : forbiddenDetail);

    internal static void SetETag(HttpResponse response, int rowVersion) =>
        response.Headers.ETag = IfMatch.ToETag(rowVersion);

    /// <summary>The listing's MLS rule profile, or null when it targets none or the profile is unknown.</summary>
    internal static Task<MlsRuleProfile?> LoadRuleProfileAsync(AppDbContext context, string? targetMlsId, CancellationToken cancellationToken) =>
        targetMlsId is null
            ? Task.FromResult<MlsRuleProfile?>(null)
            : context.MlsRuleProfiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.Id == targetMlsId, cancellationToken);

    internal static IReadOnlySet<string> RequiredKeys(MlsRuleProfile? profile) =>
        ListingFieldService.RequiredKeys(
            profile is null ? [] : JsonSerializer.Deserialize<List<string>>(profile.RequiredFields) ?? []);

    /// <summary>The profile's enumerated lookups: RESO key to the values this MLS accepts.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> EnumeratedLookups(MlsRuleProfile? profile)
    {
        if (profile is null)
        {
            return new Dictionary<string, IReadOnlySet<string>>();
        }

        var lookups = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(profile.EnumeratedFieldLookups) ?? [];

        return lookups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)new HashSet<string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The full detail. Photos carry a short-lived signed URL to their normalized copy; the
    /// original never gets one (VIII.4.c).
    /// </summary>
    internal static async Task<ListingDetailDto> LoadDetailAsync(
        AppDbContext context,
        IPhotoStorage storage,
        ListingEntity listing,
        CancellationToken cancellationToken)
    {
        var profile = await LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken);
        var requiredKeys = RequiredKeys(profile);

        var fields = await context.ListingFields
            .AsNoTracking()
            .Where(field => field.ListingId == listing.Id)
            .OrderBy(field => field.ResoFieldKey)
            .ToListAsync(cancellationToken);

        var photos = await context.Photos
            .AsNoTracking()
            .Where(photo => photo.ListingId == listing.Id)
            .OrderBy(photo => photo.OrderIndex)
            .ToListAsync(cancellationToken);

        var photoIds = photos.Select(photo => photo.Id).ToList();
        var analyses = await context.PhotoAnalyses
            .AsNoTracking()
            .Where(analysis => photoIds.Contains(analysis.PhotoId))
            .ToDictionaryAsync(analysis => analysis.PhotoId, cancellationToken);

        var urls = await SignAsync(storage, photos, cancellationToken);

        var present = fields.Select(field => field.ResoFieldKey).ToHashSet(StringComparer.Ordinal);
        var missing = requiredKeys.Where(key => !present.Contains(key)).Order(StringComparer.Ordinal).ToList();

        return new ListingDetailDto(
            listing.Id,
            FormatStatus(listing.Status),
            listing.AddressLine1,
            listing.City,
            listing.State.Trim(),
            listing.PostalCode,
            listing.PropertyType,
            listing.ListPrice,
            listing.Bedrooms,
            listing.Bathrooms,
            listing.AgentNotes,
            listing.TargetMlsId,
            listing.CoverPhotoId,
            listing.RowVersion,
            listing.DeletedAt,
            listing.CreatedAt,
            listing.UpdatedAt,
            fields.Select(field => ToDto(field, requiredKeys)).ToList(),
            missing,
            photos.Select(photo => ToDto(photo, urls.GetValueOrDefault(photo.Id), analyses.GetValueOrDefault(photo.Id))).ToList());
    }

    private static PhotoDto ToDto(Photo photo, string? normalizedUrl, PhotoAnalysis? analysis) => new(
        photo.Id,
        photo.OrderIndex,
        photo.IsCover,
        photo.Caption,
        photo.Width,
        photo.Height,
        photo.MimeType,
        photo.ChecksumSha256,
        normalizedUrl,
        photo.Altered,
        photo.AlterationType switch
        {
            AlterationType.VirtualStaging => "virtual_staging",
            AlterationType.Other => "other",
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(photo), photo.AlterationType, null),
        },
        photo.DisclosureLabel,
        photo.PairedOriginalId,
        analysis is null ? null : ToDto(analysis));

    private static PhotoAnalysisDto ToDto(PhotoAnalysis analysis) => new(
        analysis.RoomType is { } roomType ? PostgresLabel(roomType) : null,
        analysis.RoomTypeConfidence,
        Strings(analysis.FeatureTags),
        Strings(analysis.QualityFlags),
        Strings(analysis.ContentFlags),
        analysis.AltText,
        analysis.CoverRecommended,
        analysis.OrderRecommendation,
        analysis.RecommendationReason,
        analysis.ModelId,
        analysis.PromptVersion,
        analysis.Failed,
        analysis.FailureReason);

    /// <summary>A <c>[PgName]</c> enum's database label, which is also its contract spelling.</summary>
    private static string PostgresLabel<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        typeof(TEnum).GetField(value.ToString())!
            .GetCustomAttributes(typeof(NpgsqlTypes.PgNameAttribute), inherit: false)
            .Cast<NpgsqlTypes.PgNameAttribute>()
            .Single()
            .PgName;

    private static List<string> Strings(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    /// <summary>
    /// Signs every normalized copy, a few at a time: one listing holds up to 100 photos and the
    /// storage API signs one object per request.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> SignAsync(
        IPhotoStorage storage,
        IReadOnlyList<Photo> photos,
        CancellationToken cancellationToken)
    {
        var urls = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();

        await Parallel.ForEachAsync(
            photos.Where(photo => photo.NormalizedStorageKey is not null),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (photo, token) =>
            {
                var url = await storage.SignNormalizedAsync(photo.NormalizedStorageKey!, token);
                urls[photo.Id] = url.ToString();
            });

        return new Dictionary<Guid, string>(urls);
    }
}
