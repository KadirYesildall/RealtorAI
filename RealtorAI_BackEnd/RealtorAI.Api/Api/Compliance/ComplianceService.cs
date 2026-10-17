using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Data.Entities;
using Api.Endpoints;
using Libs.Ai;
using Libs.Compliance;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Listings;
using Pipeline;
using DataCheckType = Api.Data.CheckType;
using DataSource = Api.Data.FindingSource;
using Finding = Libs.Compliance.Finding;
using FindingSource = Libs.Compliance.FindingSource;
using Severity = Libs.Compliance.Severity;

namespace Api.Compliance;

/// <summary>Compliance settings.</summary>
/// <param name="EstimatedTokensPerJudgeCall">The pre-dispatch budget estimate for one judge call (FR-055).</param>
public sealed record ComplianceOptions(int EstimatedTokensPerJudgeCall);

/// <summary>
/// T135 — runs the compliance checks over a listing or one of its items and stores what they find
/// (FR-017–FR-025; research R36).
///
/// <para>
/// <b>What is checked where.</b> Each current content draft gets the lexicon and the judge
/// (merged), the risky-claim check, and the MLS remarks rules. Each photo gets the integrity check
/// and the MLS resolution rule. The listing gets the MLS listing rules, which fail closed on an
/// incomplete profile, the advertising disclosure, and — when the judge could not run for some
/// draft — a warning saying the contextual review did not happen.
/// </para>
/// <para>
/// <b>Stored as a difference.</b> A finding is the same finding on a re-check when its rule,
/// layer, span, phrase and reason match. That one is kept as it is, so an acknowledgement
/// survives a check of unchanged words. One no longer found is resolved, and a new one is raised
/// and audited. Writes for a listing are serialised on the listing's row, so a check started by
/// a run and one started by an edit cannot both insert the same finding.
/// </para>
/// </summary>
internal sealed class ComplianceService : IComplianceRunner
{
    internal const string JudgeUnavailable = "fair_housing.judge_unavailable";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext context;
    private readonly IAuditWriter audit;
    private readonly IAiService ai;
    private readonly ComplianceOptions options;

    /// <summary>Which model and prompt judged each draft in this check, for the findings it raised (FR-024).</summary>
    private readonly Dictionary<Guid, (string ModelId, string PromptVersion)> judgedRecord = [];

    public ComplianceService(AppDbContext context, IAuditWriter audit, IAiService ai, ComplianceOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(options);

        this.context = context;
        this.audit = audit;
        this.ai = ai;
        this.options = options;
    }

