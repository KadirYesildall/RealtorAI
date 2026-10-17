namespace Pipeline;

/// <summary>
/// Runs the compliance checks for a pipeline run and stores what they find, implemented by the
/// host where the EF model lives (research R20).
/// </summary>
public interface IComplianceRunner
{
    /// <summary>
    /// Checks the run's one target, or the whole listing when the run names none, and stores the
    /// findings: new ones raised, ones no longer found resolved, unchanged ones kept as they are.
    /// </summary>
    Task CheckAsync(PipelineRunContext run, CancellationToken cancellationToken);
}
