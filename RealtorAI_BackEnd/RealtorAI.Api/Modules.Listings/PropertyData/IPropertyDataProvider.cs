using System.Text.Json;

namespace Modules.Listings.PropertyData;

/// <summary>The address a public-record lookup is made for.</summary>
public sealed record PropertyAddress(
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode);

/// <summary>One field value as a public record states it.</summary>
/// <param name="ResoFieldKey">The RESO key it fills, e.g. <c>LivingArea</c>.</param>
/// <param name="Value">The value as JSON, stored as-is in <c>listing_fields.value</c>.</param>
public sealed record PublicRecordField(string ResoFieldKey, JsonElement Value);

/// <summary>The outcome of a public-record lookup (FR-008).</summary>
public abstract record PropertyDataLookup
{
    private PropertyDataLookup()
    {
    }

    /// <summary>
    /// No public-record source is configured. Every <see cref="PublicRecordFields.Keys"/> field
    /// stays empty and is marked required.
    /// </summary>
    public sealed record NotConfigured : PropertyDataLookup;

    /// <summary>
    /// A source is configured but holds no record for the address. Treated exactly like
    /// <see cref="NotConfigured"/> for the listing, and kept distinct so the agent can be told
    /// which one happened.
    /// </summary>
    public sealed record NoMatch(string SourceName) : PropertyDataLookup;

    /// <summary>
    /// A record was found. FR-008 requires recording where it came from and when, so a found
    /// result cannot be constructed without both.
    /// </summary>
    public sealed record Found : PropertyDataLookup
    {
        public Found(string sourceName, DateTimeOffset retrievedAt, IReadOnlyList<PublicRecordField> fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
            ArgumentNullException.ThrowIfNull(fields);

            var duplicate = fields
                .GroupBy(field => field.ResoFieldKey, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicate is not null)
            {
                throw new ArgumentException(
                    $"A public record returned '{duplicate.Key}' more than once; which value is "
                    + "the record's is ambiguous.",
                    nameof(fields));
            }

            SourceName = sourceName;
            RetrievedAt = retrievedAt;
            Fields = fields;
        }

        public string SourceName { get; }

        public DateTimeOffset RetrievedAt { get; }

        public IReadOnlyList<PublicRecordField> Fields { get; }
    }
}

/// <summary>
/// T060 — public-record pre-fill (FR-008, research R5).
///
/// <para>
/// Slice A ships no vendor: choosing one is a commercial decision — coverage, licensing,
/// per-call price — with no architectural consequence behind this interface. FR-008 specifies
/// the behaviour for both cases, configured and not, so both must exist as code:
/// <see cref="NullPropertyDataProvider"/> is the complete implementation of the unconfigured
/// case, not a placeholder for the configured one. This is the one place plan.md's Complexity
/// Tracking accepts a single-implementation interface, for exactly that reason.
/// </para>
/// <para>
/// Whatever a provider returns is recorded with <c>public_record</c> provenance. None of these
/// fields is ever filled by a model — FR-007, enforced by a check constraint on
/// <c>listing_fields</c> as well as here.
/// </para>
/// </summary>
public interface IPropertyDataProvider
{
    Task<PropertyDataLookup> LookupAsync(PropertyAddress address, CancellationToken cancellationToken);
}

/// <summary>
/// The provider when no public-record source is configured: every lookup reports
/// <see cref="PropertyDataLookup.NotConfigured"/>, so the pre-fill fields stay empty and are
/// marked required for the agent to enter (FR-008).
/// </summary>
public sealed class NullPropertyDataProvider : IPropertyDataProvider
{
    public Task<PropertyDataLookup> LookupAsync(PropertyAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        return Task.FromResult<PropertyDataLookup>(new PropertyDataLookup.NotConfigured());
    }
}

/// <summary>
/// The fields FR-008 pre-fills from a public record, and leaves empty and required otherwise.
/// </summary>
public static class PublicRecordFields
{
    public const string LivingArea = "LivingArea";
    public const string LotSizeSquareFeet = "LotSizeSquareFeet";
    public const string YearBuilt = "YearBuilt";
    public const string TaxAnnualAmount = "TaxAnnualAmount";

    /// <summary>
    /// Square footage, lot size, year built and tax — the FR-008 set. Every one is also an
    /// FR-007 field, accepting only agent-entered or public-record provenance.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
        [LivingArea, LotSizeSquareFeet, YearBuilt, TaxAnnualAmount];
}
