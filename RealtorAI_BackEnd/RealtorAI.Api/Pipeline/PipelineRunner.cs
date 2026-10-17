using Hangfire;
using Microsoft.Extensions.Logging;
using Pipeline.Steps;

namespace Pipeline;

/// <summary>
/// T095 — runs one pipeline run, step by step, as a background job (FR-053, research R12).
///
/// <para>
/// <b>Order.</b> The run's steps run in their declared order, each over the listing as the previous
/// left it: the run's photos are re-read before every step, so a copy the normalize step just
/// produced is what the quality step measures.
/// </para>
/// <para>
/// <b>Failure.</b> A step reports its own failure and the run goes on; only the listing's final
/// status depends on the outcome — ready for review when at least one photo was analysed, back
/// to a draft when none was (SC-010).
/// </para>
/// <para>
/// <b>No automatic retry.</b> Hangfire's default would re-run the whole job on an exception,
/// repeating every step's work and spending the budget again for nothing new. The steps are
/// idempotent, so a run the process died in the middle of is resumed by Hangfire re-queuing the
/// job when its invisibility timeout lapses — and started again by the agent if they want
/// failures retried.
/// </para>
/// </summary>
public sealed partial class PipelineRunner
{
    private readonly IPipelineStore store;
    private readonly PipelineReporter reporter;
    private readonly IReadOnlyList<IPipelineStep> steps;
    private readonly ILogger<PipelineRunner> logger;

    public PipelineRunner(
        IPipelineStore store,
        PipelineReporter reporter,
        IEnumerable<IPipelineStep> steps,
        ILogger<PipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(logger);

        this.store = store;
        this.reporter = reporter;
        this.steps = steps.OrderBy(step => step.Kind).ToList();
        this.logger = logger;
    }

    /// <summary>The steps a run consists of, in order — what <c>POST /pipeline</c> queues.</summary>
    public IReadOnlyList<PipelineStep> Steps => steps.Select(step => step.Kind).ToList();

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var first = await store.LoadRunAsync(runId, cancellationToken);

        if (first is null)
        {
            LogRunMissing(logger, runId);
            return;
        }

        // A run is the steps it was queued with: all of them for analysis, one for a regeneration.
        var queued = steps.Where(step => first.Steps.Contains(step.Kind)).ToList();
        var analyses = first.Steps.Contains(PipelineStep.PhotoAnalysis);
        var analysed = false;

        if (queued.Count == 0)
        {
            // Nothing this host can run: finish it, so the listing is not held by an empty run.
            await store.FinishRunAsync(runId, RunOutcome.Unchanged, cancellationToken);
            return;
        }

        foreach (var step in queued)
        {
            // Re-read for every step: each works on the listing as the previous step left it.
            var run = await store.LoadRunAsync(runId, cancellationToken);

            if (run is null)
            {
                LogRunMissing(logger, runId);
                return;
            }

            await reporter.StepAsync(run, step.Kind, StepState.Running, null, cancellationToken);

            StepOutcome outcome;

            try
            {
                outcome = await step.ExecuteAsync(run, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A defect, not an expected failure — steps report those as outcomes. Recorded
                // so the run still finishes and the listing is not left "analyzing" for ever.
                LogStepCrashed(logger, exception, step.Kind, runId);
                outcome = StepOutcome.Failed("This step stopped unexpectedly. Run analysis again; everything can also be done by hand.");
            }

            if (step.Kind == PipelineStep.PhotoAnalysis)
            {
                analysed = outcome.State == StepState.Done;
            }

            // The listing moves on before the last step reports, so anyone who sees every step
            // settled also sees the listing's final status — never "all done, still analysing".
            if (ReferenceEquals(step, queued[^1]))
            {
                var finished = !analyses ? RunOutcome.Unchanged : analysed ? RunOutcome.Analysed : RunOutcome.TotalFailure;
                await store.FinishRunAsync(runId, finished, cancellationToken);
            }

            await reporter.StepAsync(run, step.Kind, outcome.State, outcome.Reason, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {RunId} no longer exists; nothing to do.")]
    private static partial void LogRunMissing(ILogger logger, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline step {Step} crashed in run {RunId}.")]
    private static partial void LogStepCrashed(ILogger logger, Exception exception, PipelineStep step, Guid runId);
}
