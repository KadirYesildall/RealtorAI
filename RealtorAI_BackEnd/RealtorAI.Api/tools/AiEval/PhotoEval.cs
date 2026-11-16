using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Libs.Ai;

namespace AiEval;

/// <summary>
/// One labelled photograph of the room-type evaluation set, as committed in
/// <c>EvalSets/photo.analysis/rooms-v1.json</c>. The image itself is not committed; it is fetched
/// into the <see cref="EvalSetCache"/> and verified against <paramref name="Sha256"/>.
/// </summary>
/// <param name="Id">Stable identifier (<c>kitchen-01</c>), unique in the set.</param>
/// <param name="Label">The room type an agent would accept unchanged: a value of the active prompt schema's <c>roomType</c> enum.</param>
/// <param name="LabelConfidence"><see cref="PhotoEval.Clear"/> or <see cref="PhotoEval.Ambiguous"/>.</param>
/// <param name="SourcePage">The page that states the photo's author and license.</param>
/// <param name="FileUrl">The exact file that was hashed — the one the fetch command downloads.</param>
/// <param name="License">One of the forms <see cref="PhotoEval.IsAllowedLicense"/> accepts.</param>
/// <param name="LicenseUrl">The license's text, or the page stating the public-domain status.</param>
/// <param name="Author">As the source page credits it (the A of TASL).</param>
/// <param name="Title">As the source page titles it (the T of TASL).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file's bytes.</param>
/// <param name="Notes">What the photo shows and, for an ambiguous label, which other label is defensible and why.</param>
internal sealed record RoomPhoto(
    string Id,
    string Label,
    string LabelConfidence,
    string SourcePage,
    string FileUrl,
    string License,
    string LicenseUrl,
    string Author,
    string Title,
    string Sha256,
    string? Notes)
{
    internal bool IsAmbiguous => LabelConfidence == PhotoEval.Ambiguous;

    internal RemoteEvalFile File => new(Id, FileUrl, Sha256);
}

/// <summary>One slot of the evaluation run: which pass over the set, and which photo.</summary>
internal sealed record ScheduledPhoto(int Pass, int PhotoIndex);

/// <summary>What the model said about one photo in one pass.</summary>
internal sealed record RoomPhotoResult(RoomPhoto Photo, int Pass, EvalCall Call)
{
    /// <summary>The model's room type, or null when the call produced no schema-valid answer.</summary>
    internal string? Predicted => Call.Document?["roomType"]?.GetValue<string>();

    /// <summary>
    /// The agent would keep the model's room type unchanged: it equals the label. A call with no
    /// valid answer is not accepted — production records the photo as failed and the agent
    /// classifies it by hand, which is a change.
    /// </summary>
    internal bool Accepted => Predicted is { } predicted && predicted == Photo.Label;
}

