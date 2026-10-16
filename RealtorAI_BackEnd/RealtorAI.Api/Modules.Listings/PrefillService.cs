using System.Globalization;
using System.Text.Json;
using Libs.Reso;
using Microsoft.Extensions.Logging;
using Modules.Listings.PropertyData;

namespace Modules.Listings;

/// <summary>How a pre-fill attempt ended, so the agent can be told which of FR-008's cases applied.</summary>
public enum PrefillOutcome
{
    /// <summary>No public-record source is configured.</summary>
    NotConfigured,

    /// <summary>The source has no record for the address.</summary>
    NoMatch,

    /// <summary>The source answered with a record.</summary>
    Found,

    /// <summary>
    /// The source could not be reached or answered with something unusable. The draft is still
    /// created — a vendor outage must not stop an agent starting a listing — and the fields are
    /// left for the agent exactly as if no source were configured.
    /// </summary>
    Unavailable,
}

/// <summary>One public-record value to write, with the provenance note FR-008 requires.</summary>
public sealed record PrefilledField(string ResoFieldKey, JsonElement Value, string SourceNote);

/// <summary>What pre-fill produced for a new listing.</summary>
public sealed record PrefillPlan(PrefillOutcome Outcome, IReadOnlyList<PrefilledField> Fields);

/// <summary>
/// T078 — public-record pre-fill for a new draft (FR-007, FR-008; Story 1 scenarios 4 and 5).
///
/// <para>
/// Every value this produces is written with <c>public_record</c> provenance and a source note
/// naming the source and the retrieval date. Nothing here — and nothing anywhere — fills these
/// fields by inference: when the provider has nothing, the plan is empty, the fields stay empty,
/// and <see cref="ListingFieldService.RequiredKeys"/> reports them as required before approval.
/// </para>
/// <para>
/// In Slice A the provider is <see cref="NullPropertyDataProvider"/> (research R5), so every plan
/// is <see cref="PrefillOutcome.NotConfigured"/>. The <see cref="PrefillOutcome.Found"/> branch
/// is the complete behaviour for when a source is chosen; it is unit-tested against a provider
/// that returns a record.
/// </para>
/// </summary>
public sealed partial class PrefillService
{
    private readonly IPropertyDataProvider provider;
    private readonly ILogger<PrefillService> logger;

    public PrefillService(IPropertyDataProvider provider, ILogger<PrefillService> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        this.provider = provider;
        this.logger = logger;
    }

    public async Task<PrefillPlan> PlanAsync(PropertyAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        PropertyDataLookup lookup;

        try
        {
            lookup = await provider.LookupAsync(address, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProviderFailed(logger, exception);
            return new PrefillPlan(PrefillOutcome.Unavailable, []);
        }

        return lookup switch
        {
            PropertyDataLookup.NotConfigured => new PrefillPlan(PrefillOutcome.NotConfigured, []),
            PropertyDataLookup.NoMatch => new PrefillPlan(PrefillOutcome.NoMatch, []),
            PropertyDataLookup.Found found => FromRecord(found),
            _ => throw new InvalidOperationException($"Unknown lookup result {lookup.GetType().Name}."),
        };
    }

    private PrefillPlan FromRecord(PropertyDataLookup.Found found)
    {
        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"{found.SourceName}, retrieved {found.RetrievedAt.UtcDateTime:yyyy-MM-dd}");

        var fields = new List<PrefilledField>();

        foreach (var field in found.Fields)
        {
            // A provider is outside code. A key that cannot be a RESO field, or a value that
            // fails the same rules an agent's entry would, is dropped — and left for the agent —
            // rather than stored as a public record nobody can rely on.
            var (validated, errors) = ListingFieldService.Validate(
                new FieldUpsertInput(field.ResoFieldKey, field.Value, "public_record", note),
                new Dictionary<string, IReadOnlySet<string>>(),
                found.RetrievedAt.Year);

            if (validated is null || !ResoFieldKey.IsWellFormed(field.ResoFieldKey))
            {
                LogFieldDropped(logger, field.ResoFieldKey, found.SourceName, string.Join(" ", errors.Values.SelectMany(message => message)));
                continue;
            }

            fields.Add(new PrefilledField(validated.ResoFieldKey, validated.Value, note));
        }

        return new PrefillPlan(PrefillOutcome.Found, fields);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Public-record lookup failed; the draft is created without pre-fill.")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped public-record field {Key} from {Source}: {Reason}")]
    private static partial void LogFieldDropped(ILogger logger, string key, string source, string reason);
}
