using System.Text.Json.Serialization;

namespace Pipeline;

/// <summary>
/// The pipeline's steps, in execution order (contracts/api.openapi.yaml <c>PipelineStatus</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineStep>))]
public enum PipelineStep
{
    [JsonStringEnumMemberName("normalize")]
    Normalize,

    [JsonStringEnumMemberName("technical_quality")]
    TechnicalQuality,

    [JsonStringEnumMemberName("embeddings")]
    Embeddings,

    [JsonStringEnumMemberName("photo_analysis")]
    PhotoAnalysis,

    [JsonStringEnumMemberName("cover_and_order")]
    CoverAndOrder,

    [JsonStringEnumMemberName("content_generation")]
    ContentGeneration,

    [JsonStringEnumMemberName("compliance")]
    Compliance,
}

/// <summary>Where one pipeline step stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepState>))]
public enum StepState
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>
    /// Not run — typically because no model is enabled for it. The listing stays completable by
    /// hand (SC-010), so a skipped step is a normal state, not an error.
    /// </summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>Where one photo stands in analysis (FR-053: results stream per photo).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhotoState>))]
public enum PhotoState
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("analyzing")]
    Analyzing,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>A pipeline step changed state.</summary>
/// <param name="FailureReason">
/// Why a <see cref="StepState.Failed"/> or <see cref="StepState.Skipped"/> step did not
/// complete, phrased for the agent. Null otherwise.
/// </param>
public sealed record StepProgress(
    Guid ListingId,
    PipelineStep Step,
    StepState State,
    string? FailureReason);

/// <summary>One photo changed state.</summary>
public sealed record PhotoProgress(
    Guid ListingId,
    Guid PhotoId,
    PhotoState State,
    string? FailureReason);

/// <summary>
/// T056 — how pipeline steps report progress, without knowing how it is delivered.
///
/// <para>
/// Steps live in this project and the SignalR hub lives in the host, which references this
/// project; the dependency can only point one way. The host implements this over the hub, and a
/// step simply reports what happened.
/// </para>
/// <para>
/// Delivery is best-effort by design. A dropped notification costs a moment of stale UI,
/// because the polling endpoint (<c>GET /api/listings/{id}/pipeline</c>) reads the persisted
/// state and is always correct — a notification is a hint to refresh, never the record. An
/// implementation must therefore not throw into the step for a delivery failure: a pipeline
/// step must not fail because nobody was watching it.
/// </para>
/// </summary>
public interface IPipelineProgressNotifier
{
    Task StepChangedAsync(StepProgress progress, CancellationToken cancellationToken);

    Task PhotoChangedAsync(PhotoProgress progress, CancellationToken cancellationToken);
}
