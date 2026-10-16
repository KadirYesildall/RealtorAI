using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Listings;
using Modules.Media;
using Pgvector;
using Pipeline;

namespace Api.Pipeline;

/// <summary>
/// The pipeline's persistence over the EF model (research R20: the model lives in the host).
///
/// <para>
/// <b>Listing status.</b> The run moves the listing from <c>analyzing</c> to
/// <c>ready_for_review</c> or back to <c>draft</c> without advancing its row version. The row
/// version guards what the agent edits (FR-066); a status change the system makes in the
/// background is not an edit of theirs, and advancing the version would turn the agent's next
/// autosave — made from a perfectly current view of their own fields — into a conflict. Recorded
/// in research R34.
/// </para>
/// </summary>
internal sealed class EfPipelineStore : IPipelineStore
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, RoomType> RoomTypes = Enum.GetValues<RoomType>()
        .ToDictionary(Label, type => type, StringComparer.Ordinal);

    private readonly AppDbContext context;
    private readonly IAuditWriter audit;

    public EfPipelineStore(AppDbContext context, IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(audit);

        this.context = context;
        this.audit = audit;
    }

    public async Task<PipelineRunContext?> LoadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        // A step may have left tracked rows behind; every step starts from what is stored.
        context.ChangeTracker.Clear();

        var run = await context.PipelineRuns.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        if (run is null)
        {
            return null;
        }

        var listing = await context.Listings.AsNoTracking()
            .Where(candidate => candidate.Id == run.ListingId)
            .Select(candidate => new { candidate.TargetMlsId })
            .SingleAsync(cancellationToken);

        var profile = listing.TargetMlsId is null
            ? null
            : await context.MlsRuleProfiles.AsNoTracking()
                .Where(candidate => candidate.Id == listing.TargetMlsId)
                .Select(candidate => new { candidate.PhotoMinWidth, candidate.PhotoMinHeight })
                .SingleOrDefaultAsync(cancellationToken);

        var photos = await context.Photos.AsNoTracking()
            .Where(photo => photo.ListingId == run.ListingId)
            .OrderBy(photo => photo.OrderIndex)
            .Select(photo => new PipelinePhoto(
                photo.Id,
                photo.OrderIndex,
                photo.OriginalStorageKey,
                photo.NormalizedStorageKey,
                photo.MimeType,
                photo.Width,
                photo.Height))
            .ToListAsync(cancellationToken);

        return new PipelineRunContext(run.Id, run.ListingId, run.StartedBy, photos, profile?.PhotoMinWidth, profile?.PhotoMinHeight);
    }

    public Task SetStepStateAsync(Guid runId, PipelineStep pipelineStep, StepState state, string? reason, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
            update public.pipeline_step_states
            set state = {PipelineSpellings.Of(state)}, failure_reason = {reason}, updated_at = timezone('utc', now())
            where run_id = {runId} and step = {PipelineSpellings.Of(pipelineStep)}
            """,
            cancellationToken);

    public Task SetPhotoStateAsync(Guid runId, Guid photoId, PhotoState state, string? reason, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
            insert into public.pipeline_photo_states (run_id, photo_id, state, failure_reason)
            select {runId}, {photoId}, {PipelineSpellings.Of(state)}, {reason}
            where exists (select 1 from public.photos where id = {photoId})
            on conflict (run_id, photo_id) do update
            set state = excluded.state, failure_reason = excluded.failure_reason, updated_at = timezone('utc', now())
            """,
            cancellationToken);

    public Task SetNormalizedKeyAsync(Guid photoId, string normalizedStorageKey, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"update public.photos set normalized_storage_key = {normalizedStorageKey} where id = {photoId}",
            cancellationToken);

    public Task SaveQualityFlagsAsync(Guid photoId, IReadOnlyList<string> flags, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(flags);

        return context.Database.ExecuteSqlAsync(
            $"""
            insert into public.photo_analyses (photo_id, quality_flags)
            values ({photoId}, {json}::jsonb)
            on conflict (photo_id) do update set quality_flags = excluded.quality_flags
            """,
            cancellationToken);
    }

    public Task AddQualityFlagAsync(Guid photoId, string flag, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new[] { flag });

        return context.Database.ExecuteSqlAsync(
            $"""
            insert into public.photo_analyses (photo_id, quality_flags)
            values ({photoId}, {json}::jsonb)
            on conflict (photo_id) do update
            set quality_flags = case
                when public.photo_analyses.quality_flags ? {flag} then public.photo_analyses.quality_flags
                else public.photo_analyses.quality_flags || excluded.quality_flags
            end
            """,
            cancellationToken);
    }

    public async Task SaveEmbeddingAsync(Guid photoId, IReadOnlyList<float> vector, string modelId, CancellationToken cancellationToken)
    {
        var existing = await context.PhotoEmbeddings.SingleOrDefaultAsync(row => row.PhotoId == photoId, cancellationToken);

        if (existing is null)
        {
            context.PhotoEmbeddings.Add(new PhotoEmbedding { PhotoId = photoId, Embedding = new Vector(vector.ToArray()), ModelId = modelId });
        }
        else
        {
            existing.Embedding = new Vector(vector.ToArray());
            existing.ModelId = modelId;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SimilarPair>> FindSimilarPairsAsync(
        Guid listingId,
        string modelId,
        double minimumSimilarity,
        CancellationToken cancellationToken)
    {
        // Cosine distance (<=>), matching the hnsw index's vector_cosine_ops; similarity is
        // 1 - distance. Each unordered pair once (a < b), within one listing and one model.
        var rows = await context.Database.SqlQuery<SimilarPairRow>(
            $"""
            select a.photo_id as "First", b.photo_id as "Second", (1 - (a.embedding <=> b.embedding))::float8 as "Similarity"
            from public.photo_embeddings a
            join public.photos pa on pa.id = a.photo_id
            join public.photo_embeddings b on b.photo_id > a.photo_id
            join public.photos pb on pb.id = b.photo_id
            where pa.listing_id = {listingId} and pb.listing_id = {listingId}
              and a.model_id = {modelId} and b.model_id = {modelId}
              and 1 - (a.embedding <=> b.embedding) >= {minimumSimilarity}
            """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new SimilarPair(row.First, row.Second, row.Similarity)).ToList();
    }

    public async Task<IReadOnlySet<Guid>> AnalysedPhotosAsync(Guid listingId, CancellationToken cancellationToken)
    {
        var ids = await context.PhotoAnalyses.AsNoTracking()
            .Where(analysis => analysis.ModelId != null && !analysis.Failed)
            .Join(context.Photos.AsNoTracking().Where(photo => photo.ListingId == listingId), analysis => analysis.PhotoId, photo => photo.Id, (analysis, _) => analysis.PhotoId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task SaveAnalysisAsync(PipelineRunContext run, PhotoAnalysisOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcome);

        var document = outcome.Document;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var analysis = await context.PhotoAnalyses.SingleOrDefaultAsync(row => row.PhotoId == outcome.PhotoId, cancellationToken);

        if (analysis is null)
        {
            analysis = new PhotoAnalysis { Id = Guid.CreateVersion7(), PhotoId = outcome.PhotoId };
            context.PhotoAnalyses.Add(analysis);
        }

        analysis.RoomType = RoomTypes[document["roomType"]!.GetValue<string>()];
        analysis.RoomTypeConfidence = Math.Round((decimal)document["confidence"]!.GetValue<double>(), 3);
        analysis.FeatureTags = document["featureTags"]!.ToJsonString();
        analysis.ContentFlags = document["contentFlags"]!.ToJsonString();
        analysis.AltText = document["altText"]!.GetValue<string>();
        analysis.ModelId = outcome.ModelId;
        analysis.PromptVersion = outcome.PromptVersion.ToString(CultureInfo.InvariantCulture);
        analysis.PromptHash = outcome.PromptHash;
        analysis.RawOutput = document.ToJsonString();
        analysis.Failed = false;
        analysis.FailureReason = null;

        // FR-010: the alt text becomes the caption the agent edits — but never over one they wrote.
        await context.Database.ExecuteSqlAsync(
            $"update public.photos set caption = {analysis.AltText} where id = {outcome.PhotoId} and caption is null",
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        var brokerageId = await context.Listings.AsNoTracking()
            .Where(listing => listing.Id == run.ListingId)
            .Select(listing => listing.BrokerageId)
            .SingleAsync(cancellationToken);

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.AiCall,
                ActorId = run.StartedBy,
                ListingId = run.ListingId,
                BrokerageId = brokerageId,
                TargetType = "photo",
                TargetId = outcome.PhotoId,
                ModelId = outcome.ModelId,
                PromptVersion = analysis.PromptVersion,
                PromptHash = outcome.PromptHash,
                InputRef = $"photos/{outcome.PhotoId:D}/normalized",
                OutputRef = $"photo_analyses/{analysis.Id:D}",
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveAnalysisFailureAsync(Guid photoId, string reason, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
            insert into public.photo_analyses (photo_id, failed, failure_reason)
            values ({photoId}, true, {reason})
            on conflict (photo_id) do update set failed = true, failure_reason = excluded.failure_reason
            """,
            cancellationToken);

    public async Task<IReadOnlyList<ClassifiedPhoto>> ClassifiedPhotosAsync(Guid listingId, CancellationToken cancellationToken)
    {
        var rows = await context.Photos.AsNoTracking()
            .Where(photo => photo.ListingId == listingId)
            .GroupJoin(
                context.PhotoAnalyses.AsNoTracking(),
                photo => photo.Id,
                analysis => analysis.PhotoId,
                (photo, analyses) => new { photo, analyses })
            .SelectMany(row => row.analyses.DefaultIfEmpty(), (row, analysis) => new { row.photo, analysis })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                var modelRoom = row.analysis is { Failed: false, ModelId: not null } ? row.analysis.RoomType : null;
                var room = row.photo.RoomTypeOverride ?? modelRoom;

                return room is null
                    ? null
                    : new ClassifiedPhoto(
                        row.photo.Id,
                        row.photo.OrderIndex,
                        Label(room.Value),
                        row.photo.RoomTypeOverride is null ? row.analysis?.RoomTypeConfidence : null,
                        ModelRecommendsCover(row.analysis?.RawOutput),
                        Strings(row.analysis?.QualityFlags),
                        Strings(row.analysis?.ContentFlags));
            })
            .OfType<ClassifiedPhoto>()
            .ToList();
    }

    public async Task SaveSuggestionsAsync(IReadOnlyList<OrderSuggestion> suggestions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var suggestion in suggestions)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                insert into public.photo_analyses (photo_id, cover_recommended, order_recommendation, recommendation_reason)
                values ({suggestion.PhotoId}, {suggestion.CoverRecommended}, {suggestion.OrderRecommendation}, {suggestion.Reason})
                on conflict (photo_id) do update
                set cover_recommended = excluded.cover_recommended,
                    order_recommendation = excluded.order_recommendation,
                    recommendation_reason = excluded.recommendation_reason
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinishRunAsync(Guid runId, RunOutcome outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var listingId = await context.PipelineRuns.Where(run => run.Id == runId).Select(run => run.ListingId).SingleAsync(cancellationToken);

        var next = ListingLifecycle.Transition(
            ListingStatus.Analyzing,
            outcome == RunOutcome.Analysed ? ListingStatus.ReadyForReview : ListingStatus.Draft);

        // Only a listing still being analysed moves: one the agent archived meanwhile stays
        // archived, and one that was already past draft when the run started was never moved.
        await context.Listings
            .Where(listing => listing.Id == listingId && listing.Status == ListingStatus.Analyzing)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, next), cancellationToken);

        await context.Database.ExecuteSqlAsync(
            $"update public.pipeline_runs set finished_at = timezone('utc', now()) where id = {runId}",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static string Label(RoomType type) =>
        typeof(RoomType).GetField(type.ToString())!
            .GetCustomAttributes(typeof(NpgsqlTypes.PgNameAttribute), inherit: false)
            .Cast<NpgsqlTypes.PgNameAttribute>()
            .Single()
            .PgName;

    private static bool ModelRecommendsCover(string? rawOutput) =>
        rawOutput is not null && JsonNode.Parse(rawOutput)?["coverRecommended"]?.GetValue<bool>() == true;

    private static List<string> Strings(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<string>>(json, Web) ?? [];

    /// <summary>A row of the similarity query; settable properties, as EF materializes unmapped types.</summary>
    private sealed class SimilarPairRow
    {
        public Guid First { get; set; }

        public Guid Second { get; set; }

        public double Similarity { get; set; }
    }
}
