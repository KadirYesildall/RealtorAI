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
/// </summary>
internal static class EvalVerdict
{
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
