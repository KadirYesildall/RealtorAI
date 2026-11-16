namespace AiEval;

/// <summary>The registry row under evaluation — the exact model a verdict will be recorded against.</summary>
/// <param name="Parameters">
/// The row's provider call parameters (a JSON object), sent with every evaluation call exactly as
/// production sends them. Part of the identity because they change the model's behaviour: the
/// compliance judge's temperature 0 (FR-024) lives here.
/// </param>
internal sealed record RegistryTarget(
    Guid Id,
    string TaskKey,
    string Provider,
    string ModelId,
    string? Endpoint,
    string Parameters,
    bool Enabled);

/// <summary>
/// Measures one task's metrics for one model against labelled evaluation sets.
///
/// <para>
/// A suite reports raw measurements only. It does not decide whether they pass: that belongs to
/// <see cref="EvalVerdict"/> and the thresholds in <see cref="MetricCatalog"/>, so no suite can
/// lower a bar by how it reports.
/// </para>
/// <para>
/// <b>A suite never sees a chat client.</b> It is handed an <see cref="EvalModelInvoker"/>
/// already bound to the registry row under evaluation, so the only model it can call is the one
/// the verdict will be recorded against, and only as production would call it.
/// </para>
/// </summary>
internal interface IEvalSuite
{
    /// <summary>The registry task this suite evaluates.</summary>
    string TaskKey { get; }

    /// <summary>Runs the suite's evaluation sets against <paramref name="model"/>'s target.</summary>
    /// <returns>
    /// The measurements, together with the evaluation sets they were taken on. Metric keys must
    /// exist in the task's catalog.
    /// </returns>
    Task<SuiteRun> RunAsync(EvalModelInvoker model, CancellationToken cancellationToken);
}

/// <summary>
/// What one suite run measured, and on which evaluation sets.
///
/// <para>
/// <b>Measurements cannot leave a suite without the sets they were taken on.</b> The constructor
/// requires at least one <see cref="EvalSet"/>, and an <see cref="EvalSet"/> can only be obtained
/// by loading a committed manifest — so whether those sets were reviewed is a fact read from the
/// repository, never something a suite asserts. <see cref="EvalVerdict"/> reads it from here.
/// </para>
/// </summary>
internal sealed class SuiteRun
{
    /// <param name="evalSets">Every evaluation set any measurement was taken on.</param>
    /// <param name="measurements">Metric key to measured value.</param>
    /// <param name="unmeasured">
    /// Why a metric the suite was meant to report has no value — for example, that the provider
    /// failed some calls (see <see cref="CallMetrics"/>). Printed by the harness, so an
    /// unmeasured metric is never left unexplained.
    /// </param>
    internal SuiteRun(
        IReadOnlyList<EvalSet> evalSets,
        IReadOnlyDictionary<string, double> measurements,
        IReadOnlyList<string>? unmeasured = null)
    {
        ArgumentNullException.ThrowIfNull(evalSets);
        ArgumentNullException.ThrowIfNull(measurements);

        if (evalSets.Count == 0)
        {
            throw new ArgumentException(
                "A suite run must name the evaluation sets its measurements were taken on. A "
                + "measurement with no set behind it cannot be checked, and a verdict resting on "
                + "it could not be either.",
                nameof(evalSets));
        }

        if (evalSets.Any(set => set is null))
        {
            throw new ArgumentException("An evaluation set in the list is null.", nameof(evalSets));
        }

        EvalSets = [.. evalSets];
        Measurements = new Dictionary<string, double>(measurements, StringComparer.Ordinal);
        Unmeasured = [.. unmeasured ?? []];
    }

    internal IReadOnlyList<EvalSet> EvalSets { get; }

    internal IReadOnlyDictionary<string, double> Measurements { get; }

    internal IReadOnlyList<string> Unmeasured { get; }
}

/// <summary>
/// The suites the harness can run, keyed by task.
///
/// <para>
/// Three suites ship, each with its evaluation set: photo analysis (<see cref="PhotoEval"/>,
/// T156), Fair Housing judging (<see cref="ComplianceEval"/>, T157) and listing content
/// (<see cref="ListingContentEval"/>, T158), whose set holds inputs only because neither of its
/// metrics judges the copy. All three report the call-level
/// metrics — schema validity, latency and per-listing consumption (T158) — through
/// <see cref="EvalModelInvoker"/> and <see cref="CallMetrics"/> rather than as a suite of their
/// own, because those metrics describe the same calls the accuracy metrics are measured on.
/// </para>
/// <para>
/// <c>photo.embedding</c> has no suite, because the specification defines no acceptance metric for
/// it (research R27). A task with no suite here
/// produces no measurements, so <see cref="EvalVerdict"/> reports it incomplete and the harness
/// records nothing — the absence of a suite can never become a pass. Registering a suite does not
/// make it passable either: until its set's manifest is marked reviewed, every run is
/// incomplete (see <see cref="EvalSet"/>).
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
    /// <param name="connectionString">
    /// The harness's database connection, which the content suite reads the MLS rule profile
    /// through. Null when the harness only lists criteria; no suite touches it until it runs.
    /// </param>
    internal static IReadOnlyDictionary<string, IEvalSuite> Shipped(string? connectionString) =>
        Build([new PhotoEval(), new ComplianceEval(), new ListingContentEval(connectionString)]);
}
