using System.Security.Cryptography;
using System.Text;
using Api.Data;
using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Modules.Content;
using Modules.Listings;

namespace Api.Endpoints;

internal sealed record UnconfirmedClaimDto(int SpanStart, int SpanEnd, Guid FieldId);

/// <summary>The contract's <c>ContentDraft</c>.</summary>
/// <param name="Version">The generation set this draft belongs to; edits are counted by <paramref name="RowVersion"/>.</param>
/// <param name="CharacterLimit">The MLS's limit for this kind, or null when the profile no longer has a verified one.</param>
/// <param name="ContentHash">SHA-256 of the body's UTF-8 bytes, submitted as reviewedContentHash when approving (FR-066).</param>
internal sealed record ContentDraftDto(
    Guid Id,
    string Kind,
    int VariantIndex,
    string? Tone,
    string Body,
    int Version,
    string Status,
    bool Selected,
    IReadOnlyList<Guid> SupportingFieldIds,
    IReadOnlyList<UnconfirmedClaimDto> UnconfirmedClaims,
    int CharacterCount,
    int? CharacterLimit,
    string ContentHash,
    int RowVersion);

/// <summary>Drafts as the contract shows them, and the spellings content shares with the pipeline.</summary>
internal static class ContentRepresentation
{
    internal const string DraftTarget = "content_draft";

    internal static string Label(ContentKind kind) => ListingRepresentation.PostgresLabel(kind);

    internal static string Label(Tone tone) => ListingRepresentation.PostgresLabel(tone);

    internal static ContentKind? ParseKind(string? kind) => Parse<ContentKind>(kind);

    internal static Tone? ParseTone(string? tone) => Parse<Tone>(tone);

    internal static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>The listing's current drafts — every kind's newest version — in kind and variant order.</summary>
    internal static async Task<IReadOnlyList<ContentDraftDto>> LoadCurrentAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken)
    {
        var drafts = await context.ContentDrafts.AsNoTracking()
            .Where(draft => draft.ListingId == listingId && draft.Status != ContentStatus.Stale)
            .OrderBy(draft => draft.Kind)
            .ThenBy(draft => draft.VariantIndex)
            .ToListAsync(cancellationToken);

        if (drafts.Count == 0)
        {
            return [];
        }

        var (facts, profile) = await LoadContextAsync(context, listingId, cancellationToken);

        return drafts.Select(draft => ToDto(draft, facts, profile)).ToList();
    }

    internal static async Task<ContentDraftDto> LoadOneAsync(AppDbContext context, ContentDraft draft, CancellationToken cancellationToken)
    {
        var (facts, profile) = await LoadContextAsync(context, draft.ListingId, cancellationToken);

        return ToDto(draft, facts, profile);
    }

    /// <summary>The facts as grounding judges them now, and the listing's MLS profile.</summary>
    internal static async Task<(IReadOnlyDictionary<Guid, GroundingFact> Facts, ContentProfile? Profile)> LoadContextAsync(
        AppDbContext context,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var fields = await context.ListingFields.AsNoTracking()
            .Where(field => field.ListingId == listingId)
            .ToListAsync(cancellationToken);

        var facts = fields.ToDictionary(
            field => field.Id,
            field => new GroundingFact(
                field.Id,
                field.ResoFieldKey,
                System.Text.Json.Nodes.JsonNode.Parse(field.Value),
                Sourced: field.Provenance is Provenance.AgentEntered or Provenance.PublicRecord,
                NeedsConfirmation: ListingFieldService.RequiresConfirmation(field.Provenance) && !field.Confirmed));

        var targetMlsId = await context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.TargetMlsId)
            .SingleAsync(cancellationToken);

        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, targetMlsId, cancellationToken);

        return (facts, profile is null ? null : ToContentProfile(profile));
    }

    internal static ContentProfile ToContentProfile(MlsRuleProfile profile) =>
        new(profile.VerifiedAt is not null, profile.PublicRemarksMaxChars, profile.PrivateRemarksMaxChars);

    private static ContentDraftDto ToDto(ContentDraft draft, IReadOnlyDictionary<Guid, GroundingFact> facts, ContentProfile? profile)
    {
        var claims = ClaimStorage.Parse(draft.SupportingFieldIds);
        var kind = Label(draft.Kind);

        return new ContentDraftDto(
            draft.Id,
            kind,
            draft.VariantIndex,
            draft.Tone is { } tone ? Label(tone) : null,
            draft.Body,
            draft.Version,
            ListingRepresentation.PostgresLabel(draft.Status),
            draft.Selected,
            GroundingService.SupportingFieldIds(claims),
            GroundingService.UnconfirmedClaims(claims, facts).Select(claim => new UnconfirmedClaimDto(claim.SpanStart, claim.SpanEnd, claim.FieldId)).ToList(),
            draft.Body.Length,
            ContentRules.LimitFor(kind, profile),
            Hash(draft.Body),
            draft.RowVersion);
    }

    private static TEnum? Parse<TEnum>(string? label)
        where TEnum : struct, Enum =>
        label is null
            ? null
            : Enum.GetValues<TEnum>().Cast<TEnum?>().FirstOrDefault(value => ListingRepresentation.PostgresLabel(value!.Value) == label);
}