/// <summary>
/// T156 — measures SC-002 and SC-005 for a <c>photo.analysis</c> model, with the call-level
/// metrics of T158 taken on the same calls: room classification accepted rate, JSON-schema
/// validity (FR-015), 30-photo latency at p90, and tokens and images per listing (SC-006).
///
/// <para>
/// <b>Production's path.</b> Each photo is read from the cache, verified against the manifest's
/// SHA-256, normalized by the production <c>ImageNormalizer</c>
/// (<see cref="EvalModelInvoker.PrepareImage"/>) and analysed by <see cref="PhotoListingRunner"/>:
/// one call per photo with <c>PhotoAnalysisTask.BuildInput</c> beside it, a listing's photos one
/// after another, as <c>PhotoAnalysisStep</c> does.
/// </para>
/// <para>
/// <b>Room classification accepted rate</b> = analyses whose <c>roomType</c> equals the label /
/// all analyses. SC-002 reads "accepted without change"; the label is the room type an agent
/// would accept, so a different answer is a change. An answer that failed validation twice counts
/// as not accepted (see <see cref="RoomPhotoResult.Accepted"/>), and it also counts against the
/// schema-validity rate.
/// </para>
/// <para>
/// <b>Ambiguous labels are scored, not dropped.</b> About a quarter of the set is marked
/// <see cref="Ambiguous"/>: photos where another room type is defensible (a primary bedroom with
/// no visible primary cue, a pool mostly hidden by the garden). Dropping them would score the
/// model only on the easy photos and raise the rate; accepting any defensible answer would need a
/// list of acceptable alternatives that nobody has reviewed. So they count exactly like clear
/// items, against their one label, which can only lower the rate. The breakdown prints the rate
/// on clear and ambiguous items separately, so a reviewer can see how much of a miss rests on
/// ambiguous labels. Changing how an item is scored means relabelling it or removing it in a new,
/// reviewed version of the set — never a rule here.
/// </para>
/// <para>
/// <b>Latency sampling.</b> SC-005 is a p90 over 30-photo listings, and the set has far fewer
/// than 300 photos, so one pass yields only a handful of listings: 131 photos make 4, and a
/// nearest-rank p90 of 4 values is simply the slowest one. The run therefore makes
/// <see cref="PassesFor"/> passes over the set — enough for at least
/// <see cref="MinimumLatencyListings"/> complete listings — each pass in its own deterministic
/// order (<see cref="Schedule"/>), and runs them as one stream that <see cref="PhotoListingRunner"/>
/// cuts into consecutive 30-photo listings. For 131 photos that is 3 passes, 393 analyses, 13
/// listings, and 3 analyses outside any complete listing (still scored). Every photo is analysed
/// the same number of times, so repetition does not change any photo's weight in the accuracy
/// rate; it adds a second and third look at each photo, which the breakdown uses to report
/// answers that changed between passes.
/// </para>
/// <para>
/// <b>What that sample does not establish.</b> Thirteen listings is a small sample for a tail
/// statistic: the p90 is the 12th slowest of 13, and one slow listing more or less moves it a
/// lot. The listings are not independent either — they reuse the same photos, and they run
/// back to back in one session against one provider at one time of day, so they say nothing
/// about provider load at other times. A provider that caches repeated request prefixes could
/// also answer later passes faster than first sight of a photo. The figure is a measurement of
/// this run, printed with its sample size; it is not a confidence statement about production.
/// </para>
/// <para>
/// <b>Incomplete, never partial.</b> If any photo is missing from the cache or fails its hash,
/// no model is called and no metric is reported: a rate over the photos that happened to be
/// present would be a rate over a different set from the one that was reviewed. The reason names
/// the files and the command that fetches them, and <see cref="EvalVerdict"/> reports the run
/// incomplete. A provider failure on any call likewise withholds every metric
/// (<see cref="Measure"/>).
/// </para>
/// </summary>
internal sealed class PhotoEval : IEvalSuite
{
    /// <summary>The shipped evaluation set this suite measures on.</summary>
    internal const string SetFileName = "rooms-v1.json";

    internal const string Clear = "clear";
    internal const string Ambiguous = "ambiguous";

    /// <summary>
    /// The fewest complete listings the latency sample is built to contain. Ten is the smallest
    /// sample whose nearest-rank p90 (rank ⌈0.9·n⌉) is not simply its maximum, so it is the least
    /// for which "p90" means more than "the slowest listing seen". It sets how many passes the
    /// run makes and nothing else: no measured value is compared with it, and it is not an
    /// acceptance threshold (those are <see cref="MetricCatalog"/>'s, from the specification).
    /// </summary>
    internal const int MinimumLatencyListings = 10;

    /// <summary>The call-level metrics this task's catalog defines (T158).</summary>
    private static readonly string[] CallLevelMetrics =
    [
        MetricCatalog.SchemaValidityRate,
        MetricCatalog.Latency30PhotosP90Seconds,
        MetricCatalog.TokensPerListing,
        MetricCatalog.ImagesPerListing,
    ];

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Licenses a photo may carry: CC0 1.0, CC BY 2.0–4.0, or a public-domain status as the source
    /// page states it (<c>Public-Domain (PD-self)</c>, <c>Public-Domain (PD-USGov-NPS)</c>, …).
    /// Each permits use without permission, at most with attribution, which ATTRIBUTION.md gives.
    /// NonCommercial, NoDerivatives and ShareAlike terms are excluded by construction: they are
    /// not in the pattern.
    /// </summary>
    private static readonly Regex AllowedLicensePattern = new(
        @"^(CC0-1\.0|CC-BY-(2\.0|2\.5|3\.0|4\.0)|Public-Domain \([^()]+\))$",
        RegexOptions.CultureInvariant);

