using System.Diagnostics;
using System.Globalization;
using Libs.Ai.Tasks;

namespace AiEval;

/// <summary>One evaluated listing: its calls, and the wall time the whole listing took.</summary>
/// <param name="Calls">Every call made for the listing, in order.</param>
/// <param name="WallTime">
/// Measured around the listing as a whole rather than summed from its calls, so whatever sits
/// between calls in the real step is inside the figure too.
/// </param>
internal sealed record EvalListingRun(IReadOnlyList<EvalCallRecord> Calls, TimeSpan WallTime);

/// <summary>One evaluation photo's call, in the order the photos were given.</summary>
internal sealed record EvalPhotoResult(int Index, EvalCall Call);

/// <summary>Everything a photo evaluation produced.</summary>
/// <param name="Photos">One result per photo, in input order — what a suite scores labels against.</param>
/// <param name="Listings">
/// The complete <see cref="MetricCatalog.LatencyListingPhotoCount"/>-photo listings. Photos past the
/// last complete listing are still in <paramref name="Photos"/> and <paramref name="Calls"/>, but
/// form no listing: a shorter run is not the listing SC-005 is stated for.
/// </param>
/// <param name="Calls">Every call's record, for the metrics computed over all calls.</param>
internal sealed record PhotoEvalRun(
    IReadOnlyList<EvalPhotoResult> Photos,
    IReadOnlyList<EvalListingRun> Listings,
    IReadOnlyList<EvalCallRecord> Calls);

/// <summary>
/// Runs evaluation photos through the model the way the pipeline's photo-analysis step does.
///
/// <para>
/// <b>What "the way the pipeline does" means here</b>, read from
/// <c>Pipeline/Steps/PhotoAnalysisStep.cs</c>: one call per photo, each photo sent alone as its
/// normalized JPEG with <see cref="PhotoAnalysisTask.BuildInput"/> beside it, and the photos of a
/// listing analysed <b>one after another</b> — the step is a sequential loop, and a listing's run
/// is one background job, so a listing never has two analysis calls in flight. The harness
/// therefore runs each listing's photos sequentially too. Running them concurrently would
/// measure a latency production never sees, and would make SC-005 easier to pass than it is.
/// </para>
/// <para>
/// Photo ids are fresh UUIDs, as uploads have; the model sees nothing else that identifies a
/// photo, so the evaluation input is indistinguishable from a production one.
/// </para>
/// </summary>
internal static class PhotoListingRunner
{
    internal static async Task<PhotoEvalRun> RunAsync(
        EvalModelInvoker model,
        IReadOnlyList<EvalImage> photos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(photos);

        var listingSize = MetricCatalog.LatencyListingPhotoCount;
        var results = new List<EvalPhotoResult>(photos.Count);
        var listings = new List<EvalListingRun>();

        for (var start = 0; start < photos.Count; start += listingSize)
        {
            var count = Math.Min(listingSize, photos.Count - start);
            var calls = new List<EvalCallRecord>(count);
            var listingClock = Stopwatch.StartNew();

            for (var index = start; index < start + count; index++)
            {
                var call = await model.InvokeAsync(
                    PhotoAnalysisTask.BuildInput(Guid.CreateVersion7()),
                    [photos[index]],
                    cancellationToken);

                results.Add(new EvalPhotoResult(index, call));
                calls.Add(call.Record);
            }

            listingClock.Stop();

            if (count == listingSize)
            {
                listings.Add(new EvalListingRun(calls, listingClock.Elapsed));
            }
        }

        return new PhotoEvalRun(results, listings, [.. results.Select(result => result.Call.Record)]);
    }
}

/// <summary>Call-level metric values, and why any requested metric has none.</summary>
internal sealed record CallMeasurement(
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyList<string> Unmeasured);

