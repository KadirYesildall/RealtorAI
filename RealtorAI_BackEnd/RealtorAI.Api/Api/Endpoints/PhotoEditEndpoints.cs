using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Media;

namespace Api.Endpoints;

internal sealed record ReorderPhotosRequest(IReadOnlyList<Guid>? OrderedPhotoIds, Guid? CoverPhotoId);

internal sealed record PhotoOrderDto(IReadOnlyList<Guid> OrderedPhotoIds, Guid? CoverPhotoId);

/// <summary>
/// T086/T087 — the agent's edits to a listing's photos (FR-001, FR-011, FR-020; Story 2
/// scenario 7): override what analysis concluded, declare an alteration, reorder and choose the
/// cover, remove a photo. Owner only; each is audited.
///
/// <para>
/// <b>Overrides never touch the model's output.</b> The agent's room type and tags are stored on
/// the photo and win over <c>photo_analyses</c>, which keeps what the model said (FR-049). The
/// audit records both values.
/// </para>
/// <para>
/// <b>The alteration declaration is all or nothing</b> (FR-020, AB 723): a type, a disclosure
/// label the MLS's rule profile approves, and the unaltered original on the same listing. No
/// label is invented: an MLS whose approved labels are not recorded — CRMLS, pending legal review
/// (T161) — accepts no declaration at all. The database check enforces the three parts again.
/// The system never alters pixels; this records what the agent did elsewhere.
/// </para>
/// <para>
/// <b>Removal detaches, never deletes, the original</b> (FR-001): the row goes, the bytes stay in
/// the write-once bucket until the retention purge (FR-065).
/// </para>
/// <para>
/// <b>Location.</b> tasks.md places photo endpoints in <c>Modules.Media/Endpoints/</c>; they live
/// in the host with the EF model (research R20).
/// </para>
/// </summary>
internal static class PhotoEditEndpoints
{
    private const string PhotoTarget = "photo";

    /// <summary>Agent-entered tags per photo; the model's schema allows 12, the agent a few more.</summary>
    private const int MaxTags = 20;

    private const int MaxTagLength = 60;

    /// <summary>The alt-text bound the model's schema uses, applied to the agent's caption too.</summary>
    private const int MaxCaptionLength = 300;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, RoomType> RoomTypes = Enum.GetValues<RoomType>()
        .ToDictionary(ListingRepresentation.PostgresLabel, type => type, StringComparer.Ordinal);