    private readonly Func<EvalSet> loadSet;
    private readonly EvalSetCache cache;
    private readonly TextWriter report;

    /// <summary>The shipped suite: the committed set, the default cache, reported to the console.</summary>
    internal PhotoEval()
        : this(
            () => EvalSet.LoadShipped(AiTaskKey.PhotoAnalysis, SetFileName),
            new EvalSetCache(EvalSetCache.DefaultRoot()),
            Console.Out)
    {
    }

    /// <param name="loadSet">Loads the set to measure on. Tests pass a manifest written for the test.</param>
    /// <param name="cache">Where the set's photos were fetched to.</param>
    /// <param name="report">Where the per-room breakdown, the misses and the latency sample are written.</param>
    internal PhotoEval(Func<EvalSet> loadSet, EvalSetCache cache, TextWriter report)
    {
        ArgumentNullException.ThrowIfNull(loadSet);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(report);

        this.loadSet = loadSet;
        this.cache = cache;
        this.report = report;
    }

    public string TaskKey => AiTaskKey.PhotoAnalysis;

    public async Task<SuiteRun> RunAsync(EvalModelInvoker model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Target.TaskKey != TaskKey)
        {
            throw new InvalidOperationException(
                $"The photo suite evaluates '{TaskKey}' models; registry entry {model.Target.Id} serves '{model.Target.TaskKey}'.");
        }

        var set = loadSet();
        var photos = ReadPhotos(set);
        RequireLabelsInSchema(photos, model.Prompt.OutputSchema, set.Name);

        var originals = new List<byte[]>(photos.Count);
        var missing = new List<RoomPhoto>();
        var mismatched = new List<RoomPhoto>();

        foreach (var photo in photos)
        {
            switch (cache.TryRead(TaskKey, photo.File, out var content))
            {
                case CachedFileState.Verified:
                    originals.Add(content!);
                    break;
                case CachedFileState.Missing:
                    missing.Add(photo);
                    break;
                default:
                    mismatched.Add(photo);
                    break;
            }
        }

        if (missing.Count > 0 || mismatched.Count > 0)
        {
            return new SuiteRun([set], new Dictionary<string, double>(), [CacheIncompleteReason(cache.Root, set, photos.Count, missing, mismatched)]);
        }

        var images = originals.Select(original => model.PrepareImage(original)).ToList();
        var schedule = Schedule(photos, MetricCatalog.LatencyListingPhotoCount, MinimumLatencyListings);

        var run = await PhotoListingRunner.RunAsync(
            model,
            [.. schedule.Select(slot => images[slot.PhotoIndex])],
            cancellationToken);

        var results = run.Photos
            .Select(photo => new RoomPhotoResult(
                photos[schedule[photo.Index].PhotoIndex],
                schedule[photo.Index].Pass,
                photo.Call))
            .ToList();

        var measurement = Measure(results, run.Listings);

        WriteBreakdown(set, photos.Count, results, run.Listings);

