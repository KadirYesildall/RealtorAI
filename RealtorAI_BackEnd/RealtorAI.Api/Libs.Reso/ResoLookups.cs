using System.Text.Json;
using System.Text.Json.Nodes;

namespace Libs.Reso;

/// <summary>
/// The standard RESO lookup values of the six enumerated fields Slice A drafts (research R3).
///
/// <para>
/// <b>Unverified seed.</b> The values are a versioned data file, <c>Lookups/dd-2.0-slice-a.json</c>,
/// transcribed without the published Data Dictionary to hand (research R35). <see cref="Verified"/>
/// says so until someone checks every value against the official dictionary. Until then they
/// keep AI output to well-known lookup names. They are not yet a guarantee of what an MLS
/// accepts, and no MLS submission (Slice B) may rely on them while the flag is false.
/// </para>
///
/// <para>
/// <b>Scope.</b> Enforced on what a model proposes, never on what an agent enters: the agent is
/// the authority on their listing, and the MLS rule check (Story 4) is where an agent's value
/// meets the MLS's list.
/// </para>
/// </summary>
public static class ResoLookups
{
    private static readonly Seed Data = Seed.Load();

    public static string DataDictionaryVersion => Data.Version;

    public static bool Verified => Data.Verified;

    public static IReadOnlyList<string> EnumeratedFields => [.. Data.Fields.Keys];

    public static bool IsEnumerated(string field) => Data.Fields.ContainsKey(field);

    /// <summary>The standard values of <paramref name="field"/>, or none if it is not enumerated.</summary>
    public static IReadOnlyList<string> Standard(string field) =>
        Data.Fields.TryGetValue(field, out var values) ? values : [];

    /// <summary>
    /// The values of <paramref name="proposed"/> the MLS permits for <paramref name="field"/>, in
    /// the order proposed, each once. Spellings must match exactly: a lookup name is an
    /// identifier, and a near miss is a different value.
    /// </summary>
    public static IReadOnlyList<string> Permitted(string field, IEnumerable<string> proposed, ResoLookupMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(mapping);

        var allowed = mapping.Allowed(field).ToHashSet(StringComparer.Ordinal);

        return proposed.Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed record Seed(string Version, bool Verified, IReadOnlyDictionary<string, IReadOnlyList<string>> Fields)
    {
        internal static Seed Load()
        {
            using var stream = typeof(ResoLookups).Assembly.GetManifestResourceStream("Libs.Reso.Lookups.dd-2.0-slice-a.json")
                ?? throw new InvalidOperationException("The RESO lookup seed is not embedded in Libs.Reso.");

            var document = JsonNode.Parse(stream)!.AsObject();
            var fields = document["fields"]!.AsObject().ToDictionary(
                field => field.Key,
                field => (IReadOnlyList<string>)field.Value!.AsArray().Select(value => value!.GetValue<string>()).ToList(),
                StringComparer.Ordinal);

            return new Seed(document["dataDictionaryVersion"]!.GetValue<string>(), document["verified"]!.GetValue<bool>(), fields);
        }
    }
}

/// <summary>
/// An MLS's changes to the standard lookups: the profile's <c>enumerated_field_lookups</c>
/// (0010). Per field, <c>only</c> narrows the standard values to those listed, and <c>extra</c>
/// adds the MLS's local values. Both are optional.
///
/// <para>
/// A restriction can only narrow. Listing a value in <c>only</c> that the standard set lacks does
/// not admit it, because an MLS-local value belongs in <c>extra</c>, where it is plainly a
/// local value. A mapping that does not parse is refused with <see cref="FormatException"/>,
/// so a broken profile fails closed instead of reading as "no restriction".
/// </para>
/// </summary>
public sealed class ResoLookupMapping
{
    private readonly IReadOnlyDictionary<string, (IReadOnlyList<string>? Only, IReadOnlyList<string> Extra)> fields;

    private ResoLookupMapping(IReadOnlyDictionary<string, (IReadOnlyList<string>? Only, IReadOnlyList<string> Extra)> fields)
    {
        this.fields = fields;
    }

    public static ResoLookupMapping None { get; } = new(new Dictionary<string, (IReadOnlyList<string>?, IReadOnlyList<string>)>());

    /// <summary>The values the MLS accepts for <paramref name="field"/>: standard ones first, then local ones.</summary>
    public IReadOnlyList<string> Allowed(string field)
    {
        var standard = ResoLookups.Standard(field);

        if (standard.Count == 0 || !fields.TryGetValue(field, out var change))
        {
            return standard;
        }

        var narrowed = change.Only is null ? standard : standard.Where(change.Only.Contains).ToList();

        return narrowed.Concat(change.Extra.Where(value => !narrowed.Contains(value))).ToList();
    }

    public static ResoLookupMapping Parse(string json)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The MLS lookup mapping is not JSON.", exception);
        }

        if (root is not JsonObject document)
        {
            throw new FormatException("The MLS lookup mapping must be an object keyed by RESO field.");
        }

        var fields = new Dictionary<string, (IReadOnlyList<string>? Only, IReadOnlyList<string> Extra)>(StringComparer.Ordinal);

        foreach (var (field, node) in document)
        {
            if (node is not JsonObject change || change.Any(property => property.Key is not ("only" or "extra")))
            {
                throw new FormatException($"The lookup mapping for {field} must be an object with only \"only\" and \"extra\".");
            }

            fields[field] = (Values(field, change["only"]), Values(field, change["extra"]) ?? []);
        }

        return new ResoLookupMapping(fields);
    }

    private static List<string>? Values(string field, JsonNode? node) => node switch
    {
        null => null,
        JsonArray values when values.All(value => value is JsonValue item && item.TryGetValue<string>(out _)) =>
            values.Select(value => value!.GetValue<string>()).ToList(),
        _ => throw new FormatException($"The lookup mapping for {field} must list values as strings."),
    };
}
