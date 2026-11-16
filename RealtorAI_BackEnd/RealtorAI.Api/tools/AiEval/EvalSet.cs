using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Libs.Ai;

namespace AiEval;

/// <summary>Where an evaluation set's items came from, and on what terms they may be used.</summary>
/// <param name="Source">Where the items were obtained, specifically enough for someone else to find them.</param>
/// <param name="Rights">The license or permission under which the items may be used for evaluation.</param>
/// <param name="LabelledBy">Who assigned the labels.</param>
/// <param name="LabellingMethod">How the labels were assigned: the guide followed, the adjudication of disagreements.</param>
/// <param name="CreatedOn">When this version of the set was assembled.</param>
internal sealed record EvalSetProvenance(
    string Source,
    string Rights,
    string LabelledBy,
    string LabellingMethod,
    DateOnly CreatedOn);

/// <summary>Whether a person other than the labeller has checked this version of the set.</summary>
/// <param name="Reviewed">True only once the review is done and the set accepted.</param>
/// <param name="Reviewer">Who reviewed it. Required when <paramref name="Reviewed"/> is true.</param>
/// <param name="ReviewedAt">When. Required when <paramref name="Reviewed"/> is true.</param>
/// <param name="ItemsSha256">
/// The <see cref="EvalSet.ItemsHash"/> of the items the reviewer signed off. Required when
/// <paramref name="Reviewed"/> is true; it is what ties the review to the labels it covered.
/// </param>
/// <param name="Notes">What was checked, and anything found.</param>
internal sealed record EvalSetReview(
    bool Reviewed,
    string? Reviewer,
    DateTimeOffset? ReviewedAt,
    string? ItemsSha256,
    string? Notes);

/// <summary>
/// A labelled evaluation set: a versioned JSON manifest committed under
/// <c>tools/AiEval/EvalSets/&lt;task&gt;/</c>, shared by the photo (T156) and Fair Housing (T157)
/// suites.
///
/// <para>The manifest shape (format 1):</para>
/// <code>
/// {
///   "format": 1,
///   "id": "photo-rooms",                  // stable name of the set
///   "task": "photo.analysis",             // must match the directory it sits in
///   "version": 1,                         // a changed item is a new version
///   "description": "…",
///   "provenance": { "source": "…", "rights": "…", "labelledBy": "…",
///                   "labellingMethod": "…", "createdOn": "2026-11-16" },
///   "review": { "reviewed": false, "reviewer": null, "reviewedAt": null,
///               "itemsSha256": null, "notes": null },
///   "items": [ { … task-specific … } ]
/// }
/// </code>
///
/// <para>
/// <b>Why the review block exists.</b> SC-002 and SC-003 are accuracy claims, and an accuracy
/// figure is only as good as the labels it is scored against. A set nobody but its labeller has
/// checked can carry systematic label errors that make a weak model look good, and VIII.4 would
/// then enable that model on the strength of the harness. So an unreviewed set can be run — that
/// is how a set is developed — but it can never produce a recorded verdict; the gate is in
/// <see cref="EvalVerdict"/>.
/// </para>
/// <para>
/// <b>Why the review records a hash of the items.</b> A <c>"reviewed": true</c> that stayed
/// true through later edits would let a relabel, an added item or a swapped photo reach a
/// recorded verdict under a review that never saw it. So a reviewed manifest must carry
/// <c>review.itemsSha256</c>, the <see cref="ItemsHash"/> of the items as signed off (the harness
/// prints it on every run), and the set counts as reviewed only while the items still hash to
/// it. When they do not, the set loads — its measurements are still worth seeing — but
/// <see cref="IsReviewed"/> is false and <see cref="ReviewMismatch"/> says why, so
/// <see cref="EvalVerdict"/> returns Incomplete. A changed item therefore needs a new review in
/// code, not just a convention.
/// </para>
/// <para>
/// <b>Why the reviewed flag can only come from the file.</b> The constructor is private and the
/// type is a class with read-only properties rather than a record, so there is no
/// <c>with</c>-expression or initializer through which code could produce a reviewed set: the
/// only way to obtain one is <see cref="Load"/>, reading a manifest whose review block is
/// committed, diffable, and attributable in version control.
/// </para>
/// <para>
/// <b>Strict parsing.</b> Unknown properties are rejected, so a misspelt <c>"reviwed"</c> or
/// <c>"reviewer "</c> fails loudly instead of silently reading as absent.
/// </para>
/// </summary>
internal sealed partial class EvalSet
{
    /// <summary>The manifest format this loader reads.</summary>
    internal const int ManifestFormat = 1;