        return new SuiteRun([set], measurement.Values, measurement.Unmeasured);
    }

    /// <summary>
    /// The task's five metrics from the per-analysis results and the complete listings. Nothing
    /// here compares a value with a threshold; <see cref="EvalVerdict"/> does.
    /// </summary>
    internal static CallMeasurement Measure(IReadOnlyList<RoomPhotoResult> results, IReadOnlyList<EvalListingRun> listings)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(listings);

        var callLevel = CallMetrics.Measure([.. results.Select(result => result.Call.Record)], listings, CallLevelMetrics);

        var values = new Dictionary<string, double>(callLevel.Values, StringComparer.Ordinal);
        var unmeasured = new List<string>(callLevel.Unmeasured);

        if (results.Any(result => result.Call.Record.Outcome == EvalCallOutcome.ProviderFailed))
        {
            unmeasured.Add(
                $"{MetricCatalog.RoomClassificationAccepted}: some photos were never analysed, and scoring the rest "
                + "would describe the photos that got through, not the set. Run the evaluation again when the "
                + "provider is healthy.");
        }
        else if (results.Count == 0)
        {
            unmeasured.Add($"{MetricCatalog.RoomClassificationAccepted}: no photo was analysed.");
        }
        else
        {
            values[MetricCatalog.RoomClassificationAccepted] = AcceptedRate(results);
        }

        return new CallMeasurement(values, unmeasured);
    }

    /// <summary>Accepted analyses over all analyses, ambiguous labels included.</summary>
    /// <exception cref="ArgumentException">No results — a rate over none is undefined.</exception>
    internal static double AcceptedRate(IReadOnlyList<RoomPhotoResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            throw new ArgumentException("A rate over no analyses is undefined.", nameof(results));
        }

        return (double)results.Count(result => result.Accepted) / results.Count;
    }

    /// <summary>How many passes over <paramref name="photoCount"/> photos give at least <paramref name="minimumListings"/> complete listings.</summary>
    internal static int PassesFor(int photoCount, int listingSize, int minimumListings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(photoCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listingSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumListings);

        var needed = (long)listingSize * minimumListings;

        return (int)((needed + photoCount - 1) / photoCount);
    }

    /// <summary>
    /// The order photos are analysed in: <see cref="PassesFor"/> passes, each containing every
    /// photo exactly once.
    ///
    /// <para>
    /// Within pass <c>p</c>, photos are ordered by the SHA-256 of <c>"p:id"</c>. That is
    /// deterministic — the same set always gives the same listings, so two models are timed on
    /// identical listings and a run can be repeated — and it mixes room types the way a real
    /// listing does, where the manifest's grouped order would make a listing of thirty kitchens.
    /// It depends on nothing but the ids, not on a random-number implementation that could
    /// change between runtime versions. Each pass gets a different order, so the listings of one
    /// pass are not the listings of the next.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ScheduledPhoto> Schedule(IReadOnlyList<RoomPhoto> photos, int listingSize, int minimumListings)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var passes = PassesFor(photos.Count, listingSize, minimumListings);
        var schedule = new List<ScheduledPhoto>(passes * photos.Count);

        for (var pass = 1; pass <= passes; pass++)
        {
            var order = Enumerable.Range(0, photos.Count)
                .OrderBy(index => OrderKey(pass, photos[index].Id), StringComparer.Ordinal)
                .ThenBy(index => index);

            schedule.AddRange(order.Select(index => new ScheduledPhoto(pass, index)));
        }

        return schedule;
    }

    /// <summary>
    /// The set's photos, checked field by field. A malformed item would silently change the
    /// rate's denominator or put an unlicensed photo in front of a model, so it stops the run
    /// before any model is called — and the fetch command refuses the set too.
    /// </summary>
    /// <exception cref="InvalidDataException">An item is missing a field, uses an unknown value, or repeats an id or hash.</exception>
    internal static IReadOnlyList<RoomPhoto> ReadPhotos(EvalSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var photos = set.ItemsAs<RoomPhoto>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < photos.Count; index++)
        {
            var photo = photos[index];
            var where = $"Evaluation set {set.Name}: items[{index.ToString(CultureInfo.InvariantCulture)}]";

            Require(where, "id", photo.Id);
            Require(where, "label", photo.Label);
            Require(where, "author", photo.Author);
            Require(where, "title", photo.Title);

            if (!ids.Add(photo.Id))
            {
                throw new InvalidDataException($"{where}: id '{photo.Id}' is used more than once.");
            }

            if (photo.LabelConfidence is not (Clear or Ambiguous))
            {
                throw new InvalidDataException(
                    $"{where} ({photo.Id}): labelConfidence must be '{Clear}' or '{Ambiguous}', not '{photo.LabelConfidence}'.");
            }

            if (photo.Sha256 is null || !Sha256Pattern.IsMatch(photo.Sha256))
            {
                throw new InvalidDataException($"{where} ({photo.Id}): sha256 must be 64 lower-case hex characters.");
            }

            if (!hashes.Add(photo.Sha256))
            {
                // The same bytes twice would be one photo counted twice, and its cache file
                // would be shared by two labels.
                throw new InvalidDataException($"{where} ({photo.Id}): sha256 {photo.Sha256} is already used by another item.");
            }

            if (!IsAllowedLicense(photo.License))
            {
                throw new InvalidDataException(
                    $"{where} ({photo.Id}): license '{photo.License}' is not CC0 1.0, CC BY 2.0-4.0, or a stated public-domain status.");
            }

            RequireUrl(where, photo.Id, "fileUrl", photo.FileUrl, httpsOnly: true);
            RequireUrl(where, photo.Id, "sourcePage", photo.SourcePage, httpsOnly: true);

            // Creative Commons' own license URLs are often written with http://.
            RequireUrl(where, photo.Id, "licenseUrl", photo.LicenseUrl, httpsOnly: false);
        }

        return photos;
    }

    /// <summary>Whether <paramref name="license"/> is one the set may use (see <see cref="AllowedLicensePattern"/>).</summary>
    internal static bool IsAllowedLicense(string? license) =>
        license is not null && AllowedLicensePattern.IsMatch(license);

    /// <summary>The room types the active prompt's schema allows: its <c>roomType</c> enum.</summary>
    /// <exception cref="InvalidDataException">The schema has no such enum.</exception>
    internal static IReadOnlySet<string> RoomTypesOf(string outputSchema)
    {
        ArgumentNullException.ThrowIfNull(outputSchema);

        if (JsonNode.Parse(outputSchema)?["properties"]?["roomType"]?["enum"] is not JsonArray values)
        {
            throw new InvalidDataException(
                "The photo.analysis schema has no properties.roomType.enum, so a room-type label cannot be scored against it.");
        }

        return values.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every label must be an answer the model is allowed to give. Checked against the schema the
    /// model is actually sent, so a prompt version that renames or drops a room type stops the
    /// run instead of scoring every such photo as a miss the model could not have avoided.
    /// </summary>
    /// <exception cref="InvalidDataException">A label is not in the schema's enum.</exception>
    internal static void RequireLabelsInSchema(IReadOnlyList<RoomPhoto> photos, string outputSchema, string setName)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var roomTypes = RoomTypesOf(outputSchema);
        var unknown = photos.Where(photo => !roomTypes.Contains(photo.Label)).ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidDataException(
                $"Evaluation set {setName}: labels the active photo.analysis schema does not allow: "
                + string.Join(", ", unknown.Select(photo => $"{photo.Id} '{photo.Label}'"))
                + $". Allowed: {string.Join(", ", roomTypes)}.");
        }
    }

    /// <summary>The set's files, for the fetch command.</summary>
    internal static IReadOnlyList<RemoteEvalFile> RemoteFiles(EvalSet set) =>
        [.. ReadPhotos(set).Select(photo => photo.File)];

    private static string CacheIncompleteReason(
        string cacheRoot,
        EvalSet set,
        int total,
        List<RoomPhoto> missing,
        List<RoomPhoto> mismatched)
    {
        static string Ids(IReadOnlyList<RoomPhoto> photos) =>
            string.Join(", ", photos.Take(10).Select(photo => photo.Id)) + (photos.Count > 10 ? ", …" : string.Empty);

        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

        var parts = new List<string>();

        if (missing.Count > 0)
        {
            parts.Add($"{Count(missing.Count)} missing ({Ids(missing)})");
        }

        if (mismatched.Count > 0)
        {
            parts.Add($"{Count(mismatched.Count)} whose bytes do not match the manifest's SHA-256 ({Ids(mismatched)})");
        }

        return $"evaluation set {set.Name}: {string.Join(" and ", parts)} of {Count(total)} photos in the cache at "
            + $"{Path.Combine(cacheRoot, AiTaskKey.PhotoAnalysis)}. No model was called and nothing is reported, "
            + "because a rate over the photos that happen to be present is a rate over a different set. Run "
            + $"'aieval --fetch-eval-sets' (the cache is {EvalSetCache.DirectoryEnvironmentVariable} when set, "
            + "otherwise under local application data).";
    }

    private static string OrderKey(int pass, string id) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            pass.ToString(CultureInfo.InvariantCulture) + ":" + id)));

    private static void Require(string where, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{where}: {name} is missing or blank.");
        }
    }

    private static void RequireUrl(string where, string id, string name, string? value, bool httpsOnly)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidDataException(
                $"{where} ({id}): {name} must be an absolute {(httpsOnly ? "https" : "http(s)")} URL, not '{value}'.");
        }
    }

    /// <summary>
    /// The evidence behind the five figures: per room type and label confidence, the misses,
    /// answers that changed between passes, and the latency sample's size and spread. Printed so
    /// a person reviewing the set or the model can check what the rates rest on.
    /// </summary>
    private void WriteBreakdown(
        EvalSet set,
        int photoCount,
        List<RoomPhotoResult> results,
        IReadOnlyList<EvalListingRun> listings)
    {
        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

        string Share(IEnumerable<RoomPhotoResult> group)
        {
            var list = group.ToList();
            return $"{Count(list.Count(result => result.Accepted))}/{Count(list.Count)}";
        }

        var passes = results.Count == 0 ? 0 : results.Max(result => result.Pass);

        report.WriteLine(
            $"Room classification on {set.Name}: {Share(results)} analyses accepted "
            + $"({Count(passes)} passes over {Count(photoCount)} photos). Clear labels {Share(results.Where(result => !result.Photo.IsAmbiguous))}, "
            + $"ambiguous labels {Share(results.Where(result => result.Photo.IsAmbiguous))}; both count.");

        foreach (var room in results.GroupBy(result => result.Photo.Label).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            report.WriteLine(
                $"  {room.Key,-16} clear {Share(room.Where(result => !result.Photo.IsAmbiguous)),-8} "
                + $"ambiguous {Share(room.Where(result => result.Photo.IsAmbiguous))}");
        }

        var misses = results
            .Where(result => !result.Accepted)
            .GroupBy(result => result.Photo.Id, StringComparer.Ordinal)
            .Select(group => $"{group.Key} ({group.First().Photo.Label}{(group.First().Photo.IsAmbiguous ? ", ambiguous" : string.Empty)} → "
                + string.Join("/", group.Select(result => result.Predicted ?? "no valid answer").Distinct(StringComparer.Ordinal))
                + $" ×{Count(group.Count())})")
            .ToList();

        if (misses.Count > 0)
        {
            report.WriteLine($"  Not accepted: {string.Join("; ", misses)}");
        }

        var changed = results
            .GroupBy(result => result.Photo.Id, StringComparer.Ordinal)
            .Where(group => group.Select(result => result.Predicted).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (passes > 1)
        {
            report.WriteLine(
                $"  Answer changed between passes for {Count(changed.Count)} of {Count(photoCount)} photos"
                + (changed.Count > 0 ? $": {string.Join(", ", changed)}" : "."));
        }

        if (listings.Count > 0)
        {
            var seconds = listings.Select(listing => listing.WallTime.TotalSeconds).Order().ToList();
            var rank = (int)Math.Ceiling(CallMetrics.LatencyPercentile * seconds.Count / 100);
            var outside = results.Count - (listings.Count * MetricCatalog.LatencyListingPhotoCount);

            report.WriteLine(
                $"  Latency sample: {Count(listings.Count)} listings of {Count(MetricCatalog.LatencyListingPhotoCount)} photos "
                + $"({Count(outside)} analyses outside a complete listing). p90 is rank {Count(rank)} of {Count(seconds.Count)} "
                + $"(nearest rank). Listing seconds: min {seconds[0].ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"median {CallMetrics.Percentile(seconds, 50).ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"max {seconds[^1].ToString("0.0", CultureInfo.InvariantCulture)}. A small, repeated-photo sample; see PhotoEval.");
        }
    }
}
