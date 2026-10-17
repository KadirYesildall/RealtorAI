using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Data.Entities;
using Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Modules.Content;
using Modules.Listings;

namespace Api.Approvals;

/// <summary>One output the agent can approve, as the review screen shows it.</summary>
/// <param name="Kind">The content kind for a draft; null for the listing.</param>
/// <param name="ContentHash">The hash of exactly what is shown — what the approve call sends back (FR-066).</param>
/// <param name="ApprovalId">The live approval of this content, when there is one.</param>
internal sealed record ApprovalOutputDto(
    string TargetType,
    Guid TargetId,
    string? Kind,
    string ContentHash,
    bool Approved,
    Guid? ApprovalId,
    IReadOnlyList<BlockerDto> Blockers);

internal sealed record BlockerDto(string Code, string Detail, Guid? TargetId, string? FieldKey = null);

/// <summary>
/// T147 — the approval gate's view of a listing: each approvable output, its hash, and its
/// blockers (FR-014, FR-022, FR-026, SC-011).
///
/// <para>
/// <b>Outputs.</b> The draft chosen for publication of each kind, and the listing itself — its
/// facts and photos — which is approved last, once the copy it carries is.
/// </para>
/// <para>
/// <b>The listing's hash</b> is SHA-256 over a canonical rendering of what the review screen shows
/// of it: the basic details, every field with its value, provenance and confirmation, and every
/// photo with its order, cover, caption, room type, features and alteration declaration. The
/// agent's private notes are not part of it; they are never published. A draft's hash is its
/// body's.
/// </para>
/// </summary>
internal static class ApprovalService
{
    internal const string Channel = "mls";

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The listing's review hash, as it is now.</summary>
    internal static async Task<string> ReviewHashAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken)
    {
        var listing = await context.Listings.AsNoTracking().SingleAsync(candidate => candidate.Id == listingId, cancellationToken);
        var fields = await context.ListingFields.AsNoTracking().Where(field => field.ListingId == listingId).ToListAsync(cancellationToken);
        var photos = await context.Photos.AsNoTracking().Where(photo => photo.ListingId == listingId).OrderBy(photo => photo.OrderIndex).ToListAsync(cancellationToken);

        var document = new JsonObject
        {
            ["basics"] = new JsonObject
            {
                ["addressLine1"] = listing.AddressLine1,
                ["city"] = listing.City,
                ["state"] = listing.State.Trim(),
                ["postalCode"] = listing.PostalCode,
                ["propertyType"] = listing.PropertyType,
                ["listPrice"] = listing.ListPrice,
                ["bedrooms"] = listing.Bedrooms,
                ["bathrooms"] = listing.Bathrooms,
            },
            ["fields"] = new JsonArray(fields
                .OrderBy(field => field.ResoFieldKey, StringComparer.Ordinal)
                .Select(field => (JsonNode)new JsonObject
                {
                    ["key"] = field.ResoFieldKey,
                    ["value"] = JsonNode.Parse(field.Value),
                    ["provenance"] = ListingFieldService.Format(field.Provenance),
                    ["confirmed"] = field.Confirmed,
                })
                .ToArray()),
            ["photos"] = new JsonArray(photos
                .Select(photo => (JsonNode)new JsonObject
                {
                    ["id"] = photo.Id.ToString("D"),
                    ["checksum"] = photo.ChecksumSha256,
                    ["order"] = photo.OrderIndex,
                    ["cover"] = photo.IsCover,
                    ["caption"] = photo.Caption,
                    ["roomType"] = photo.RoomTypeOverride is { } room ? ListingRepresentation.PostgresLabel(room) : null,
                    ["featureTags"] = photo.FeatureTagsOverride is { } tags ? JsonNode.Parse(tags) : null,
                    ["altered"] = photo.Altered,
                    ["alterationType"] = photo.AlterationType is { } type ? ListingRepresentation.PostgresLabel(type) : null,
                    ["disclosureLabel"] = photo.DisclosureLabel,
                    ["pairedOriginalId"] = photo.PairedOriginalId?.ToString("D"),
                })
                .ToArray()),
        };

        return Hash(document.ToJsonString());
    }

    /// <summary>Every approvable output of the listing, with what blocks each.</summary>
    internal static async Task<IReadOnlyList<ApprovalOutputDto>> OutputsAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken)
    {
        var listing = await context.Listings.AsNoTracking().SingleAsync(candidate => candidate.Id == listingId, cancellationToken);
        var (facts, _) = await ContentRepresentation.LoadContextAsync(context, listingId, cancellationToken);
        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken);

        var selected = await context.ContentDrafts.AsNoTracking()
            .Where(draft => draft.ListingId == listingId && draft.Selected && draft.Status != ContentStatus.Stale)
            .OrderBy(draft => draft.Kind)
            .ToListAsync(cancellationToken);

        var checks = await context.ComplianceChecks.AsNoTracking()
            .Where(check => check.ListingId == listingId)
            .ToDictionaryAsync(check => check.TargetId, check => check.ContentHash, cancellationToken);

        var approvals = await context.Approvals.AsNoTracking()
            .Where(approval => approval.ListingId == listingId && approval.RevokedAt == null && approval.Channel == Channel)
            .ToListAsync(cancellationToken);

        var openBlocking = await context.ComplianceFindings.AsNoTracking()
            .Where(finding => finding.ListingId == listingId && finding.Status == FindingStatus.Open && finding.Severity == FindingSeverity.Blocking)
            .Select(finding => new GateFinding(finding.Id, finding.TargetType, finding.TargetId, finding.RuleId, finding.Reason))
            .ToListAsync(cancellationToken);

        var running = await context.PipelineRuns.AsNoTracking().AnyAsync(run => run.ListingId == listingId && run.FinishedAt == null, cancellationToken);

        var fields = await context.ListingFields.AsNoTracking().Where(field => field.ListingId == listingId).ToListAsync(cancellationToken);
        var present = fields.Select(field => field.ResoFieldKey).ToHashSet(StringComparer.Ordinal);
        var missing = ListingRepresentation.RequiredKeys(profile).Where(key => !present.Contains(key)).Order(StringComparer.Ordinal).ToList();

        Approval? Live(Guid targetId, string hash) => approvals.FirstOrDefault(approval => approval.TargetId == targetId && approval.ContentHash == hash);

        var drafts = selected
            .Select(draft =>
            {
                var hash = Hash(draft.Body);
                var unconfirmed = GroundingService.UnconfirmedClaims(ClaimStorage.Parse(draft.SupportingFieldIds), facts)
                    .Select(claim => claim.FieldId)
                    .Distinct()
                    .Select(id => (id, facts[id].Key))
                    .ToList();

                return (Entity: draft, Subject: new DraftSubject(
                    draft.Id,
                    ContentRepresentation.Label(draft.Kind),
                    hash,
                    checks.GetValueOrDefault(draft.Id),
                    draft.Status == ContentStatus.Approved && Live(draft.Id, hash) is not null,
                    unconfirmed));
            })
            .ToList();

        var outputs = drafts
            .Select(draft =>
            {
                var blockers = draft.Subject.Approved ? [] : ApprovalGate.ForDraft(draft.Subject, openBlocking, running);

                return new ApprovalOutputDto(
                    ContentRepresentation.DraftTarget,
                    draft.Subject.Id,
                    draft.Subject.Kind,
                    draft.Subject.Hash,
                    draft.Subject.Approved,
                    draft.Subject.Approved ? Live(draft.Subject.Id, draft.Subject.Hash)!.Id : null,
                    blockers.Select(ToDto).ToList());
            })
            .ToList();

        var listingHash = await ReviewHashAsync(context, listingId, cancellationToken);
        var listingApproval = listing.Status == ListingStatus.Approved ? Live(listingId, listingHash) : null;

        var listingSubject = new ListingSubject(
            listingId,
            listingHash,
            checks.GetValueOrDefault(listingId),
            drafts.Select(draft => draft.Subject).ToList(),
            fields
                .Where(field => ListingFieldService.RequiresConfirmation(field.Provenance) && !field.Confirmed)
                .OrderBy(field => field.ResoFieldKey, StringComparer.Ordinal)
                .Select(field => (field.Id, field.ResoFieldKey))
                .ToList(),
            missing);

        outputs.Add(new ApprovalOutputDto(
            "listing",
            listingId,
            null,
            listingHash,
            listingApproval is not null,
            listingApproval?.Id,
            listingApproval is not null ? [] : ApprovalGate.ForListing(listingSubject, openBlocking, running).Select(ToDto).ToList()));

        return outputs;
    }

    private static BlockerDto ToDto(Blocker blocker) => new(blocker.Code, blocker.Detail, blocker.TargetId, blocker.FieldKey);
}
