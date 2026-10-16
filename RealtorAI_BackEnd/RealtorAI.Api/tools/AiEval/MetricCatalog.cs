using System.Globalization;

namespace AiEval;

/// <summary>How a measured value is compared with its threshold.</summary>
internal enum Comparison
{
    /// <summary>The value must be at least the threshold (rates that should be high).</summary>
    AtLeast,

    /// <summary>The value must be at most the threshold (rates and budgets that should be low).</summary>
    AtMost,

    /// <summary>The value must be strictly below the threshold (latency limits stated as "under").</summary>
    Below,
}

/// <summary>
/// One acceptance criterion a model must meet before it may be enabled.
/// </summary>
/// <param name="Key">Stable identifier, recorded in <c>eval_result</c>.</param>
/// <param name="Description">What is measured, in words an operator can check against the data.</param>
/// <param name="Source">The requirement that sets the threshold, so the number is traceable.</param>
/// <param name="Comparison">Direction of the test.</param>
/// <param name="Threshold">
/// The bar, or null where the specification has not set one yet. A null threshold cannot be
/// met — see <see cref="EvalVerdict"/>.
/// </param>
internal sealed record EvalMetric(
    string Key,
    string Description,
    string Source,
    Comparison Comparison,
    double? Threshold)
{
    internal bool IsMetBy(double value) => Threshold is { } threshold && Comparison switch
    {
        Comparison.AtLeast => value >= threshold,
        Comparison.AtMost => value <= threshold,
        Comparison.Below => value < threshold,
        _ => throw new InvalidOperationException($"Unhandled comparison {Comparison}."),
    };

    internal string FormatRule() => Threshold is { } threshold
        ? Comparison switch
        {
            Comparison.AtLeast => "≥ ",
            Comparison.AtMost => "≤ ",
            Comparison.Below => "< ",
            _ => "? ",
        } + threshold.ToString("0.###", CultureInfo.InvariantCulture)
        : "threshold not set";
}

/// <summary>
/// The acceptance metrics per registry task, from specs/002-ai-listing-launch/quickstart.md §6.
///
/// <para>
/// <b>Thresholds are copied from the specification, not chosen here.</b> Where the
/// specification has not set one, the threshold is null and the metric cannot pass. That is
/// deliberate: inventing a number to unblock enablement would turn VIII.4's "passes the
/// evaluation harness" into "passes whatever bar the harness author picked". Two such gaps
/// exist today and are recorded in research R27:
/// </para>
/// <list type="bullet">
///   <item>JSON-schema validity rate (FR-015): quickstart says "report + threshold set here",
///   and no threshold has been set.</item>
///   <item><c>photo.embedding</c>: the specification defines no acceptance metric for the
///   embedding model, so the catalog has none and such a model cannot pass.</item>
/// </list>
/// </summary>
internal sealed class MetricCatalog
{
    internal const string RoomClassificationAccepted = "room_classification_accepted_rate";
    internal const string FairHousingRecall = "fair_housing_recall";
    internal const string FairHousingFalsePositiveRate = "fair_housing_false_positive_rate";
    internal const string SchemaValidityRate = "schema_validity_rate";
    internal const string Latency30PhotosP90Seconds = "analysis_latency_30_photos_p90_seconds";
    internal const string TokensPerListing = "tokens_per_listing";
    internal const string ImagesPerListing = "images_per_listing";

    private readonly Dictionary<string, IReadOnlyList<EvalMetric>> byTask;

    /// <param name="budget">
    /// The per-listing budget a model's measured consumption must fit within (SC-006). Taken
    /// from the same configuration as the API's default budget, enforced in tokens and images
    /// until provider pricing is verified (research R16).
    /// </param>
    internal MetricCatalog(EvalBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var schemaValidity = new EvalMetric(
            SchemaValidityRate,
            "Share of outputs that validate against the task's JSON Schema on the first attempt.",
            "FR-015",
            Comparison.AtLeast,
            Threshold: null);

        var tokens = new EvalMetric(
            TokensPerListing,
            "Mean tokens (in + out) consumed per evaluated listing.",
            "SC-006",
            Comparison.AtMost,
            budget.TokensPerListing);

        byTask = new Dictionary<string, IReadOnlyList<EvalMetric>>(StringComparer.Ordinal)
        {
            ["photo.analysis"] =
            [
                new EvalMetric(
                    RoomClassificationAccepted,
                    "Share of photos whose predicted room type matches the label.",
                    "SC-002",
                    Comparison.AtLeast,
                    0.90),
                schemaValidity,
                new EvalMetric(
                    Latency30PhotosP90Seconds,
                    "90th-percentile wall time to analyse a 30-photo listing, in seconds.",
                    "SC-005",
                    Comparison.Below,
                    120),
                tokens,
                new EvalMetric(
                    ImagesPerListing,
                    "Mean images sent to the model per evaluated listing.",
                    "SC-006",
                    Comparison.AtMost,
                    budget.ImagesPerListing),
            ],

            ["compliance.judge"] =
            [
                new EvalMetric(
                    FairHousingRecall,
                    "Share of labelled Fair Housing violations the judge flags.",
                    "SC-003",
                    Comparison.AtLeast,
                    0.95),
                new EvalMetric(
                    FairHousingFalsePositiveRate,
                    "Share of compliant samples the judge flags anyway.",
                    "SC-003",
                    Comparison.AtMost,
                    0.10),
                schemaValidity,
            ],

            ["listing.content"] =
            [
                schemaValidity,
                tokens,
            ],

            ["photo.embedding"] = [],
        };
    }

    /// <summary>The task keys the registry accepts (0008's <c>registry_task_key_known</c>).</summary>
    internal IEnumerable<string> Tasks => byTask.Keys;

    internal bool IsKnownTask(string taskKey) => byTask.ContainsKey(taskKey);

    internal IReadOnlyList<EvalMetric> For(string taskKey) =>
        byTask.TryGetValue(taskKey, out var metrics)
            ? metrics
            : throw new ArgumentException($"Unknown task '{taskKey}'.", nameof(taskKey));
}

/// <summary>Per-listing consumption limits used by the cost metrics.</summary>
internal sealed record EvalBudget(double TokensPerListing, double ImagesPerListing)
{
    /// <summary>
    /// The API's configured defaults (<c>Ai:Budget</c> in appsettings.Development.json).
    /// Overridable on the command line for a brokerage with a different budget.
    /// </summary>
    internal static EvalBudget Default { get; } = new(200_000, 120);
}
