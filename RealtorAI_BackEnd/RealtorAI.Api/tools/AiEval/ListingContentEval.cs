using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Libs.Ai;
using Libs.Ai.Tasks;
using Libs.Reso;
using Modules.Content;
using Npgsql;

namespace AiEval;

/// <summary>One fact of an evaluation listing, as the agent or a public record supplied it.</summary>
/// <param name="Provenance">The contract spelling: <c>agent_entered</c> or <c>public_record</c>.</param>
internal sealed record ContentListingFact(string Key, JsonNode? Value, string Provenance, bool Confirmed);

/// <summary>
/// One fictional listing of the content evaluation set, as committed in
/// <c>EvalSets/listing.content/listings-v1.json</c>: what <c>ContentGenerationStep</c> reads for a
/// listing before it calls the model.
/// </summary>
/// <param name="TargetMlsId">The rule profile whose verified limits the copy is written to.</param>
internal sealed record ContentListingItem(
    string Id,
    string TargetMlsId,
    string City,
    string State,
    string PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes,
    IReadOnlyList<ContentListingFact> Facts,
    IReadOnlyList<PhotoObservation> Photos);

/// <summary>An MLS rule profile as content generation reads it: its limits and its lookup changes.</summary>
internal sealed record EvalContentProfile(ContentProfile Profile, ResoLookupMapping Lookups);

/// <summary>
/// Measures <c>listing.content</c>'s two catalog metrics: the JSON-schema validity rate (FR-015)
/// and tokens per listing (SC-006).
///
/// <para>
/// <b>A listing is evaluated as a full pipeline run generates its content</b>
/// (<c>ContentGenerationStep</c>): one structured call proposing the enumerated RESO fields from
/// what the photos showed, choosing among the lookups the MLS accepts; the permitted values added
/// to the listing's facts as unconfirmed AI-detected facts, as production stores them before
/// writing; then one text call per content kind with a verified limit, to that limit, in the
/// brokerage default tone, with the production variant count. Requests are built by
/// <see cref="ListingContentTask"/> itself, so the model under evaluation is sent what production
/// sends. Tokens per listing is the sum over those calls, retries included — what one listing
/// costs.
/// </para>
/// <para>
/// <b>The limits come from the verified rule profile, and nothing else.</b> Production generates
/// no content for a listing whose MLS has no verified limits (research R2), so there is no call to
/// reproduce, and a limit chosen here would measure calls production never makes: the length of
/// the request and of the answer both depend on it. Until the profile is verified (T160), the
/// suite calls no model and reports both metrics unmeasured with that reason, which
/// <see cref="EvalVerdict"/> never lets pass.
/// </para>
/// <para>
/// <b>Nothing here judges the copy.</b> Neither metric says whether the words are grounded or
/// compliant; grounding and the compliance check run on production drafts, and the Fair Housing
/// judge has its own suite. The set therefore carries inputs, not labels, and its review asks only
/// whether those inputs are representative.
/// </para>
/// <para>
/// <b>One call at a time</b>, as the step generates kind by kind. No latency metric is defined for
/// this task.
/// </para>
/// </summary>
internal sealed class ListingContentEval : IEvalSuite
{
    /// <summary>The shipped evaluation set this suite measures on.</summary>
    internal const string SetFileName = "listings-v1.json";

    /// <summary>
    /// The tone each listing is written in: the brokerage default production falls back to when a
    /// brokerage has set none (<c>Tone.Professional</c>). A unit test pins it to the host's label.
    /// </summary>
    internal const string Tone = "professional";

    /// <summary>The provenance production gives the structured proposals it stores.</summary>
    internal const string AiDetectedFromPhoto = "ai_detected_from_photo";

