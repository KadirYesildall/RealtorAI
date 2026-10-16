namespace Pipeline;

/// <summary>
/// The contract's spellings of the pipeline's enums (<c>PipelineStatus</c>), which are also the
/// values 0015_pipeline_runs.sql's check constraints accept. One mapping, used by the store that
/// writes them and the endpoint that reads them back.
/// </summary>
public static class PipelineSpellings
{
    public static string Of(PipelineStep step) => step switch
    {
        PipelineStep.Normalize => "normalize",
        PipelineStep.TechnicalQuality => "technical_quality",
        PipelineStep.Embeddings => "embeddings",
        PipelineStep.PhotoAnalysis => "photo_analysis",
        PipelineStep.CoverAndOrder => "cover_and_order",
        PipelineStep.ContentGeneration => "content_generation",
        PipelineStep.Compliance => "compliance",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    public static string Of(StepState state) => state switch
    {
        StepState.Queued => "queued",
        StepState.Running => "running",
        StepState.Done => "done",
        StepState.Failed => "failed",
        StepState.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static string Of(PhotoState state) => state switch
    {
        PhotoState.Queued => "queued",
        PhotoState.Analyzing => "analyzing",
        PhotoState.Done => "done",
        PhotoState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
