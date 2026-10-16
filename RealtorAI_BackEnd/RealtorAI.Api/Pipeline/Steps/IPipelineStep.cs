namespace Pipeline.Steps;

/// <summary>How a step ended.</summary>
/// <param name="Reason">Why it failed or was skipped, phrased for the agent. Null when done.</param>
public sealed record StepOutcome(StepState State, string? Reason)
{
    public static StepOutcome Done { get; } = new(StepState.Done, null);

    public static StepOutcome Failed(string reason) => new(StepState.Failed, reason);

    public static StepOutcome Skipped(string reason) => new(StepState.Skipped, reason);
}

/// <summary>
/// One step of the analysis pipeline (research R12).
///
/// <para>
/// <b>Idempotent and resumable.</b> A step does only the work its results do not already show —
/// a photo with a normalized copy is not normalized again, a photo already analysed is not sent
/// to the model again — so a run resumed after a crash, or started again to retry what failed,
/// repeats nothing and spends nothing twice.
/// </para>
/// <para>
/// <b>Failure stays inside the step.</b> A step reports a failure as its outcome rather than
/// throwing, and the run carries on with the steps that do not depend on it: a sidecar outage
/// costs the quality flags, not the analysis (contracts/vision-sidecar.md). Only cancellation
/// escapes.
/// </para>
/// </summary>
public interface IPipelineStep
{
    PipelineStep Kind { get; }

    Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken);
}