    internal static IEndpointRouteBuilder MapPhotoEditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings/{listingId:guid}/photos")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Media");

        group.MapPut("/order", ReorderAsync).WithName("ReorderPhotos");
        group.MapPatch("/{photoId:guid}", PatchAsync).WithName("PatchPhoto");
        group.MapDelete("/{photoId:guid}", DeleteAsync).WithName("DeletePhoto");

        return app;
    }

    // ═════════════════════════════ PATCH ═════════════════════════════

    private static async Task<Results<Ok<PhotoDto>, ProblemHttpResult, UnauthorizedHttpResult>> PatchAsync(
        Guid listingId,
        Guid photoId,
        JsonObject body,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IAuditWriter audit,
        IPhotoStorage storage,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can change its photos.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var photo = await context.Photos.SingleOrDefaultAsync(candidate => candidate.Id == photoId && candidate.ListingId == listingId, cancellationToken);

        if (photo is null)
        {
            return ApiProblems.NotFound("This listing has no photo with that id.");
        }

        var before = Snapshot(photo);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (body.TryGetPropertyValue("roomType", out var roomType))
        {
            if (roomType is null)
            {
                photo.RoomTypeOverride = null;
            }
            else if (roomType.GetValueKind() == JsonValueKind.String && RoomTypes.TryGetValue(roomType.GetValue<string>(), out var parsed))
            {
                photo.RoomTypeOverride = parsed;
            }
            else
            {
                errors["roomType"] = [$"Use one of: {string.Join(", ", RoomTypes.Keys)}; or null to return to the analysis."];
            }
        }

        if (body.TryGetPropertyValue("featureTags", out var tags))
        {
            if (tags is null)
            {
                photo.FeatureTagsOverride = null;
            }
            else if (ParseTags(tags) is { } parsedTags)
            {
                photo.FeatureTagsOverride = JsonSerializer.Serialize(parsedTags);
            }
            else
            {
                errors["featureTags"] = [$"Send a list of up to {MaxTags} tags of 1 to {MaxTagLength} characters, or null."];
            }
        }

        if (body.TryGetPropertyValue("caption", out var caption))
        {
            if (caption is null)
            {
                photo.Caption = null;
            }
            else if (caption.GetValueKind() == JsonValueKind.String && caption.GetValue<string>().Trim() is { Length: <= MaxCaptionLength } text)
            {
                photo.Caption = text.Length == 0 ? null : text;
            }
            else
            {
                errors["caption"] = [$"A caption is text of at most {MaxCaptionLength} characters."];
            }
        }

        if (body.TryGetPropertyValue("altered", out var altered))
        {
            if (altered is null || altered.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors["altered"] = ["Send true to declare an alteration, or false to withdraw it."];
            }
            else if (altered.GetValue<bool>())
            {
                await DeclareAlterationAsync(context, listingId, photo, body, errors, cancellationToken);
            }
            else
            {
                photo.Altered = false;
                photo.AlterationType = null;
                photo.DisclosureLabel = null;
                photo.PairedOriginalId = null;
            }
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var after = Snapshot(photo);

        if (!JsonNode.DeepEquals(before, after))
        {
            await context.SaveChangesAsync(cancellationToken);
            await ReturnApprovedListingToReviewAsync(context, listingId, cancellationToken);
            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.PhotoOverride,
                    ActorId = agentId,
                    ListingId = listingId,
                    BrokerageId = authorization.BrokerageId,
                    TargetType = PhotoTarget,
                    TargetId = photoId,
                    BeforeValue = before.ToJsonString(),
                    AfterValue = after.ToJsonString(),
                },
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(await ListingRepresentation.LoadPhotoAsync(context, storage, photoId, cancellationToken));
    }

    /// <summary>FR-020's three parts, each checked against the listing and its MLS's rules.</summary>
    private static async Task DeclareAlterationAsync(
        AppDbContext context,
        Guid listingId,
        Photo photo,
        JsonObject body,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var type = body["alterationType"] is JsonValue typeValue && typeValue.GetValueKind() == JsonValueKind.String
            ? typeValue.GetValue<string>() switch
            {
                "virtual_staging" => AlterationType.VirtualStaging,
                "other" => AlterationType.Other,
                _ => (AlterationType?)null,
            }
            : null;

        if (type is null)
        {
            errors["alterationType"] = ["Declare the alteration as virtual_staging or other."];
        }

        var targetMlsId = await context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.TargetMlsId)
            .SingleAsync(cancellationToken);
        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, targetMlsId, cancellationToken);
        var approvedLabels = profile is null ? [] : JsonSerializer.Deserialize<List<string>>(profile.DisclosureLabels) ?? [];
        var label = body["disclosureLabel"] is JsonValue labelValue && labelValue.GetValueKind() == JsonValueKind.String
            ? labelValue.GetValue<string>()
            : null;

        if (approvedLabels.Count == 0)
        {
            errors["disclosureLabel"] = [
                "No disclosure label is approved for this listing's MLS yet, so an alteration cannot be declared. "
                + "Use the unaltered photo instead (FR-020).",
            ];
        }
        else if (label is null || !approvedLabels.Contains(label, StringComparer.Ordinal))
        {
            errors["disclosureLabel"] = ["Choose one of the disclosure labels this MLS approves: " + string.Join(" | ", approvedLabels)];
        }

        Guid? pairedId = body["pairedOriginalId"] is JsonValue pairedValue
            && pairedValue.GetValueKind() == JsonValueKind.String
            && Guid.TryParse(pairedValue.GetValue<string>(), out var parsedPaired)
            ? parsedPaired
            : null;

        if (pairedId is null)
        {
            errors["pairedOriginalId"] = ["Name the unaltered original, shown beside the altered photo."];
        }
        else if (pairedId == photo.Id)
        {
            errors["pairedOriginalId"] = ["A photo cannot be its own unaltered original."];
        }
        else
        {
            var paired = await context.Photos.AsNoTracking()
                .Where(candidate => candidate.Id == pairedId && candidate.ListingId == listingId)
                .Select(candidate => new { candidate.Altered })
                .SingleOrDefaultAsync(cancellationToken);

            if (paired is null)
            {
                errors["pairedOriginalId"] = ["The unaltered original must be a photo on this listing."];
            }
            else if (paired.Altered)
            {
                errors["pairedOriginalId"] = ["The paired original must itself be unaltered."];
            }
        }

        // A photo serving as another's unaltered original cannot become an altered one itself.
        if (await context.Photos.AsNoTracking().AnyAsync(other => other.PairedOriginalId == photo.Id, cancellationToken))
        {
            errors["altered"] = ["This photo is the unaltered original of an altered photo, so it cannot be declared altered."];
        }

        if (errors.Count == 0)
        {
            photo.Altered = true;
            photo.AlterationType = type;
            photo.DisclosureLabel = label;
            photo.PairedOriginalId = pairedId;
        }
    }

    private static List<string>? ParseTags(JsonNode tags)
    {
        if (tags is not JsonArray array || array.Count > MaxTags)
        {
            return null;
        }

        var parsed = new List<string>();

        foreach (var tag in array)
        {
            if (tag is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetValue<string>().Trim();

            if (text.Length is 0 or > MaxTagLength)
            {
                return null;
            }

            if (!parsed.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(text);
            }
        }

        return parsed;
    }

    private static JsonObject Snapshot(Photo photo) => new()
    {
        ["roomType"] = photo.RoomTypeOverride is { } room ? ListingRepresentation.PostgresLabel(room) : null,
        ["featureTags"] = photo.FeatureTagsOverride is null ? null : JsonNode.Parse(photo.FeatureTagsOverride),
        ["caption"] = photo.Caption,
        ["altered"] = photo.Altered,
        ["alterationType"] = photo.AlterationType is { } type ? ListingRepresentation.PostgresLabel(type) : null,
        ["disclosureLabel"] = photo.DisclosureLabel,
        ["pairedOriginalId"] = photo.PairedOriginalId,
    };

    // ═════════════════════════════ PUT /order ═════════════════════════════

    private static async Task<Results<Ok<PhotoOrderDto>, ProblemHttpResult, UnauthorizedHttpResult>> ReorderAsync(
        Guid listingId,
        ReorderPhotosRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can order its photos.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Locked, so the set the order is checked against cannot change before it is applied.
        await context.Database.ExecuteSqlAsync($"select 1 from public.listings where id = {listingId} for update", cancellationToken);

        var photos = await context.Photos.Where(photo => photo.ListingId == listingId).ToListAsync(cancellationToken);
        var ordered = request.OrderedPhotoIds ?? [];

        if (ordered.Count == 0
            || ordered.Distinct().Count() != ordered.Count
            || !ordered.ToHashSet().SetEquals(photos.Select(photo => photo.Id)))
        {
            return ApiProblems.Validation(
                "orderedPhotoIds",
                "Name every photo on this listing exactly once. If photos were added or removed elsewhere, reload and order again.");
        }

        if (request.CoverPhotoId is { } coverId && !ordered.Contains(coverId))
        {
            return ApiProblems.Validation("coverPhotoId", "The cover must be one of this listing's photos.");
        }

        var before = new PhotoOrderDto(
            photos.OrderBy(photo => photo.OrderIndex).Select(photo => photo.Id).ToList(),
            photos.SingleOrDefault(photo => photo.IsCover)?.Id);

        // order_index is unique and deferrable (0006), so the whole order lands at commit with no
        // transient collision between photos trading places.
        for (var position = 0; position < ordered.Count; position++)
        {
            photos.Single(photo => photo.Id == ordered[position]).OrderIndex = position;
        }

        await context.SaveChangesAsync(cancellationToken);

        var cover = request.CoverPhotoId ?? before.CoverPhotoId;

        if (request.CoverPhotoId is not null)
        {
            // Cleared before set: the one-cover index is not deferrable.
            await context.Database.ExecuteSqlAsync($"update public.photos set is_cover = false where listing_id = {listingId} and is_cover", cancellationToken);
            await context.Database.ExecuteSqlAsync($"update public.photos set is_cover = true where id = {cover}", cancellationToken);
            await context.Database.ExecuteSqlAsync($"update public.listings set cover_photo_id = {cover} where id = {listingId}", cancellationToken);
        }

        var after = new PhotoOrderDto(ordered, cover);

        await ReturnApprovedListingToReviewAsync(context, listingId, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.PhotoReorder,
                ActorId = agentId,
                ListingId = listingId,
                BrokerageId = authorization.BrokerageId,
                TargetType = ListingEndpoints.ListingTarget,
                TargetId = listingId,
                BeforeValue = JsonSerializer.Serialize(before, Web),
                AfterValue = JsonSerializer.Serialize(after, Web),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(after);
    }

    // ═════════════════════════════ DELETE ═════════════════════════════

    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> DeleteAsync(
        Guid listingId,
        Guid photoId,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can remove its photos.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var photo = await context.Photos.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == photoId && candidate.ListingId == listingId, cancellationToken);

        if (photo is null)
        {
            return ApiProblems.NotFound("This listing has no photo with that id.");
        }

        if (await context.Photos.AsNoTracking().AnyAsync(other => other.PairedOriginalId == photoId, cancellationToken))
        {
            return ApiProblems.Conflict(
                "This photo is paired with an altered photo",
                "It is the unaltered original shown beside an altered photo (FR-020). Withdraw that declaration first, then remove it.");
        }

        // The row goes; the original's bytes stay in the write-once bucket (FR-001), reclaimed
        // only by the retention purge (FR-065). The cover pointer clears itself (on delete set null).
        await context.Database.ExecuteSqlAsync($"delete from public.photos where id = {photoId}", cancellationToken);
        await ReturnApprovedListingToReviewAsync(context, listingId, cancellationToken);

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.PhotoDelete,
                ActorId = agentId,
                ListingId = listingId,
                BrokerageId = authorization.BrokerageId,
                TargetType = PhotoTarget,
                TargetId = photoId,
                BeforeValue = JsonSerializer.Serialize(
                    new { photo.OriginalStorageKey, photo.ChecksumSha256, photo.OrderIndex, photo.IsCover },
                    Web),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>FR-025: a change to an approved listing's photos returns it to review.</summary>
    private static Task<int> ReturnApprovedListingToReviewAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
            update public.listings
            set status = 'ready_for_review', row_version = row_version + 1, updated_at = timezone('utc', now())
            where id = {listingId} and status = 'approved'
            """,
            cancellationToken);
}
