namespace AiEval;

/// <summary>Outcome of one metric.</summary>
internal enum MetricOutcome
{
    Passed,
    Failed,

    /// <summary>No suite produced a value for this metric.</summary>
    NotMeasured,

    /// <summary>A value exists but the specification sets no bar to compare it with.</summary>
    NoThreshold,
}

/// <summary>Outcome of a whole evaluation.</summary>
internal enum Verdict
{
    /// <summary>Every metric was measured against a threshold and met it.</summary>
    Passed,

    /// <summary>At least one measured metric missed its threshold.</summary>
    Failed,

    /// <summary>
    /// Nothing failed, but something could not be judged. Not written to the registry: recording
    /// it as <c>failed</c> would misstate what is known, and as <c>passed</c> would be false.
    /// </summary>
    Incomplete,
}

internal sealed record MetricResult(EvalMetric Metric, double? Value, MetricOutcome Outcome);

/// <summary>A verdict, the per-metric results behind it, and why it is not a pass or failure when it is not.</summary>
/// <param name="Reasons">
/// Every explanation the harness prints under the results: an unreviewed evaluation set, a
/// missing suite, a metric a suite could not measure. Empty only when nothing needs explaining.
/// </param>
/// <param name="EvalSets">The evaluation sets the measurements were taken on, for the report.</param>
internal sealed record EvalOutcome(
    Verdict Verdict,
    IReadOnlyList<MetricResult> Results,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<EvalSet> EvalSets);

/// <summary>
/// T054 — turns measurements into a verdict.
///
/// <para>
/// The rule is that a pass must be earned on every criterion. A metric that was not measured,
/// or whose threshold has not been set, cannot contribute a pass, and a task with no metrics at
/// all cannot pass either — an empty checklist is not a completed one. This is the property
/// that makes the registry's <c>eval_status = 'passed'</c> mean something: it is only ever
/// written by this code, and this code only returns <see cref="Verdict.Passed"/> when each
/// specified bar was actually cleared.
/// </para>
/// <para>
/// <b>The evaluation-set review gate lives here.</b> A run on any evaluation set whose manifest
/// is not marked reviewed is <see cref="Verdict.Incomplete"/>, whatever it measured — even a
/// measured failure, since labels nobody has checked cannot establish that a model is worse than
/// the bar any more than that it is better. It sits here, rather than in the suite contract or in
/// <c>Program</c>, for three reasons:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Suites cannot be trusted to call a gate.</b> An interface cannot force an
///     implementation to run a check, and a base-class check can be overridden or simply not
///     inherited. A gate in the suite contract would hold only for suites written carefully.
///   </description></item>
///   <item><description>
///     <b>Suites cannot avoid this one.</b> A suite's only output is a <see cref="SuiteRun"/>,
///     which cannot be built without the <see cref="EvalSet"/>s it measured on, and an
///     <see cref="EvalSet"/> can only be loaded from a committed manifest. So every measurement
///     that reaches this method carries its sets' review state as read from the repository,
///     and a verdict cannot be produced from measurements without passing through here.
///   </description></item>
///   <item><description>
///     <b>This is the code that defines a verdict.</b> <see cref="RegistryResultWriter"/> records
///     nothing but <see cref="Verdict.Passed"/> or <see cref="Verdict.Failed"/>, and those come
///     only from here — so the property "a recorded verdict rests on reviewed labels" is enforced
///     at the same single point as "a recorded pass cleared every bar", and is unit-tested with
///     it.
///   </description></item>
/// </list>
/// </summary>
internal static class EvalVerdict
{
    /// <summary>
    /// Judges a suite run — the path every harness verdict takes.
    /// </summary>
    /// <param name="metrics">The task's acceptance metrics.</param>
    /// <param name="run">What the task's suite measured, or null when the task has no suite.</param>
    /// <param name="taskKey">The task under evaluation; every evaluation set must belong to it.</param>
    /// <exception cref="ArgumentException">
    /// A set belongs to another task, or a measurement names a metric the task does not define —
    /// both wiring errors in a suite, which must not be read as measurements.
    /// </exception>
    internal static EvalOutcome Evaluate(
        IReadOnlyList<EvalMetric> metrics,
        SuiteRun? run,
        string taskKey)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        if (run is null)
        {
            var (unmeasuredVerdict, unmeasuredResults) = Evaluate(metrics, new Dictionary<string, double>());

            return new EvalOutcome(
                unmeasuredVerdict,
                unmeasuredResults,
                [$"No evaluation suite exists for task '{taskKey}', so nothing was measured."],
                []);
        }

