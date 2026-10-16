namespace AiEval;

/// <summary>The registry row under evaluation — the exact model a verdict will be recorded against.</summary>
internal sealed record RegistryTarget(
    Guid Id,
    string TaskKey,
    string Provider,
    string ModelId,
    string? Endpoint,
    bool Enabled);

/// <summary>
/// Measures one task's metrics for one model against a labelled evaluation set.
///
/// <para>
/// A suite reports raw measurements only. It does not decide whether they pass: that belongs to
/// <see cref="EvalVerdict"/> and the thresholds in <see cref="MetricCatalog"/>, so no suite can
/// lower a bar by how it reports.
/// </para>
/// </summary>
internal interface IEvalSuite
{
    /// <summary>The registry task this suite evaluates.</summary>
    string TaskKey { get; }

    /// <summary>Runs the evaluation set against <paramref name="target"/>.</summary>
    /// <returns>Metric key to measured value. Keys must exist in the task's catalog.</returns>
    Task<IReadOnlyDictionary<string, double>> RunAsync(
        RegistryTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// The suites the harness can run, keyed by task.
///
/// <para>
/// Suites arrive with their labelled evaluation sets: photo analysis (T156), Fair Housing
/// judging (T157), and latency/cost/schema-validity measurement (T158). A task with no suite
/// here produces no measurements, so <see cref="EvalVerdict"/> reports it incomplete and the
/// harness records nothing — the absence of a suite can never become a pass.
/// </para>
/// </summary>
internal static class EvalSuites
{
    internal static IReadOnlyDictionary<string, IEvalSuite> Build(IEnumerable<IEvalSuite> suites)
    {
        ArgumentNullException.ThrowIfNull(suites);

        var byTask = new Dictionary<string, IEvalSuite>(StringComparer.Ordinal);

        foreach (var suite in suites)
        {
            if (!byTask.TryAdd(suite.TaskKey, suite))
            {
                throw new InvalidOperationException(
                    $"Two evaluation suites claim task '{suite.TaskKey}'. Exactly one must own "
                    + "each task, or which one produced a recorded verdict is ambiguous.");
            }
        }

        return byTask;
    }

    /// <summary>The suites shipped with the harness.</summary>
    internal static IReadOnlyDictionary<string, IEvalSuite> Registered { get; } = Build([]);
}
