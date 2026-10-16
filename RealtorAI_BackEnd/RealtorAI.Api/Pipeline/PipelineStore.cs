using System.Text.Json.Nodes;

namespace Pipeline;

/// <summary>One photo as the pipeline sees it.</summary>
/// <param name="Width">The original's width — what an MLS minimum is measured against.</param>
/// <param name="Height">The original's height.</param>
public sealed record PipelinePhoto(
    Guid Id,
    int OrderIndex,
    string OriginalStorageKey,
    string? NormalizedStorageKey,
    string MimeType,
    int? Width,
    int? Height);

/// <summary>Everything a run needs to know about its listing, loaded once when it starts.</summary>
/// <param name="StartedBy">The agent whose action started the run; every AI call is audited as theirs.</param>
/// <param name="MinWidth">The MLS rule profile's minimum, or null when it has none verified.</param>
public sealed record PipelineRunContext(
    Guid RunId,
    Guid ListingId,
    Guid StartedBy,
    IReadOnlyList<PipelinePhoto> Photos,
    int? MinWidth,
    int? MinHeight);

/// <summary>A schema-validated <c>photo.analysis</c> result and where it came from (VIII.5).</summary>
public sealed record PhotoAnalysisOutcome(
    Guid PhotoId,
    JsonNode Document,
    string ModelId,
    int PromptVersion,
    string PromptHash);

/// <summary>A photo's analysis as cover-and-order sees it: the agent's values where they override the model's.</summary>
public sealed record ClassifiedPhoto(
    Guid PhotoId,
    int OrderIndex,
    string RoomType,
    decimal? Confidence,
    bool ModelRecommendsCover,
    IReadOnlyList<string> QualityFlags,
    IReadOnlyList<string> ContentFlags);

/// <summary>One cover-and-order suggestion, with its reason (FR-010).</summary>
/// <param name="Reason">A stable reason code, <c>rule:…</c>, which the review screen states in the agent's language.</param>
public sealed record OrderSuggestion(Guid PhotoId, bool CoverRecommended, int OrderRecommendation, string Reason);

/// <summary>How a run ended, for the listing's status (data-model.md state machine).</summary>
public enum RunOutcome
{
    /// <summary>At least one photo was analysed: the listing is ready for review.</summary>
    Analysed,

    /// <summary>Every analysis failed: the listing goes back to a draft to finish by hand (SC-010).</summary>
    TotalFailure,
}

/// <summary>
/// The persistence the pipeline needs, implemented by the host where the EF model lives
/// (research R20). Every method is idempotent where a step may repeat it: a run resumed after a
/// crash, or started again to retry failed photos, must not double anything.
/// </summary>
public interface IPipelineStore
{
    /// <summary>The run and its listing's photos, or null when the run does not exist.</summary>
    Task<PipelineRunContext?> LoadRunAsync(Guid runId, CancellationToken cancellationToken);

    Task SetStepStateAsync(Guid runId, PipelineStep pipelineStep, StepState state, string? reason, CancellationToken cancellationToken);

    Task SetPhotoStateAsync(Guid runId, Guid photoId, PhotoState state, string? reason, CancellationToken cancellationToken);

    Task SetNormalizedKeyAsync(Guid photoId, string normalizedStorageKey, CancellationToken cancellationToken);

    /// <summary>Replaces the photo's quality flags with <paramref name="flags"/>.</summary>
    Task SaveQualityFlagsAsync(Guid photoId, IReadOnlyList<string> flags, CancellationToken cancellationToken);

    /// <summary>Adds one quality flag, leaving the others.</summary>
    Task AddQualityFlagAsync(Guid photoId, string flag, CancellationToken cancellationToken);

    Task SaveEmbeddingAsync(Guid photoId, IReadOnlyList<float> vector, string modelId, CancellationToken cancellationToken);

    /// <summary>
    /// Pairs of this listing's photos whose embeddings from <paramref name="modelId"/> have cosine
    /// similarity of at least <paramref name="minimumSimilarity"/>.
    /// </summary>
    Task<IReadOnlyList<Modules.Media.SimilarPair>> FindSimilarPairsAsync(
        Guid listingId,
        string modelId,
        double minimumSimilarity,
        CancellationToken cancellationToken);

    /// <summary>Photos of the listing that already hold a successful model analysis.</summary>
    Task<IReadOnlySet<Guid>> AnalysedPhotosAsync(Guid listingId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a successful analysis, fills an empty caption from its alt text (FR-010), and audits
    /// the call as <c>ai_call</c> — in one transaction, so a result is never stored unaudited.
    /// </summary>
    Task SaveAnalysisAsync(PipelineRunContext run, PhotoAnalysisOutcome outcome, CancellationToken cancellationToken);

    /// <summary>Records that analysis failed for a photo, with the reason (Story 2 scenario 8).</summary>
    Task SaveAnalysisFailureAsync(Guid photoId, string reason, CancellationToken cancellationToken);

    /// <summary>The listing's photos that have a room type — the model's, or the agent's override.</summary>
    Task<IReadOnlyList<ClassifiedPhoto>> ClassifiedPhotosAsync(Guid listingId, CancellationToken cancellationToken);

    Task SaveSuggestionsAsync(IReadOnlyList<OrderSuggestion> suggestions, CancellationToken cancellationToken);

    /// <summary>Marks the run finished and moves the listing on, if it is still being analysed.</summary>
    Task FinishRunAsync(Guid runId, RunOutcome outcome, CancellationToken cancellationToken);
}