        var foreign = run.EvalSets.Where(set => set.TaskKey != taskKey).ToList();

        if (foreign.Count > 0)
        {
            throw new ArgumentException(
                $"The suite for '{taskKey}' measured on evaluation sets of another task: "
                + string.Join(", ", foreign.Select(set => $"{set.Name} ({set.TaskKey})")),
                nameof(run));
        }

        var (verdict, results) = Evaluate(metrics, run.Measurements);
        var reasons = new List<string>();

        var unreviewed = run.EvalSets.Where(set => !set.IsReviewed).ToList();

        foreach (var set in unreviewed)
        {
            reasons.Add(set.ReviewMismatch is { } mismatch
                ? $"evaluation set {set.Name} is marked reviewed, but {mismatch} ({set.ManifestPath}). "
                  + "Its measurements are shown, but no verdict can rest on labels the review did not see."
                : $"evaluation set {set.Name} has not been reviewed ({set.ManifestPath}). Its "
                  + "measurements are shown, but no verdict can rest on labels nobody has checked; "
                  + "record the review in the manifest's review block and run again.");
        }

        reasons.AddRange(run.Unmeasured);

        return new EvalOutcome(
            unreviewed.Count > 0 ? Verdict.Incomplete : verdict,
            results,
            reasons,
            run.EvalSets);
    }

    /// <summary>
    /// Compares measurements with thresholds. The rule set the gated overload builds on; the
    /// harness itself always goes through <see cref="Evaluate(IReadOnlyList{EvalMetric}, SuiteRun?, string)"/>.
    /// </summary>
    internal static (Verdict Verdict, IReadOnlyList<MetricResult> Results) Evaluate(
        IReadOnlyList<EvalMetric> metrics,
        IReadOnlyDictionary<string, double> measurements)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(measurements);

        var unknown = measurements.Keys
            .Where(key => metrics.All(metric => metric.Key != key))
            .ToList();

        if (unknown.Count > 0)
        {
            // A suite reporting a metric the catalog does not define is a wiring error, and
            // ignoring it would let a misspelt key silently leave the real metric unmeasured.
            throw new ArgumentException(
                "Measurements were reported for metrics this task does not define: "
                + string.Join(", ", unknown),
                nameof(measurements));
        }

        var results = metrics
            .Select(metric =>
            {
                if (!measurements.TryGetValue(metric.Key, out var value))
                {
                    return new MetricResult(metric, null, MetricOutcome.NotMeasured);
                }

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    throw new ArgumentException(
                        $"Metric '{metric.Key}' was reported as {value}, which is not a measurement.",
                        nameof(measurements));
                }

                if (metric.Threshold is null)
                {
                    return new MetricResult(metric, value, MetricOutcome.NoThreshold);
                }

                return new MetricResult(
                    metric,
                    value,
                    metric.IsMetBy(value) ? MetricOutcome.Passed : MetricOutcome.Failed);
            })
            .ToList();

        Verdict verdict;

        if (results.Any(result => result.Outcome == MetricOutcome.Failed))
        {
            verdict = Verdict.Failed;
        }
        else if (results.Count > 0 && results.All(result => result.Outcome == MetricOutcome.Passed))
        {
            verdict = Verdict.Passed;
        }
        else
        {
            verdict = Verdict.Incomplete;
        }

        return (verdict, results);
    }
}