    /// <summary>The provenances a set fact may carry: the sourced ones an agent's listing starts with.</summary>
    internal static readonly IReadOnlySet<string> SourcedProvenances = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent_entered",
        "public_record",
    };

    private static readonly string[] Metrics = [MetricCatalog.SchemaValidityRate, MetricCatalog.TokensPerListing];

    private readonly Func<EvalSet> loadSet;
    private readonly Func<string, CancellationToken, Task<EvalContentProfile?>> loadProfile;
    private readonly TextWriter report;

    /// <summary>The shipped suite: the committed set, profiles read from the harness's database.</summary>
    /// <param name="connectionString">The harness's connection, or null when only listing criteria.</param>
    internal ListingContentEval(string? connectionString)
        : this(
            () => EvalSet.LoadShipped(AiTaskKey.ListingContent, SetFileName),
            (mlsId, cancellationToken) => LoadProfileAsync(
                connectionString ?? throw new InvalidOperationException(
                    "The content suite reads the MLS rule profile, which needs --connection or REALTORAI_EVAL_POSTGRES."),
                mlsId,
                cancellationToken),
            Console.Out)
    {
    }

    /// <param name="loadSet">Loads the set to measure on. Tests pass a manifest written for the test.</param>
    /// <param name="loadProfile">Reads a rule profile by id, or null when there is none.</param>
    /// <param name="report">Where the per-listing breakdown is written.</param>
    internal ListingContentEval(
        Func<EvalSet> loadSet,
        Func<string, CancellationToken, Task<EvalContentProfile?>> loadProfile,
        TextWriter report)
    {
        ArgumentNullException.ThrowIfNull(loadSet);
        ArgumentNullException.ThrowIfNull(loadProfile);
        ArgumentNullException.ThrowIfNull(report);

        this.loadSet = loadSet;
        this.loadProfile = loadProfile;
        this.report = report;
    }

    public string TaskKey => AiTaskKey.ListingContent;

    public async Task<SuiteRun> RunAsync(EvalModelInvoker model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Target.TaskKey != TaskKey)
        {
            throw new InvalidOperationException(
                $"The content suite evaluates '{TaskKey}' models; registry entry {model.Target.Id} serves '{model.Target.TaskKey}'.");
        }

        var set = loadSet();
        var items = ReadItems(set);
        var profiles = new Dictionary<string, EvalContentProfile>(StringComparer.Ordinal);
        var blocked = new List<string>();

        foreach (var mlsId in items.Select(item => item.TargetMlsId).Distinct(StringComparer.Ordinal))
        {
            var profile = await loadProfile(mlsId, cancellationToken);

            if (WhyNotWritable(mlsId, profile) is { } reason)
            {
                blocked.Add(reason);
            }
            else
            {
                profiles[mlsId] = profile!;
            }
        }

        if (blocked.Count > 0)
        {
            return new SuiteRun(
                [set],
                new Dictionary<string, double>(),
                [$"{string.Join(", ", Metrics)}: {string.Join(" ", blocked)} No model was called."]);
        }

        var calls = new List<EvalCallRecord>();
        var listings = new List<EvalListingRun>(items.Count);

        foreach (var item in items)
        {
            var listing = await GenerateAsync(model, item, profiles[item.TargetMlsId], cancellationToken);

            calls.AddRange(listing.Calls);
            listings.Add(listing);
            WriteListing(item, listing);
        }

        var measurement = CallMetrics.Measure(calls, listings, Metrics);

        return new SuiteRun([set], measurement.Values, measurement.Unmeasured);
    }

    /// <summary>
    /// Why content cannot be generated for listings on <paramref name="mlsId"/>, or null when at
    /// least one kind has a verified limit — the same test <c>ContentGenerationStep</c> skips on.
    /// </summary>
    internal static string? WhyNotWritable(string mlsId, EvalContentProfile? profile)
    {
        if (profile is null)
        {
            return $"No MLS rule profile has id '{mlsId}'.";
        }

        if (!ContentKinds.All.Any(kind => ContentRules.LimitFor(kind, profile.Profile) is not null))
        {
            return $"MLS rule profile '{mlsId}' has no verified character limits, so production generates "
                + "no content for its listings and there is no call to reproduce. Verify the profile (T160) "
                + "and run the evaluation again.";
        }

        return null;
    }

    /// <summary>
    /// Runs one listing's content generation against the model under evaluation, in the step's
    /// order, and returns every call it made.
    /// </summary>
    internal static async Task<EvalListingRun> GenerateAsync(
        EvalModelInvoker model,
        ContentListingItem item,
        EvalContentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(profile);

        var clock = Stopwatch.StartNew();
        var calls = new List<EvalCallRecord>();
        var facts = Facts(item);

        var allowed = ResoLookups.EnumeratedFields.ToDictionary(field => field, profile.Lookups.Allowed, StringComparer.Ordinal);
        var structured = await model.InvokeAsync(ListingContentTask.BuildStructuredInput(Input(item, facts), allowed), [], cancellationToken);
        calls.Add(structured.Record);

        // A proposal that failed validation twice leaves the facts as they were, as in production.
        if (structured.Document is { } document)
        {
            facts = WithProposals(item, facts, ListingContentTask.ReadStructure(document), profile.Lookups);
        }

        foreach (var kind in ContentKinds.All)
        {
            if (ContentRules.LimitFor(kind, profile.Profile) is not { } limit)
            {
                continue;
            }

            var text = await model.InvokeAsync(
                ListingContentTask.BuildTextInput(Input(item, facts), kind, Tone, ContentRules.VariantsFor(kind, null), limit),
                [],
                cancellationToken);

            calls.Add(text.Record);
        }

        return new EvalListingRun(calls, clock.Elapsed);
    }

    /// <summary>
    /// The facts after the structured proposal is stored: each permitted value becomes an
    /// unconfirmed AI-detected fact, unless the listing already holds that field. Every set fact is
    /// sourced and confirmed (<see cref="ReadItems"/>), and production never overwrites either.
    /// </summary>
    internal static IReadOnlyList<ContentFact> WithProposals(
        ContentListingItem item,
        IReadOnlyList<ContentFact> facts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> proposed,
        ResoLookupMapping lookups)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(lookups);

        var merged = facts.ToList();

        foreach (var (key, values) in proposed)
        {
            if (!ResoLookups.IsEnumerated(key) || merged.Any(fact => fact.Key == key))
            {
                continue;
            }

            var permitted = ResoLookups.Permitted(key, values, lookups);

            if (permitted.Count == 0)
            {
                continue;
            }

            merged.Add(new ContentFact(
                FieldId(item.Id, key),
                key,
                new JsonArray([.. permitted.Select(value => (JsonNode)JsonValue.Create(value))]),
                AiDetectedFromPhoto,
                Confirmed: false));
        }

        return merged;
    }

    /// <summary>Reads a rule profile from <c>mls_rule_profiles</c>, mapped as the host maps it.</summary>
    internal static async Task<EvalContentProfile?> LoadProfileAsync(string connectionString, string mlsId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select verified_at is not null, public_remarks_max_chars, private_remarks_max_chars,
                   enumerated_field_lookups::text
            from public.mls_rule_profiles
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", mlsId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        int? Limit(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

        return new EvalContentProfile(
            new ContentProfile(reader.GetBoolean(0), Limit(1), Limit(2)),
            ResoLookupMapping.Parse(reader.GetString(3)));
    }

    /// <summary>
    /// The set's listings, checked before any model is called: a malformed listing would change
    /// what is sent, and so what is measured.
    /// </summary>
    /// <exception cref="InvalidDataException">A listing is missing a field, carries an unsourced fact, or repeats an id.</exception>
    internal static IReadOnlyList<ContentListingItem> ReadItems(EvalSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var items = set.ItemsAs<ContentListingItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var where = $"Evaluation set {set.Name}: items[{index.ToString(CultureInfo.InvariantCulture)}]";

            Require(where, "id", item.Id);
            Require(where, "targetMlsId", item.TargetMlsId);
            Require(where, "city", item.City);
            Require(where, "state", item.State);
            Require(where, "propertyType", item.PropertyType);

            if (!ids.Add(item.Id))
            {
                throw new InvalidDataException($"{where}: id '{item.Id}' is used more than once.");
            }

            if (item.Facts is null || item.Photos is null)
            {
                throw new InvalidDataException($"{where} ({item.Id}): facts and photos must be present, even if empty.");
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fact in item.Facts)
            {
                Require($"{where} ({item.Id})", "facts[].key", fact.Key);

                if (!keys.Add(fact.Key))
                {
                    throw new InvalidDataException($"{where} ({item.Id}): fact '{fact.Key}' appears more than once.");
                }

                if (!SourcedProvenances.Contains(fact.Provenance) || !fact.Confirmed)
                {
                    throw new InvalidDataException(
                        $"{where} ({item.Id}): fact '{fact.Key}' must be confirmed and one of "
                        + $"{string.Join(", ", SourcedProvenances)}; AI-detected facts come from the structured call.");
                }
            }

            foreach (var photo in item.Photos)
            {
                Require($"{where} ({item.Id})", "photos[].roomType", photo.RoomType);
            }
        }

        return items;
    }

    private static IReadOnlyList<ContentFact> Facts(ContentListingItem item) =>
        [.. item.Facts.Select(fact => new ContentFact(FieldId(item.Id, fact.Key), fact.Key, fact.Value?.DeepClone(), fact.Provenance, fact.Confirmed))];

    private static ListingContentInput Input(ContentListingItem item, IReadOnlyList<ContentFact> facts) => new(
        StableId(item.Id, "listing"),
        StableId(item.Id, "actor"),
        StableId(item.Id, "run"),
        new ContentBasics(item.City, item.State, item.PropertyType, item.ListPrice, item.Bedrooms, item.Bathrooms, item.AgentNotes),
        facts,
        item.Photos);

    /// <summary>A field id that is the same on every run, so two runs send identical requests.</summary>
    private static Guid FieldId(string itemId, string key) => StableId(itemId, "field:" + key);

    private static Guid StableId(string itemId, string purpose) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(itemId + "|" + purpose)).AsSpan(0, 16));

    private void WriteListing(ContentListingItem item, EvalListingRun listing)
    {
        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

        report.WriteLine(
            $"  {item.Id}: {Count(listing.Calls.Count)} calls, {Count(listing.Calls.Count(call => call.FirstAttemptValid))} valid "
            + $"on the first attempt, {Count(listing.Calls.Sum(call => call.Tokens))} tokens.");
    }

    private static void Require(string where, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{where}: {name} is missing or blank.");
        }
    }
}