    /// <summary>The directory, beside the harness binary, that shipped sets are copied to.</summary>
    internal const string ShippedDirectoryName = "EvalSets";

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly JsonSerializerOptions ItemOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string directory;

    private EvalSet(
        string id,
        string taskKey,
        int version,
        string description,
        EvalSetProvenance provenance,
        EvalSetReview review,
        IReadOnlyList<JsonElement> items,
        string manifestPath,
        string contentHash,
        string itemsHash)
    {
        Id = id;
        TaskKey = taskKey;
        Version = version;
        Description = description;
        Provenance = provenance;
        Review = review;
        Items = items;
        ManifestPath = manifestPath;
        ContentHash = contentHash;
        ItemsHash = itemsHash;
        directory = Path.GetDirectoryName(manifestPath)!;
    }

    internal string Id { get; }

    internal string TaskKey { get; }

    internal int Version { get; }

    internal string Description { get; }

    internal EvalSetProvenance Provenance { get; }

    internal EvalSetReview Review { get; }

    /// <summary>The task-specific items, as written. Read them typed with <see cref="ItemsAs{T}"/>.</summary>
    internal IReadOnlyList<JsonElement> Items { get; }

    /// <summary>Full path of the manifest that was loaded.</summary>
    internal string ManifestPath { get; }

    /// <summary>
    /// Lower-case hex SHA-256 of the manifest with line endings normalised to <c>\n</c> — the
    /// same normalisation as prompt hashes, so a CRLF checkout hashes like an LF one.
    /// </summary>
    internal string ContentHash { get; }

    /// <summary>
    /// Lower-case hex SHA-256 of the items in canonical form (<see cref="ComputeItemsHash"/>):
    /// what <c>review.itemsSha256</c> must equal for the review to cover this set.
    /// </summary>
    internal string ItemsHash { get; }

    /// <summary>How the set is named in harness output and reasons: <c>id vN</c>.</summary>
    internal string Name => $"{Id} v{Version.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// True only when the manifest records a review <b>and</b> the items are the ones that
    /// review signed off.
    /// </summary>
    internal bool IsReviewed => Review.Reviewed && ReviewMismatch is null;

    /// <summary>
    /// Why a manifest marked reviewed does not count as reviewed — its items no longer hash to
    /// <c>review.itemsSha256</c> — or null when there is no such conflict.
    /// </summary>
    internal string? ReviewMismatch => Review.Reviewed && !string.Equals(Review.ItemsSha256, ItemsHash, StringComparison.Ordinal)
        ? $"its review covered items with sha256 {Review.ItemsSha256}, but the items now hash to "
          + $"{ItemsHash}. The labels changed after review; a changed set is a new version that "
          + "needs its own review."
        : null;

    /// <summary>
    /// Loads a set shipped with the harness: <c>EvalSets/&lt;task&gt;/&lt;fileName&gt;</c> beside
    /// the binary, where the project file copies every committed set.
    /// </summary>
    /// <exception cref="FileNotFoundException">The manifest is not there.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed or inconsistent.</exception>
    internal static EvalSet LoadShipped(string taskKey, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException(
                $"'{fileName}' must be a file name inside EvalSets/{taskKey}/, not a path.",
                nameof(fileName));
        }

