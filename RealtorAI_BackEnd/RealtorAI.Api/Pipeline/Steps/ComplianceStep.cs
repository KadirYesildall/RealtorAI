namespace Pipeline.Steps;

/// <summary>
/// T135 — the compliance step: the last step of an analysis or regeneration run, and the only
/// step of a check the agent starts (FR-017–FR-021, FR-025).
///
/// <para>
/// It does not fail for want of a model. The deterministic layers always run, and when the
/// judge cannot, the check records that as a warning finding instead (research R36). The step
/// fails only when the check itself could not complete.
/// </para>
/// </summary>
public sealed class ComplianceStep : IPipelineStep
{
    private readonly IComplianceRunner runner;

    public ComplianceStep(IComplianceRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        this.runner = runner;
    }

    public PipelineStep Kind => PipelineStep.Compliance;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await runner.CheckAsync(run, cancellationToken);

        return StepOutcome.Done;
    }
}
