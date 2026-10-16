using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Libs.Reso;
using Modules.Listings.PropertyData;

namespace Modules.Listings;

/// <summary>
/// Where a field's value came from (FR-005). Domain vocabulary; its database labels
/// (<c>public.provenance</c>, 0005) are listed in <c>Api.Data.PostgresEnumMappings</c>.
/// </summary>
public enum Provenance
{
    AgentEntered,
    PublicRecord,
    AiDetectedFromPhoto,
    AiSuggested,
}

/// <summary>A field write as a client sent it, before validation.</summary>
public sealed record FieldUpsertInput(
    string? ResoFieldKey,
    JsonElement? Value,
    string? Provenance,
    string? SourceNote);

/// <summary>A field write that passed validation.</summary>
public sealed record ValidatedField(
    string ResoFieldKey,
    JsonElement Value,
    Provenance Provenance,
    string? SourceNote);

/// <summary>
/// T074 — the provenance rules for listing fields (FR-005, FR-006, FR-007, FR-008).
///
/// <para>
/// <b>FR-007 is enforced twice, on purpose.</b> The check constraint
/// <c>listing_fields_no_ai_measurements</c> (0005) refuses AI provenance on measurement and legal
/// keys for every writer, including this service. It is repeated here so a client is told which
/// field broke which rule with a 422, before a round trip, rather than receiving a constraint
/// error — and <see cref="MeasurementAndLegalKeys"/> is that constraint's list verbatim, so the
/// two cannot disagree about which keys are protected.
/// </para>
/// </summary>
public static class ListingFieldService
{
    /// <summary>
    /// FR-007: values only an agent or a public record may supply, never a model. Exactly the
    /// keys listed in <c>listing_fields_no_ai_measurements</c>.
    /// </summary>
    public static IReadOnlySet<string> MeasurementAndLegalKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "LivingArea",
        "LivingAreaSquareFeet",
        "LotSizeSquareFeet",
        "LotSizeAcres",
        "YearBuilt",
        "TaxAnnualAmount",
        "TaxYear",
        "AssociationFee",
        "AssociationFeeFrequency",
        "HighSchoolDistrict",
        "ElementarySchoolDistrict",
        "MiddleOrJuniorSchoolDistrict",
        "Zoning",
    };

    /// <summary>Measurements whose value must be a non-negative number.</summary>
    private static readonly HashSet<string> NonNegativeNumberKeys = new(StringComparer.Ordinal)
    {
        "LivingArea",
        "LivingAreaSquareFeet",
        "LotSizeSquareFeet",
        "LotSizeAcres",
        "TaxAnnualAmount",
        "AssociationFee",
    };

    /// <summary>Calendar years. Bounded, so a typo such as 199 or 19988 is caught rather than stored.</summary>
    private static readonly HashSet<string> YearKeys = new(StringComparer.Ordinal) { "YearBuilt", "TaxYear" };

    /// <summary>Earliest year accepted for <c>YearBuilt</c> and <c>TaxYear</c>.</summary>
    public const int EarliestYear = 1600;

    public const int MaxSourceNoteLength = 500;

    /// <summary>Provenances that need an agent's explicit confirmation before publication (FR-006).</summary>
    public static bool RequiresConfirmation(Provenance provenance) =>
        provenance is Provenance.AiDetectedFromPhoto or Provenance.AiSuggested;

    /// <summary>
    /// The keys a listing must have before approval: FR-008's public-record set, plus whatever
    /// the listing's MLS rule profile adds.
    /// </summary>
    public static IReadOnlySet<string> RequiredKeys(IEnumerable<string> ruleProfileRequiredFields)
    {
        ArgumentNullException.ThrowIfNull(ruleProfileRequiredFields);

        var keys = new HashSet<string>(PublicRecordFields.Keys, StringComparer.Ordinal);
        keys.UnionWith(ruleProfileRequiredFields);

        return keys;
    }

    /// <summary>
    /// Validates a field write. <paramref name="enumeratedLookups"/> is the listing's MLS rule
    /// profile's allowed values per enumerated key; a key absent from it is not enumerated for
    /// that MLS, and an unverified profile has none at all.
    /// </summary>
    /// <param name="currentYear">Injected so the year bound is testable without a clock.</param>
    public static (ValidatedField? Field, Dictionary<string, string[]> Errors) Validate(
        FieldUpsertInput input,
        IReadOnlyDictionary<string, IReadOnlySet<string>> enumeratedLookups,
        int currentYear)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(enumeratedLookups);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var key = input.ResoFieldKey?.Trim();
        if (!ResoFieldKey.IsWellFormed(key))
        {
            errors["resoFieldKey"] = ["Use a RESO Data Dictionary field name, such as LivingArea or InteriorFeatures."];
        }

        var provenance = ParseProvenance(input.Provenance);
        if (provenance is null)
        {
            errors["provenance"] = ["Use agent_entered, public_record, ai_detected_from_photo or ai_suggested."];
        }
        else if (key is not null && MeasurementAndLegalKeys.Contains(key) && RequiresConfirmation(provenance.Value))
        {
            errors["provenance"] = [
                $"{key} is a measurement or legal fact. It can only be entered by you or taken from a "
                + "public record — never inferred by AI (FR-007).",
            ];
        }

        var sourceNote = string.IsNullOrWhiteSpace(input.SourceNote) ? null : input.SourceNote.Trim();
        if (provenance == Provenance.PublicRecord && sourceNote is null)
        {
            errors["sourceNote"] = ["A public-record value needs its source and retrieval date (FR-008)."];
        }
        else if (sourceNote is { Length: > MaxSourceNoteLength })
        {
            errors["sourceNote"] = [$"The source note can be at most {MaxSourceNoteLength} characters."];
        }

        if (input.Value is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            errors["value"] = ["Enter a value. To clear a field, enter it again when you know it."];
        }
        else if (key is not null && ValueError(key, value, enumeratedLookups, currentYear) is { } valueError)
        {
            errors["value"] = [valueError];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ValidatedField(key!, input.Value!.Value.Clone(), provenance!.Value, sourceNote), errors);
    }

    /// <summary>
    /// Whether a write replaces what the field holds. A confirmation vouches for one value from
    /// one source, so a change to either clears it (FR-006); rewriting the same value does not.
    /// </summary>
    public static bool ChangesContent(string storedValueJson, Provenance storedProvenance, ValidatedField incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        return storedProvenance != incoming.Provenance
            || !JsonNode.DeepEquals(JsonNode.Parse(storedValueJson), JsonNode.Parse(incoming.Value.GetRawText()));
    }

    public static Provenance? ParseProvenance(string? value) => value switch
    {
        "agent_entered" => Provenance.AgentEntered,
        "public_record" => Provenance.PublicRecord,
        "ai_detected_from_photo" => Provenance.AiDetectedFromPhoto,
        "ai_suggested" => Provenance.AiSuggested,
        _ => null,
    };

    public static string Format(Provenance provenance) => provenance switch
    {
        Provenance.AgentEntered => "agent_entered",
        Provenance.PublicRecord => "public_record",
        Provenance.AiDetectedFromPhoto => "ai_detected_from_photo",
        Provenance.AiSuggested => "ai_suggested",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, null),
    };

    private static string? ValueError(
        string key,
        JsonElement value,
        IReadOnlyDictionary<string, IReadOnlySet<string>> enumeratedLookups,
        int currentYear)
    {
        if (value.ValueKind is JsonValueKind.Object)
        {
            return "A field holds a single value or a list of values, not an object.";
        }

        if (value.ValueKind is JsonValueKind.Array
            && value.EnumerateArray().Any(item => item.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
        {
            return "A list may contain only plain values.";
        }

        if (NonNegativeNumberKeys.Contains(key)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0))
        {
            return $"{key} must be a number of zero or more.";
        }

        if (YearKeys.Contains(key))
        {
            var latest = currentYear + 3;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var year) || year < EarliestYear || year > latest)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{key} must be a four-digit year from {EarliestYear} to {latest}.");
            }
        }

        if (enumeratedLookups.TryGetValue(key, out var allowed))
        {
            var values = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToList()
                : [value];

            var unknown = values
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                .Where(item => item is null || !allowed.Contains(item))
                .ToList();

            if (unknown.Count > 0)
            {
                return $"{string.Join(", ", unknown)} is not a value this MLS accepts for {key}.";
            }
        }

        return null;
    }
}