        return Load(Path.Combine(AppContext.BaseDirectory, ShippedDirectoryName, taskKey, fileName));
    }

    /// <summary>Loads and validates the manifest at <paramref name="manifestPath"/>.</summary>
    /// <exception cref="FileNotFoundException">The manifest does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed or inconsistent.</exception>
    internal static EvalSet Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var fullPath = Path.GetFullPath(manifestPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No evaluation set manifest at '{fullPath}'.", fullPath);
        }

        var text = File.ReadAllText(fullPath);

        Manifest manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(text, ManifestOptions)
                ?? throw Invalid(fullPath, "the manifest is JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Evaluation set manifest '{fullPath}' is not valid: {exception.Message}",
                exception);
        }

        if (manifest.Format is not ManifestFormat)
        {
            throw Invalid(
                fullPath,
                $"format is {manifest.Format?.ToString(CultureInfo.InvariantCulture) ?? "missing"}; "
                + $"this harness reads format {ManifestFormat.ToString(CultureInfo.InvariantCulture)}.");
        }

        var id = Required(fullPath, manifest.Id, "id");
        var taskKey = Required(fullPath, manifest.Task, "task");

        if (!AiTaskKey.All.Contains(taskKey, StringComparer.Ordinal))
        {
            throw Invalid(fullPath, $"task '{taskKey}' is not a registry task ({string.Join(", ", AiTaskKey.All)}).");
        }

        // The directory is part of how a set is found and reviewed; a photo set filed under the
        // compliance directory is a mistake someone would read past.
        var parentDirectory = Path.GetFileName(Path.GetDirectoryName(fullPath));

        if (!string.Equals(parentDirectory, taskKey, StringComparison.Ordinal))
        {
            throw Invalid(fullPath, $"task is '{taskKey}' but the manifest sits in '{parentDirectory}/'. Sets live in EvalSets/<task>/.");
        }

        if (manifest.Version is not { } version || version < 1)
        {
            throw Invalid(fullPath, "version must be a positive integer.");
        }

        var description = Required(fullPath, manifest.Description, "description");

        var provenance = manifest.Provenance ?? throw Invalid(fullPath, "provenance is missing.");

        if (provenance.CreatedOn is not { } createdOn)
        {
            throw Invalid(fullPath, "provenance.createdOn is missing.");
        }

        var review = manifest.Review ?? throw Invalid(fullPath, "review is missing. An unreviewed set says so: \"reviewed\": false.");

        if (review.Reviewed is not { } reviewed)
        {
            throw Invalid(fullPath, "review.reviewed is missing.");
        }

        if (reviewed)
        {
            // A review nobody can be asked about, or that cannot be placed against the set's
            // history, is not one.
            Required(fullPath, review.Reviewer, "review.reviewer (required once reviewed is true)");

            if (review.ReviewedAt is not { } reviewedAt)
            {
                throw Invalid(fullPath, "review.reviewedAt is required once reviewed is true.");
            }

            if (reviewedAt > DateTimeOffset.UtcNow)
            {
                throw Invalid(fullPath, $"review.reviewedAt {reviewedAt:O} is in the future.");
            }

            // Malformed is a broken manifest; well-formed but different is a stale review, which
            // still loads and is reported through ReviewMismatch.
            if (review.ItemsSha256 is not { } itemsSha256 || !Sha256Hex().IsMatch(itemsSha256))
            {
                throw Invalid(
                    fullPath,
                    "review.itemsSha256 is required once reviewed is true: the 64 lower-case hex "
                    + "characters the harness prints as the set's items sha256.");
            }
        }

        if (manifest.Items is not { Count: > 0 } items)
        {
            throw Invalid(fullPath, "items is missing or empty. A set with nothing in it measures nothing.");
        }

        return new EvalSet(
            id,
            taskKey,
            version,
            description,
            new EvalSetProvenance(
                Required(fullPath, provenance.Source, "provenance.source"),
                Required(fullPath, provenance.Rights, "provenance.rights"),
                Required(fullPath, provenance.LabelledBy, "provenance.labelledBy"),
                Required(fullPath, provenance.LabellingMethod, "provenance.labellingMethod"),
                createdOn),
            new EvalSetReview(reviewed, review.Reviewer, review.ReviewedAt, review.ItemsSha256, review.Notes),
            [.. items],
            fullPath,
            ComputeHash(text),
            ComputeItemsHash(items));
    }

    /// <summary>
    /// Hashes items in canonical form: each item written as compact JSON by
    /// <see cref="Utf8JsonWriter"/> with its default settings, joined as one JSON array. That makes
    /// the hash independent of indentation, line endings and string-escape spelling in the file,
    /// so reformatting a manifest does not void its review; but any change to a value, a key or
    /// the order of items or properties does, which is the conservative side to err on.
    /// </summary>
    internal static string ComputeItemsHash(IEnumerable<JsonElement> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();

            foreach (var item in items)
            {
                item.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>
    /// The items as <typeparamref name="T"/>. Strict, like the manifest: an item property
    /// <typeparamref name="T"/> does not declare is an error rather than a silently ignored label.
    /// </summary>
    /// <exception cref="InvalidDataException">An item does not fit <typeparamref name="T"/>.</exception>
    internal IReadOnlyList<T> ItemsAs<T>()
    {
        var typed = new List<T>(Items.Count);

        for (var index = 0; index < Items.Count; index++)
        {
            try
            {
                typed.Add(Items[index].Deserialize<T>(ItemOptions)
                    ?? throw Invalid(ManifestPath, $"items[{index}] is JSON null."));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Evaluation set {Name}: items[{index}] does not match {typeof(T).Name}: {exception.Message}",
                    exception);
            }
        }

        return typed;
    }

    /// <summary>
    /// Resolves a file an item references (a photo, a text sample), relative to the manifest's
    /// directory. Paths that would leave that directory are refused: everything a set measures
    /// on must be committed beside it, where its review covered it.
    /// </summary>
    /// <exception cref="InvalidDataException">The path leaves the set's directory.</exception>
    internal string ResolveFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(directory, relativePath));

        if (Path.IsPathRooted(relativePath) || !resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(ManifestPath, $"'{relativePath}' is outside the set's directory. Reference files beside the manifest.");
        }

        return resolved;
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    private static string Required(string path, string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw Invalid(path, $"{name} is missing or blank.") : value;

    private static InvalidDataException Invalid(string path, string problem) =>
        new($"Evaluation set manifest '{path}' is not valid: {problem}");

    // ───────────────────────── Wire shape ─────────────────────────
    //
    // Nullable throughout so a missing property is reported by name above, instead of surfacing
    // as a default value (a missing "reviewed" must not read as false-by-accident, nor a missing
    // version as 0).

    private sealed class Manifest
    {
        public int? Format { get; init; }

        public string? Id { get; init; }

        public string? Task { get; init; }

        public int? Version { get; init; }

        public string? Description { get; init; }

        public ProvenanceWire? Provenance { get; init; }

        public ReviewWire? Review { get; init; }

        public List<JsonElement>? Items { get; init; }
    }

    private sealed class ProvenanceWire
    {
        public string? Source { get; init; }

        public string? Rights { get; init; }

        public string? LabelledBy { get; init; }

        public string? LabellingMethod { get; init; }

        public DateOnly? CreatedOn { get; init; }
    }

    private sealed class ReviewWire
    {
        public bool? Reviewed { get; init; }

        public string? Reviewer { get; init; }

        public DateTimeOffset? ReviewedAt { get; init; }

        public string? ItemsSha256 { get; init; }

        public string? Notes { get; init; }
    }
}
