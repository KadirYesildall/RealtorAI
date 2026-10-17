using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Data.Entities;
using Api.Endpoints;
using Libs.Ai;
using Libs.Ai.Tasks;
using Libs.Reso;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Content;
using Modules.Listings;
using Pipeline;

namespace Api.Pipeline;

/// <summary>
/// Content generation's persistence over the EF model (research R20, R35).
///
/// <para>
/// <b>A new version supersedes, it does not overwrite.</b> Every generation is stored as the
/// kind's next version, and the drafts it replaces become <c>stale</c>. They stay on record,
/// because an approval or an audit event may cite them. An approval of a superseded draft is
/// revoked in the same transaction, and an approved listing returns to review. Otherwise the
/// listing would be approved while the content it shows is content nobody approved (FR-025).
/// </para>
/// </summary>
internal sealed class EfContentStore : IContentStore
{
    private const string SupersededReason = "superseded_by_regeneration";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext context;
    private readonly IAuditWriter audit;

    public EfContentStore(AppDbContext context, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);

        this.context = context;
        this.audit = audit;
    }

    public async Task<ContentSources> LoadSourcesAsync(Guid listingId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();

        var listing = await context.Listings.AsNoTracking().SingleAsync(candidate => candidate.Id == listingId, cancellationToken);

        var facts = (await context.ListingFields.AsNoTracking()
                .Where(field => field.ListingId == listingId)
                .OrderBy(field => field.ResoFieldKey)
                .ToListAsync(cancellationToken))
            .Select(field => new ContentFact(field.Id, field.ResoFieldKey, JsonNode.Parse(field.Value), ListingFieldService.Format(field.Provenance), field.Confirmed))
            .ToList();

        var photos = await context.Photos.AsNoTracking()
            .Where(photo => photo.ListingId == listingId)
            .OrderBy(photo => photo.OrderIndex)
            .GroupJoin(
                context.PhotoAnalyses.AsNoTracking().Where(analysis => !analysis.Failed && analysis.ModelId != null),
                photo => photo.Id,
                analysis => analysis.PhotoId,
                (photo, analyses) => new { photo, analyses })
            .SelectMany(row => row.analyses.DefaultIfEmpty(), (row, analysis) => new
            {
                Room = row.photo.RoomTypeOverride ?? (analysis == null ? null : analysis.RoomType),
                Tags = row.photo.FeatureTagsOverride ?? (analysis == null ? null : analysis.FeatureTags),
            })
            .ToListAsync(cancellationToken);

        var observations = photos
            .Where(photo => photo.Room is not null)
            .Select(photo => new PhotoObservation(
                ListingRepresentation.PostgresLabel(photo.Room!.Value),
                photo.Tags is null ? [] : ListingRepresentation.Strings(photo.Tags)))
            .ToList();

        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken);

        var defaultTone = listing.BrokerageId is { } brokerageId
            ? await context.BrokerageSettings.AsNoTracking()
                .Where(settings => settings.BrokerageId == brokerageId)
                .Select(settings => (Tone?)settings.DefaultTone)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return new ContentSources(
            new ContentBasics(listing.City, listing.State, listing.PropertyType, listing.ListPrice, listing.Bedrooms, listing.Bathrooms, listing.AgentNotes),
            facts,
            observations,
            profile is null ? null : ContentRepresentation.ToContentProfile(profile),
            profile is null ? ResoLookupMapping.None : ResoLookupMapping.Parse(profile.EnumeratedFieldLookups),
            ContentRepresentation.Label(defaultTone ?? Tone.Professional));
    }

    public async Task SaveStructuredAsync(
        PipelineRunContext run,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        AiResult aiCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(aiCall);

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var brokerageId = await BrokerageOfAsync(run.ListingId, cancellationToken);

        foreach (var (key, proposed) in values)
        {
            var json = JsonSerializer.Serialize(proposed);
            var field = await context.ListingFields.SingleOrDefaultAsync(
                candidate => candidate.ListingId == run.ListingId && candidate.ResoFieldKey == key,
                cancellationToken);

            // Never over what the agent entered, what a record says, or what the agent confirmed.
            if (field is not null && (!ListingFieldService.RequiresConfirmation(field.Provenance) || field.Confirmed))
            {
                continue;
            }

            if (field is not null && JsonNode.DeepEquals(JsonNode.Parse(field.Value), JsonNode.Parse(json)))
            {
                continue;
            }

            var before = field is null ? null : Snapshot(field);

            if (field is null)
            {
                field = new ListingField { Id = Guid.CreateVersion7(), ListingId = run.ListingId, ResoFieldKey = key };
                context.ListingFields.Add(field);
            }
            else
            {
                field.RowVersion++;
            }

            field.Value = json;
            field.Provenance = Provenance.AiDetectedFromPhoto;
            field.Confirmed = false;
            await context.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.FieldEdit,
                    ActorId = run.StartedBy,
                    ListingId = run.ListingId,
                    BrokerageId = brokerageId,
                    TargetType = ListingFieldEndpoints.FieldTarget,
                    TargetId = field.Id,
                    BeforeValue = before,
                    AfterValue = Snapshot(field),
                },
                cancellationToken);
        }

        await AuditCallAsync(run, brokerageId, aiCall, new JsonObject { ["mode"] = "structured", ["fields"] = values.Count }, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveGenerationAsync(PipelineRunContext run, KindGeneration generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(generation);

        var kind = ContentRepresentation.ParseKind(generation.Kind)
            ?? throw new ArgumentException($"'{generation.Kind}' is not a content kind.", nameof(generation));
        var tone = ContentRepresentation.ParseTone(generation.Tone)
            ?? throw new ArgumentException($"'{generation.Tone}' is not a tone.", nameof(generation));

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var brokerageId = await BrokerageOfAsync(run.ListingId, cancellationToken);

        var current = await context.ContentDrafts
            .Where(draft => draft.ListingId == run.ListingId && draft.Kind == kind && draft.Status != ContentStatus.Stale)
            .ToListAsync(cancellationToken);

        var version = (await context.ContentDrafts
            .Where(draft => draft.ListingId == run.ListingId && draft.Kind == kind)
            .MaxAsync(draft => (int?)draft.Version, cancellationToken) ?? 0) + 1;

        // Stale and unselected first: one selected draft per kind is a unique index, and the new
        // version's selection must not meet the old one's.
        foreach (var draft in current)
        {
            draft.Status = ContentStatus.Stale;
            draft.Selected = false;
            draft.RowVersion++;
        }

        await context.SaveChangesAsync(cancellationToken);

        var supersededIds = current.Select(draft => draft.Id).ToList();
        var revoked = await context.Approvals
            .Where(approval => approval.ListingId == run.ListingId && supersededIds.Contains(approval.TargetId) && approval.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var approval in revoked)
        {
            approval.RevokedAt = DateTimeOffset.UtcNow;
            approval.RevokedReason = SupersededReason;

            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.ApprovalRevoked,
                    ActorId = run.StartedBy,
                    ListingId = run.ListingId,
                    BrokerageId = brokerageId,
                    TargetType = ContentRepresentation.DraftTarget,
                    TargetId = approval.TargetId,
                    AfterValue = JsonSerializer.Serialize(new { reason = SupersededReason, approvalId = approval.Id }, Web),
                },
                cancellationToken);
        }

        if (revoked.Count > 0)
        {
            await context.Listings
                .Where(listing => listing.Id == run.ListingId && listing.Status == ListingStatus.Approved)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(listing => listing.Status, ListingLifecycle.AfterEdit(ListingStatus.Approved)),
                    cancellationToken);
        }

        foreach (var generated in generation.Drafts)
        {
            context.ContentDrafts.Add(new ContentDraft
            {
                Id = Guid.CreateVersion7(),
                ListingId = run.ListingId,
                Kind = kind,
                VariantIndex = generated.VariantIndex,
                Tone = tone,
                Body = generated.Body,
                SupportingFieldIds = ClaimStorage.Serialize(generated.Claims),
                Version = version,
                Status = ContentStatus.Draft,
                Selected = ContentRules.SelectedOnArrival(generation.Kind) && generated.VariantIndex == 0,
                ModelId = generation.Call.ModelId,
                PromptVersion = generation.Call.PromptVersion.ToString(CultureInfo.InvariantCulture),
                PromptHash = generation.Call.PromptHash,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        var summary = new JsonObject
        {
            ["kind"] = generation.Kind,
            ["version"] = version,
            ["tone"] = generation.Tone,
            ["variants"] = generation.Drafts.Count,
            ["characterLimit"] = generation.CharacterLimit,
            ["ungroundedSentencesRemoved"] = generation.RemovedSentences,
            ["overLimitVariantsDropped"] = generation.OverLimitVariants,
        };

        await AuditCallAsync(run, brokerageId, generation.Call, new JsonObject { ["mode"] = "text", ["kind"] = generation.Kind, ["version"] = version }, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.ContentGenerated,
                ActorId = run.StartedBy,
                ListingId = run.ListingId,
                BrokerageId = brokerageId,
                TargetType = "listing",
                TargetId = run.ListingId,
                AfterValue = summary.ToJsonString(),
                ModelId = generation.Call.ModelId,
                PromptVersion = generation.Call.PromptVersion.ToString(CultureInfo.InvariantCulture),
                PromptHash = generation.Call.PromptHash,
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AuditUnusedCallAsync(PipelineRunContext run, string kind, AiResult aiCall, string outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(aiCall);

        context.ChangeTracker.Clear();

        var brokerageId = await BrokerageOfAsync(run.ListingId, cancellationToken);

        await AuditCallAsync(run, brokerageId, aiCall, new JsonObject { ["mode"] = "text", ["kind"] = kind, ["outcome"] = outcome }, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private Task AuditCallAsync(PipelineRunContext run, Guid? brokerageId, AiResult call, JsonObject detail, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.AiCall,
                ActorId = run.StartedBy,
                ListingId = run.ListingId,
                BrokerageId = brokerageId,
                TargetType = "listing",
                TargetId = run.ListingId,
                AfterValue = detail.ToJsonString(),
                ModelId = call.ModelId,
                PromptVersion = call.PromptVersion.ToString(CultureInfo.InvariantCulture),
                PromptHash = call.PromptHash,
                InputRef = $"pipeline_runs/{run.RunId:D}",
            },
            cancellationToken);

    private Task<Guid?> BrokerageOfAsync(Guid listingId, CancellationToken cancellationToken) =>
        context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.BrokerageId)
            .SingleAsync(cancellationToken);

    private static string Snapshot(ListingField field) =>
        JsonSerializer.Serialize(
            new
            {
                resoFieldKey = field.ResoFieldKey,
                value = JsonNode.Parse(field.Value),
                provenance = ListingFieldService.Format(field.Provenance),
                confirmed = field.Confirmed,
            },
            Web);
}
