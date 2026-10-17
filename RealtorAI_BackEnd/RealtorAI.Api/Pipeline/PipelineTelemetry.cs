using System.Diagnostics;

namespace Pipeline;

/// <summary>
/// T162 — the pipeline's trace spans.
///
/// <para>
/// One <c>pipeline.run</c> span per run, with a <c>pipeline.step</c> child per step. Each step's
/// outcome is a tag, and a failed step's reason is its status description, so a slow or failed
/// run can be read step by step in the tracing backend (SC-005's latency is measured from these).
/// Built on <see cref="ActivitySource"/>, which OpenTelemetry exports when the host adds this
/// source. The pipeline project itself takes no telemetry dependency.
/// </para>
/// <para>
/// <b>No listing content in a span.</b> Tags carry ids, step names and states only; a failure
/// reason is the agent-facing sentence the step already records, never model output.
/// </para>
/// </summary>
public static class PipelineTelemetry
{
    public const string SourceName = "RealtorAI.Pipeline";

    internal static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartRun(PipelineRunContext run, IReadOnlyCollection<PipelineStep> steps)
    {
        var activity = Source.StartActivity("pipeline.run", ActivityKind.Internal);

        activity?.SetTag("realtorai.run_id", run.RunId.ToString());
        activity?.SetTag("realtorai.listing_id", run.ListingId.ToString());
        activity?.SetTag("realtorai.steps", string.Join(",", steps.Select(PipelineSpellings.Of)));
        activity?.SetTag("realtorai.photo_count", run.Photos.Count);

        return activity;
    }

    internal static Activity? StartStep(PipelineRunContext run, PipelineStep step)
    {
        var activity = Source.StartActivity("pipeline.step", ActivityKind.Internal);

        activity?.SetTag("realtorai.run_id", run.RunId.ToString());
        activity?.SetTag("realtorai.listing_id", run.ListingId.ToString());
        activity?.SetTag("realtorai.step", PipelineSpellings.Of(step));

        return activity;
    }

    internal static void Finish(Activity? activity, Steps.StepOutcome outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("realtorai.step.outcome", PipelineSpellings.Of(outcome.State));

        if (outcome.State == StepState.Failed)
        {
            activity.SetStatus(ActivityStatusCode.Error, outcome.Reason);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }
}
