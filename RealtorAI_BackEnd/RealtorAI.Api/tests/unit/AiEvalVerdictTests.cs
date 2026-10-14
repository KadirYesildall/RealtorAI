using System.Text.Json;
using AiEval;

namespace Tests.Unit;

/// <summary>
/// T054 — the evaluation harness's verdict rules and acceptance criteria.
///
/// <para>
/// The property under test is narrow and important: <see cref="Verdict.Passed"/> is returned
/// only when every specified bar was measured and cleared. Everything else — a missing
/// measurement, an unset threshold, a task with no metrics — must not pass, because a pass is
/// what VIII.4 lets the registry turn into an enabled model.
/// </para>
/// </summary>
public sealed class AiEvalVerdictTests
{
    private static readonly MetricCatalog Catalog = new(EvalBudget.Default);

    private static EvalMetric Metric(string key, Comparison comparison, double? threshold) =>
        new(key, key, "TEST", comparison, threshold);

    // ───────────────────────────── Verdict rules ─────────────────────────────

    [Fact]
    public void AllMeasuredAndMet_Passes()
    {
        var (verdict, results) = EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9), Metric("b", Comparison.AtMost, 0.1)],
            new Dictionary<string, double> { ["a"] = 0.95, ["b"] = 0.05 });

        Assert.Equal(Verdict.Passed, verdict);
        Assert.All(results, result => Assert.Equal(MetricOutcome.Passed, result.Outcome));
    }

    [Fact]
    public void OneMissedThreshold_Fails()
    {
        var (verdict, results) = EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9), Metric("b", Comparison.AtMost, 0.1)],
            new Dictionary<string, double> { ["a"] = 0.95, ["b"] = 0.2 });

        Assert.Equal(Verdict.Failed, verdict);
        Assert.Equal(MetricOutcome.Failed, results.Single(result => result.Metric.Key == "b").Outcome);
    }

    /// <summary>A measured failure stands even when other metrics went unmeasured.</summary>
    [Fact]
    public void AMissedThresholdWithOthersUnmeasured_StillFails()
    {
        var (verdict, _) = EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9), Metric("b", Comparison.AtMost, 0.1)],
            new Dictionary<string, double> { ["a"] = 0.5 });

        Assert.Equal(Verdict.Failed, verdict);
    }

    [Fact]
    public void AnUnmeasuredMetric_IsIncomplete_NotPassed()
    {
        var (verdict, results) = EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9), Metric("b", Comparison.AtMost, 0.1)],
            new Dictionary<string, double> { ["a"] = 0.95 });

        Assert.Equal(Verdict.Incomplete, verdict);
        Assert.Equal(MetricOutcome.NotMeasured, results.Single(result => result.Metric.Key == "b").Outcome);
    }

    /// <summary>
    /// A value with no bar to compare it to proves nothing; the harness must not treat "no
    /// threshold" as "no requirement".
    /// </summary>
    [Fact]
    public void AMetricWithNoThreshold_IsIncomplete_EvenWhenMeasured()
    {
        var (verdict, results) = EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9), Metric("b", Comparison.AtLeast, null)],
            new Dictionary<string, double> { ["a"] = 0.95, ["b"] = 1.0 });

        Assert.Equal(Verdict.Incomplete, verdict);
        Assert.Equal(MetricOutcome.NoThreshold, results.Single(result => result.Metric.Key == "b").Outcome);
    }

    /// <summary>An empty checklist is not a completed one.</summary>
    [Fact]
    public void ATaskWithNoMetrics_CannotPass()
    {
        var (verdict, results) = EvalVerdict.Evaluate([], new Dictionary<string, double>());

        Assert.Equal(Verdict.Incomplete, verdict);
        Assert.Empty(results);
    }

    [Fact]
    public void NothingMeasured_IsIncomplete()
    {
        var (verdict, _) = EvalVerdict.Evaluate(
            Catalog.For("compliance.judge"),
            new Dictionary<string, double>());

        Assert.Equal(Verdict.Incomplete, verdict);
    }

    /// <summary>
    /// A misspelt key must not be ignored: the real metric would then read as unmeasured while
    /// the suite believed it had reported it.
    /// </summary>
    [Fact]
    public void AMeasurementForAnUndefinedMetric_Throws()
    {
        Assert.Throws<ArgumentException>(() => EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9)],
            new Dictionary<string, double> { ["a"] = 0.95, ["recal"] = 0.99 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonFiniteMeasurement_Throws(double value)
    {
        Assert.Throws<ArgumentException>(() => EvalVerdict.Evaluate(
            [Metric("a", Comparison.AtLeast, 0.9)],
            new Dictionary<string, double> { ["a"] = value }));
    }

    // ───────────────────────────── Comparisons at the boundary ─────────────────────────────

    [Theory]
    [InlineData("AtLeast", 0.90, 0.90, true)]
    [InlineData("AtLeast", 0.90, 0.8999, false)]
    [InlineData("AtMost", 0.10, 0.10, true)]
    [InlineData("AtMost", 0.10, 0.1001, false)]
    [InlineData("Below", 120, 119.99, true)]
    [InlineData("Below", 120, 120, false)] // SC-005 says "under 2 minutes"
    public void Comparisons_HonourTheirBoundary(string comparison, double threshold, double value, bool met)
    {
        // The enum is internal to the harness, so theory data names it rather than passing it.
        Assert.Equal(met, Metric("m", Enum.Parse<Comparison>(comparison), threshold).IsMetBy(value));
    }

    // ───────────────────────────── Catalog matches the specification ─────────────────────────────

    /// <summary>
    /// The thresholds are the specification's numbers (quickstart §6). Pinned here so a change
    /// to a bar is a visible, reviewed edit rather than a drift.
    /// </summary>
    [Theory]
    [InlineData("photo.analysis", MetricCatalog.RoomClassificationAccepted, "AtLeast", 0.90, "SC-002")]
    [InlineData("photo.analysis", MetricCatalog.Latency30PhotosP90Seconds, "Below", 120.0, "SC-005")]
    [InlineData("photo.analysis", MetricCatalog.TokensPerListing, "AtMost", 200_000.0, "SC-006")]
    [InlineData("photo.analysis", MetricCatalog.ImagesPerListing, "AtMost", 120.0, "SC-006")]
    [InlineData("compliance.judge", MetricCatalog.FairHousingRecall, "AtLeast", 0.95, "SC-003")]
    [InlineData("compliance.judge", MetricCatalog.FairHousingFalsePositiveRate, "AtMost", 0.10, "SC-003")]
    [InlineData("listing.content", MetricCatalog.TokensPerListing, "AtMost", 200_000.0, "SC-006")]
    public void Catalog_CarriesTheSpecifiedThresholds(
        string task,
        string key,
        string comparison,
        double threshold,
        string source)
    {
        var metric = Catalog.For(task).Single(candidate => candidate.Key == key);

        Assert.Equal(Enum.Parse<Comparison>(comparison), metric.Comparison);
        Assert.Equal(threshold, metric.Threshold);
        Assert.Equal(source, metric.Source);
    }

    /// <summary>
    /// FR-015's schema-validity threshold has not been set by the specification, so it is null
    /// — and therefore, today, no chat model can pass. Research R27 records the decision needed.
    /// </summary>
    [Theory]
    [InlineData("photo.analysis")]
    [InlineData("compliance.judge")]
    [InlineData("listing.content")]
    public void SchemaValidity_IsRequired_AndHasNoInventedThreshold(string task)
    {
        var metric = Catalog.For(task).Single(candidate => candidate.Key == MetricCatalog.SchemaValidityRate);

        Assert.Null(metric.Threshold);
    }

    [Fact]
    public void Catalog_CoversExactlyTheRegistryTasks()
    {
        // 0008_ai_platform.sql: registry_task_key_known.
        Assert.Equal(
            ["compliance.judge", "listing.content", "photo.analysis", "photo.embedding"],
            Catalog.Tasks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ABudgetOverride_ChangesTheCostThresholds()
    {
        var catalog = new MetricCatalog(new EvalBudget(50_000, 40));

        Assert.Equal(
            50_000,
            catalog.For("listing.content").Single(metric => metric.Key == MetricCatalog.TokensPerListing).Threshold);
    }

    // ───────────────────────────── Suites ─────────────────────────────

    private sealed class FakeSuite(string taskKey) : IEvalSuite
    {
        public string TaskKey { get; } = taskKey;

        public Task<IReadOnlyDictionary<string, double>> RunAsync(
            RegistryTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    }

    [Fact]
    public void TwoSuitesForOneTask_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => EvalSuites.Build(
            [new FakeSuite("photo.analysis"), new FakeSuite("photo.analysis")]));
    }

    // ───────────────────────────── Result document ─────────────────────────────

    [Fact]
    public void ResultDocument_NamesTheModelTheBarsAndEachOutcome()
    {
        var target = new RegistryTarget(Guid.NewGuid(), "compliance.judge", "hf", "org/model", null, false);
        var (verdict, results) = EvalVerdict.Evaluate(
            Catalog.For("compliance.judge"),
            new Dictionary<string, double>
            {
                [MetricCatalog.FairHousingRecall] = 0.97,
                [MetricCatalog.FairHousingFalsePositiveRate] = 0.2,
            });

        var json = RegistryResultWriter.BuildResultDocument(target, verdict, results, DateTimeOffset.UnixEpoch);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(RegistryResultWriter.ResultFormatVersion, root.GetProperty("format").GetInt32());
        Assert.Equal("failed", root.GetProperty("verdict").GetString());
        Assert.Equal("org/model", root.GetProperty("model").GetProperty("modelId").GetString());

        var outcomes = root.GetProperty("metrics")
            .EnumerateArray()
            .ToDictionary(
                metric => metric.GetProperty("key").GetString()!,
                metric => metric.GetProperty("outcome").GetString());

        Assert.Equal("passed", outcomes[MetricCatalog.FairHousingRecall]);
        Assert.Equal("failed", outcomes[MetricCatalog.FairHousingFalsePositiveRate]);
        Assert.Equal("not_measured", outcomes[MetricCatalog.SchemaValidityRate]);
    }

    // ───────────────────────────── Command line ─────────────────────────────

    [Fact]
    public void Options_WriteWithoutActor_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(
        [
            "--registry-id", Guid.NewGuid().ToString(), "--write", "--connection", "Host=x",
        ]));

        Assert.Contains("--actor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_WriteWithoutRegistryId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(
            ["--task", "photo.analysis", "--write", "--actor", Guid.NewGuid().ToString()]));
    }

    [Fact]
    public void Options_UnknownTask_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => HarnessOptions.Parse(["--task", "photo.staging"]));
    }

    [Fact]
    public void Options_TaskAll_ParsesForTheCriteriaListing()
    {
        var options = HarnessOptions.Parse(["--task", "all"]);

        Assert.Equal("all", options.Task);
        Assert.Null(options.RegistryId);
        Assert.False(options.Write);
    }
}