/// <summary>
/// T158 — turns call records into the catalog's call-level metrics: JSON-schema validity
/// (FR-015), 30-photo latency (SC-005), and per-listing consumption (SC-006).
///
/// <para>
/// <b>Fail closed on incomplete evidence.</b> A metric is reported only when every call it rests
/// on produced the evidence it needs. If any call failed at the provider, no call-level metric is
/// reported at all: dropping those calls would bias every figure towards the calls that happened
/// to get through, and counting them as invalid output would blame the model for an outage.
/// Likewise a token figure is withheld when a provider omitted usage, because the missing counts
/// would read as zero and understate consumption. A withheld metric is unmeasured, which
/// <see cref="EvalVerdict"/> never lets pass, and the reason is printed.
/// </para>
/// <para>
/// None of this sets or interprets a threshold. Values are compared with
/// <see cref="MetricCatalog"/>'s bars by <see cref="EvalVerdict"/> alone; the schema-validity
/// rate in particular is measured and reported even though its threshold is unset (R27).
/// </para>
/// </summary>
internal static class CallMetrics
{
    /// <summary>The percentile SC-005 is stated at.</summary>
    internal const double LatencyPercentile = 90;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        MetricCatalog.SchemaValidityRate,
        MetricCatalog.Latency30PhotosP90Seconds,
        MetricCatalog.TokensPerListing,
        MetricCatalog.ImagesPerListing,
    };

    /// <summary>
    /// Computes the requested call-level metrics.
    /// </summary>
    /// <param name="calls">Every call the suite made, for the schema-validity rate.</param>
    /// <param name="listings">The complete listings, for the per-listing metrics.</param>
    /// <param name="metricKeys">
    /// Which of the four call-level metrics the task's catalog defines. A key outside those four
    /// is a wiring error and throws.
    /// </param>
    internal static CallMeasurement Measure(
        IReadOnlyList<EvalCallRecord> calls,
        IReadOnlyList<EvalListingRun> listings,
        IReadOnlyCollection<string> metricKeys)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(metricKeys);

        var unknown = metricKeys.Where(key => !Known.Contains(key)).ToList();

        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                "Not call-level metrics: " + string.Join(", ", unknown)
                + ". A suite measures its own accuracy metrics and passes only these four here.",
                nameof(metricKeys));
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var unmeasured = new List<string>();

        var failed = calls.Where(call => call.Outcome == EvalCallOutcome.ProviderFailed).ToList();

        if (failed.Count > 0)
        {
            unmeasured.Add(
                $"{Count(failed.Count)} of {Count(calls.Count)} model calls failed at the provider "
                + $"(first: {failed[0].ProviderError}). {string.Join(", ", metricKeys)} are not "
                + "reported, because a sample missing those calls would not describe the model. "
                + "Run the evaluation again when the provider is healthy.");

            return new CallMeasurement(values, unmeasured);
        }

        if (metricKeys.Contains(MetricCatalog.SchemaValidityRate))
        {
            if (calls.Count == 0)
            {
                unmeasured.Add($"{MetricCatalog.SchemaValidityRate}: no model calls were made.");
            }
            else
            {
                values[MetricCatalog.SchemaValidityRate] = SchemaValidityRate(calls);
            }
        }

        var perListing = metricKeys
            .Where(key => key is MetricCatalog.Latency30PhotosP90Seconds
                or MetricCatalog.TokensPerListing
                or MetricCatalog.ImagesPerListing)
            .ToList();

        if (perListing.Count > 0 && listings.Count == 0)
        {
            unmeasured.Add(
                $"{string.Join(", ", perListing)}: no complete listing was evaluated. SC-005 is "
                + $"stated for {Count(MetricCatalog.LatencyListingPhotoCount)} photos, so the set "
                + "needs at least that many.");

            return new CallMeasurement(values, unmeasured);
        }

        if (metricKeys.Contains(MetricCatalog.Latency30PhotosP90Seconds))
        {
            values[MetricCatalog.Latency30PhotosP90Seconds] = LatencyP90Seconds(listings);
        }

        if (metricKeys.Contains(MetricCatalog.TokensPerListing))
        {
            var unreported = listings.Sum(listing => listing.Calls.Count(call => !call.UsageReported));

            if (unreported > 0)
            {
                unmeasured.Add(
                    $"{MetricCatalog.TokensPerListing}: the provider reported no token usage for "
                    + $"{Count(unreported)} calls, and counting those as zero would understate "
                    + "consumption.");
            }
            else
            {
                values[MetricCatalog.TokensPerListing] = TokensPerListing(listings);
            }
        }

        if (metricKeys.Contains(MetricCatalog.ImagesPerListing))
        {
            values[MetricCatalog.ImagesPerListing] = ImagesPerListing(listings);
        }

        return new CallMeasurement(values, unmeasured);
    }

    /// <summary>
    /// First-attempt valid calls over attempted calls (FR-015). "First attempt" because the
    /// metric describes the model: a response rescued by the retry still cost a second request
    /// and still means the model did not produce the shape when asked.
    /// </summary>
    /// <exception cref="ArgumentException">No calls, or a provider failure among them — neither has a rate.</exception>
    internal static double SchemaValidityRate(IReadOnlyList<EvalCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);

        if (calls.Count == 0)
        {
            throw new ArgumentException("A rate over no calls is undefined.", nameof(calls));
        }

        if (calls.Any(call => call.Outcome == EvalCallOutcome.ProviderFailed))
        {
            throw new ArgumentException(
                "A provider failure produced no output to validate, so it can be neither the "
                + "numerator nor the denominator of a validity rate.",
                nameof(calls));
        }

        return (double)calls.Count(call => call.FirstAttemptValid) / calls.Count;
    }

    /// <summary>The 90th-percentile listing wall time, in seconds (SC-005).</summary>
    internal static double LatencyP90Seconds(IReadOnlyList<EvalListingRun> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);

        return Percentile([.. listings.Select(listing => listing.WallTime.TotalSeconds)], LatencyPercentile);
    }

    /// <summary>Mean tokens (in + out, every attempt) per listing.</summary>
    internal static double TokensPerListing(IReadOnlyList<EvalListingRun> listings) =>
        MeanPerListing(listings, listing => listing.Calls.Sum(call => (double)call.Tokens));

    /// <summary>Mean images sent per listing, retries included.</summary>
    internal static double ImagesPerListing(IReadOnlyList<EvalListingRun> listings) =>
        MeanPerListing(listings, listing => listing.Calls.Sum(call => (double)call.ImagesSent));

    /// <summary>
    /// The nearest-rank percentile: the smallest observed value such that at least
    /// <paramref name="percentile"/>% of the values are at or below it — the value at 1-based
    /// rank ⌈p·n/100⌉ of the sorted sample.
    ///
    /// <para>
    /// <b>Why nearest-rank rather than interpolation.</b> It always returns a latency that was
    /// actually observed, and it never rounds down: with fewer than ten listings, p90 is the
    /// slowest one. For a gate, that conservative reading of a small sample is the right
    /// direction to err — an interpolated p90 of five listings would sit below the slowest
    /// listing that was really seen. Sample size is not a threshold the specification sets, so
    /// none is imposed here; the operator sees how many listings the set produced.
    /// </para>
    /// <para>
    /// For an integer <paramref name="percentile"/>, p·n is an exact integer, so the rank is
    /// computed without floating-point error at the boundaries (p90 of 10 is rank 9, not 10).
    /// </para>
    /// </summary>
    /// <param name="values">The sample. Not modified.</param>
    /// <param name="percentile">In (0, 100].</param>
    internal static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            throw new ArgumentException("A percentile of no values is undefined.", nameof(values));
        }

        if (double.IsNaN(percentile) || percentile <= 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Must be in (0, 100].");
        }

        if (values.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            throw new ArgumentException("Every value must be a finite number.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile * sorted.Length / 100);

        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    private static double MeanPerListing(IReadOnlyList<EvalListingRun> listings, Func<EvalListingRun, double> perListing)
    {
        ArgumentNullException.ThrowIfNull(listings);

        if (listings.Count == 0)
        {
            throw new ArgumentException("A mean over no listings is undefined.", nameof(listings));
        }

        return listings.Average(perListing);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