    public Task CheckAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.ComplianceTargetType switch
        {
            "content_draft" => CheckDraftAsync(run.ListingId, run.ComplianceTargetId!.Value, run.StartedBy, cancellationToken),
            "photo" => CheckPhotosAsync(run.ListingId, run.StartedBy, run.ComplianceTargetId, cancellationToken),
            _ => CheckListingAsync(run.ListingId, run.StartedBy, cancellationToken),
        };
    }

    /// <summary>The whole listing: itself, every current draft, every photo.</summary>
    internal async Task CheckListingAsync(Guid listingId, Guid actorId, CancellationToken cancellationToken)
    {
        var sources = await LoadAsync(listingId, cancellationToken);
        var judgeMissed = false;
        var results = new List<(string TargetType, Guid TargetId, IReadOnlyList<Finding> Findings)>();

        foreach (var draft in sources.Drafts)
        {
            var (findings, judged) = await CheckTextAsync(sources, draft, actorId, cancellationToken);
            judgeMissed |= !judged;
            results.Add((ContentRepresentation.DraftTarget, draft.Id, findings));
        }

        results.AddRange(CheckPhotos(sources, onlyPhotoId: null));

        var listingFindings = MlsRuleEngine.CheckListing(sources.Rules, sources.Fields, sources.Photos.Count)
            .Concat(DisclosureChecker.Check(sources.BrokerageName, sources.BrokerageLicense, sources.AgentLicense))
            .ToList();

        if (judgeMissed)
        {
            listingFindings.Add(new Finding(
                JudgeUnavailable,
                Libs.Compliance.CheckType.FairHousing,
                Severity.Warning,
                FindingSource.Deterministic,
                "The contextual Fair Housing review did not run — no model is available for it, the listing's AI budget is spent, or its answer was unusable. The phrase list was checked; read the copy for indirect wording yourself."));
        }

        results.Add(("listing", listingId, listingFindings));

        await StoreAsync(listingId, actorId, results, cancellationToken);
    }

    /// <summary>One draft, after an edit or a rewrite (FR-025). A stale or missing draft has nothing to check.</summary>
    internal async Task CheckDraftAsync(Guid listingId, Guid draftId, Guid actorId, CancellationToken cancellationToken)
    {
        var sources = await LoadAsync(listingId, cancellationToken);

        if (sources.Drafts.FirstOrDefault(draft => draft.Id == draftId) is not { } draft)
        {
            return;
        }

        var (findings, _) = await CheckTextAsync(sources, draft, actorId, cancellationToken);

        await StoreAsync(listingId, actorId, [(ContentRepresentation.DraftTarget, draft.Id, findings)], cancellationToken);
    }

    /// <summary>One photo, or every photo when <paramref name="photoId"/> is null — an order change moves adjacency for all.</summary>
    internal async Task CheckPhotosAsync(Guid listingId, Guid actorId, Guid? photoId, CancellationToken cancellationToken)
    {
        var sources = await LoadAsync(listingId, cancellationToken);

        await StoreAsync(listingId, actorId, CheckPhotos(sources, photoId).ToList(), cancellationToken);
    }

    /// <summary>Marks every live finding of a target stale: the item is gone or superseded.</summary>
    internal static Task MarkStaleAsync(AppDbContext context, string targetType, IReadOnlyCollection<Guid> targetIds, CancellationToken cancellationToken) =>
        context.ComplianceFindings
            .Where(finding => finding.TargetType == targetType && targetIds.Contains(finding.TargetId)
                && (finding.Status == FindingStatus.Open || finding.Status == FindingStatus.Acknowledged))
            .ExecuteUpdateAsync(setters => setters.SetProperty(finding => finding.Status, FindingStatus.Stale), cancellationToken);

    // ═════════════════════════════ Checks ═════════════════════════════

    private async Task<(IReadOnlyList<Finding> Findings, bool Judged)> CheckTextAsync(
        Sources sources,
        ContentDraft draft,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var kind = ContentRepresentation.Label(draft.Kind);
        var lexicon = LexiconMatcher.FairHousing.Match(draft.Body);
        IReadOnlyList<Finding> judged = [];
        var judgedOk = false;

        try
        {
            var outcome = await ai.JudgeAsync(sources.ListingId, actorId, kind, draft.Body, options.EstimatedTokensPerJudgeCall, cancellationToken);
            judged = outcome.Findings;
            judgedOk = true;

            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.AiCall,
                    ActorId = actorId,
                    ListingId = sources.ListingId,
                    BrokerageId = sources.BrokerageId,
                    TargetType = ContentRepresentation.DraftTarget,
                    TargetId = draft.Id,
                    AfterValue = new JsonObject { ["task"] = AiTaskKey.ComplianceJudge, ["findings"] = judged.Count, ["fromCache"] = outcome.Call.FromCache }.ToJsonString(),
                    ModelId = outcome.Call.ModelId,
                    PromptVersion = outcome.Call.PromptVersion.ToString(CultureInfo.InvariantCulture),
                    PromptHash = outcome.Call.PromptHash,
                    InputRef = $"content_drafts/{draft.Id:D}",
                },
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            judgedRecord[draft.Id] = (outcome.Call.ModelId, outcome.Call.PromptVersion.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is ModelUnavailableException or AiBudgetExceededException or StructuredOutputException)
        {
            // The deterministic layers stand on their own; the listing-level finding says the
            // contextual review is missing.
        }

        var findings = FindingMerger.Merge(lexicon, judged)
            .Concat(RiskyClaimChecker.Check(draft.Body, sources.SourcedFigures))
            .Concat(MlsRuleEngine.CheckRemarks(kind, draft.Body, sources.Rules, sources.Names))
            .ToList();

        return (findings, judgedOk);
    }

    private static IEnumerable<(string TargetType, Guid TargetId, IReadOnlyList<Finding> Findings)> CheckPhotos(Sources sources, Guid? onlyPhotoId)
    {
        foreach (var photo in sources.Photos.Where(photo => onlyPhotoId is null || photo.Facts.Id == onlyPhotoId))
        {
            var findings = PhotoIntegrityChecker.Check(photo.Facts, sources.Photos.Select(candidate => candidate.Facts).ToList(), sources.Rules)
                .Concat(MlsRuleEngine.CheckPhoto(photo.Width, photo.Height, sources.Rules))
                .ToList();

            yield return ("photo", photo.Facts.Id, findings);
        }
    }

    // ═════════════════════════════ Storing ═════════════════════════════

    private async Task StoreAsync(
        Guid listingId,
        Guid actorId,
        IReadOnlyList<(string TargetType, Guid TargetId, IReadOnlyList<Finding> Findings)> results,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // One writer of a listing's findings at a time.
        await context.Database.ExecuteSqlAsync($"select 1 from public.listings where id = {listingId} for update", cancellationToken);

        var brokerageId = await context.Listings.AsNoTracking().Where(listing => listing.Id == listingId).Select(listing => listing.BrokerageId).SingleAsync(cancellationToken);

        foreach (var (targetType, targetId, findings) in results)
        {
            var existing = await context.ComplianceFindings
                .Where(finding => finding.ListingId == listingId && finding.TargetType == targetType && finding.TargetId == targetId
                    && (finding.Status == FindingStatus.Open || finding.Status == FindingStatus.Acknowledged))
                .ToListAsync(cancellationToken);

            var unmatched = existing.ToList();

            foreach (var finding in findings)
            {
                var same = unmatched.FirstOrDefault(row => Same(row, finding));

                if (same is not null)
                {
                    unmatched.Remove(same);
                    continue;
                }

                var row = new ComplianceFinding
                {
                    Id = Guid.CreateVersion7(),
                    ListingId = listingId,
                    TargetType = targetType,
                    TargetId = targetId,
                    RuleId = finding.RuleId,
                    CheckType = Enum.Parse<DataCheckType>(finding.CheckType.ToString()),
                    Severity = finding.Severity == Severity.Blocking ? FindingSeverity.Blocking : FindingSeverity.Warning,
                    Source = Enum.Parse<DataSource>(finding.Source.ToString()),
                    SpanStart = finding.SpanStart,
                    SpanEnd = finding.SpanEnd,
                    Phrase = finding.Phrase,
                    Reason = finding.Reason,
                    SuggestedFix = finding.SuggestedFix,
                    Status = FindingStatus.Open,
                    LexiconVersion = finding.Source == FindingSource.Lexicon ? LexiconMatcher.FairHousing.Version : null,
                    ModelId = finding.Source == FindingSource.Judge && judgedRecord.TryGetValue(targetId, out var judge) ? judge.ModelId : null,
                    PromptVersion = finding.Source == FindingSource.Judge && judgedRecord.TryGetValue(targetId, out var prompt) ? prompt.PromptVersion : null,
                };

                context.ComplianceFindings.Add(row);
                await context.SaveChangesAsync(cancellationToken);

                await audit.WriteAsync(
                    new AuditEntry
                    {
                        Action = AuditAction.FindingRaised,
                        ActorId = actorId,
                        ListingId = listingId,
                        BrokerageId = brokerageId,
                        TargetType = "compliance_finding",
                        TargetId = row.Id,
                        AfterValue = JsonSerializer.Serialize(
                            new
                            {
                                ruleId = row.RuleId,
                                severity = ListingRepresentation.PostgresLabel(row.Severity),
                                source = ListingRepresentation.PostgresLabel(row.Source),
                                targetType,
                                targetId,
                                phrase = row.Phrase,
                            },
                            Web),
                    },
                    cancellationToken);
            }

            // What was found before and is not found now has been dealt with.
            foreach (var gone in unmatched)
            {
                gone.Status = FindingStatus.Resolved;
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool Same(ComplianceFinding row, Finding finding) =>
        row.RuleId == finding.RuleId
        && row.Source == Enum.Parse<DataSource>(finding.Source.ToString())
        && row.SpanStart == finding.SpanStart
        && row.SpanEnd == finding.SpanEnd
        && row.Phrase == finding.Phrase
        && row.Reason == finding.Reason;

    // ═════════════════════════════ Loading ═════════════════════════════

    private sealed record PhotoUnderCheck(PhotoFacts Facts, int? Width, int? Height);

    private sealed record Sources(
        Guid ListingId,
        Guid? BrokerageId,
        MlsRules? Rules,
        RemarksNames Names,
        IReadOnlyCollection<(string Key, bool Confirmed)> Fields,
        IReadOnlyCollection<decimal> SourcedFigures,
        IReadOnlyList<ContentDraft> Drafts,
        IReadOnlyList<PhotoUnderCheck> Photos,
        string? BrokerageName,
        string? BrokerageLicense,
        string? AgentLicense);

    private async Task<Sources> LoadAsync(Guid listingId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();

        var listing = await context.Listings.AsNoTracking().SingleAsync(candidate => candidate.Id == listingId, cancellationToken);
        var agent = await context.Profiles.AsNoTracking().SingleAsync(profile => profile.Id == listing.AgentId, cancellationToken);
        var brokerage = listing.BrokerageId is { } brokerageId
            ? await context.Brokerages.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == brokerageId, cancellationToken)
            : null;
        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken);

        var fields = await context.ListingFields.AsNoTracking().Where(field => field.ListingId == listingId).ToListAsync(cancellationToken);

        var drafts = await context.ContentDrafts.AsNoTracking()
            .Where(draft => draft.ListingId == listingId && draft.Status != ContentStatus.Stale)
            .OrderBy(draft => draft.Kind)
            .ThenBy(draft => draft.VariantIndex)
            .ToListAsync(cancellationToken);

        var photos = await context.Photos.AsNoTracking()
            .Where(photo => photo.ListingId == listingId)
            .OrderBy(photo => photo.OrderIndex)
            .GroupJoin(
                context.PhotoAnalyses.AsNoTracking().Where(analysis => !analysis.Failed && analysis.ModelId != null),
                photo => photo.Id,
                analysis => analysis.PhotoId,
                (photo, analyses) => new { photo, analyses })
            .SelectMany(row => row.analyses.DefaultIfEmpty(), (row, analysis) => new { row.photo, Flags = analysis == null ? null : analysis.ContentFlags })
            .ToListAsync(cancellationToken);

        return new Sources(
            listingId,
            listing.BrokerageId,
            profile is null ? null : ToRules(profile),
            new RemarksNames(
                agent.FullName is { Length: > 0 } name ? [name] : [],
                brokerage is null ? [] : [brokerage.Name]),
            fields.Select(field => (field.ResoFieldKey, field.Confirmed || !ListingFieldService.RequiresConfirmation(field.Provenance))).ToList(),
            fields
                .Where(field => field.Provenance is Provenance.AgentEntered or Provenance.PublicRecord)
                .SelectMany(field => Figures(JsonNode.Parse(field.Value)))
                .ToHashSet(),
            drafts,
            photos
                .Select(row => new PhotoUnderCheck(
                    new PhotoFacts(
                        row.photo.Id,
                        row.photo.OrderIndex,
                        row.photo.Altered,
                        row.photo.AlterationType is { } type ? ListingRepresentation.PostgresLabel(type) : null,
                        row.photo.DisclosureLabel,
                        row.photo.PairedOriginalId,
                        row.Flags is null ? [] : ListingRepresentation.Strings(row.Flags)),
                    row.photo.Width,
                    row.photo.Height))
                .ToList(),
            brokerage?.Name,
            brokerage?.LicenseNumber,
            agent.LicenseNumber);
    }

    /// <summary>The profile as the checks read it. A profile whose rule data does not parse is treated as absent: it fails closed.</summary>
    private static MlsRules? ToRules(MlsRuleProfile profile)
    {
        try
        {
            return new MlsRules(
                profile.VerifiedAt is not null,
                profile.PublicRemarksMaxChars,
                profile.PrivateRemarksMaxChars,
                profile.PhotoMinWidth,
                profile.PhotoMinHeight,
                profile.PhotoMinCount,
                profile.PhotoMaxCount,
                (JsonNode.Parse(profile.ForbiddenInPublicRemarks) as JsonArray ?? throw new InvalidOperationException("forbidden_in_public_remarks is not an array."))
                    .Select(node => new RemarksDetector(
                        node?["detector"]?.GetValue<string>() ?? throw new InvalidOperationException("A detector has no name."),
                        node["severity"]?.GetValue<string>() == "warning" ? Severity.Warning : Severity.Blocking))
                    .ToList(),
                ListingRepresentation.Strings(profile.RequiredFields),
                ListingRepresentation.Strings(profile.AllowedAlterationTypes),
                ListingRepresentation.Strings(profile.DisclosureLabels));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<decimal> Figures(JsonNode? value) => value switch
    {
        JsonValue scalar when scalar.TryGetValue<decimal>(out var number) => [number],
        JsonValue scalar when scalar.TryGetValue<string>(out var text)
            && decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => [parsed],
        _ => [],
    };
}
