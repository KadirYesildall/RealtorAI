using System.Text.RegularExpressions;
using Libs.Reso;

namespace Modules.Listings;

/// <summary>
/// Where a listing is in its life. Domain vocabulary, so it is declared here; its database
/// labels (<c>public.listing_status</c>, 0005) are listed in <c>Api.Data.PostgresEnumMappings</c>.
/// </summary>
public enum ListingStatus
{
    Draft,
    Analyzing,
    ReadyForReview,
    Approved,
    Archived,
}

/// <summary>A transition the lifecycle does not permit.</summary>
public sealed class InvalidListingTransitionException : InvalidOperationException
{
    public InvalidListingTransitionException(ListingStatus from, ListingStatus to)
        : base($"A listing cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public InvalidListingTransitionException()
    {
    }

    public InvalidListingTransitionException(string message)
        : base(message)
    {
    }

    public InvalidListingTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ListingStatus From { get; }

    public ListingStatus To { get; }
}

/// <summary>
/// T073 — the listing state machine (data-model.md "Listings").
///
/// <code>
///   draft ──► analyzing ──► ready_for_review ──► approved
///     ▲           │               ▲                  │
///     └───────────┘ (total        └──────────────────┘ (an approved item is edited, FR-025)
///                    failure)
///   draft ──────────────────────► ready_for_review       (completed by hand, SC-010)
///   any ─────────────────────────────────────────────► archived   (soft delete, FR-067; final)
/// </code>
///
/// <para>
/// <b>The one transition data-model.md does not draw.</b> <c>draft → ready_for_review</c>
/// without analysis is how SC-010's manual path reaches review: every registry entry is
/// disabled until its evaluation passes, so during development and in any AI outage there is no
/// analysis to run, and a listing that could only reach review through <c>analyzing</c> could
/// never be completed by hand. Recorded in research R33.
/// </para>
/// <para>
/// Pure and side-effect free, so every rule is a table-driven unit test.
/// </para>
/// </summary>
public static class ListingLifecycle
{
    private static readonly HashSet<(ListingStatus From, ListingStatus To)> Allowed =
    [
        (ListingStatus.Draft, ListingStatus.Analyzing),
        (ListingStatus.Draft, ListingStatus.ReadyForReview),
        (ListingStatus.Analyzing, ListingStatus.ReadyForReview),
        (ListingStatus.Analyzing, ListingStatus.Draft),
        (ListingStatus.ReadyForReview, ListingStatus.Approved),
        (ListingStatus.Approved, ListingStatus.ReadyForReview),
        (ListingStatus.Draft, ListingStatus.Archived),
        (ListingStatus.Analyzing, ListingStatus.Archived),
        (ListingStatus.ReadyForReview, ListingStatus.Archived),
        (ListingStatus.Approved, ListingStatus.Archived),
    ];

    public static bool CanTransition(ListingStatus from, ListingStatus to) => Allowed.Contains((from, to));

    /// <exception cref="InvalidListingTransitionException">The transition is not permitted.</exception>
    public static ListingStatus Transition(ListingStatus from, ListingStatus to) =>
        CanTransition(from, to) ? to : throw new InvalidListingTransitionException(from, to);

    /// <summary>
    /// Only an archived listing is closed to edits. It stays readable for its history, but
    /// editing it would restart work the agent chose to stop (FR-067).
    /// </summary>
    public static bool IsEditable(ListingStatus status) => status != ListingStatus.Archived;

    /// <summary>
    /// The status after the agent edits the listing. Editing an approved listing returns it to
    /// review, because the approval covered content that no longer exists (FR-025); every other
    /// editable status is unchanged.
    /// </summary>
    /// <exception cref="InvalidListingTransitionException">The listing is archived.</exception>
    public static ListingStatus AfterEdit(ListingStatus status) => status switch
    {
        ListingStatus.Archived => throw new InvalidListingTransitionException(
            "An archived listing cannot be edited (FR-067)."),
        ListingStatus.Approved => ListingStatus.ReadyForReview,
        _ => status,
    };
}

/// <summary>
/// The six basic details of Story 1 — address, property type, price, bedrooms, bathrooms and the
/// agent's own notes — after validation and normalization.
/// </summary>
public sealed record ListingBasics(
    string AddressLine1,
    string City,
    string State,
    string PostalCode,
    string PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes);

/// <summary>The basics as a client sent them, before validation.</summary>
public sealed record ListingBasicsInput(
    string? AddressLine1,
    string? City,
    string? State,
    string? PostalCode,
    string? PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes);

/// <summary>
/// T073 — the rules a listing's basic details must satisfy, shared by creation and autosave so
/// a value cannot be accepted by one and refused by the other.
/// </summary>
public static partial class ListingBasicsRules
{
    public const int MaxTextLength = 200;

    public const int MaxNotesLength = 5000;

    /// <summary><c>numeric(12, 2)</c>: ten integer digits.</summary>
    public const decimal MaxListPrice = 9_999_999_999.99m;

    public const int MaxBedrooms = 99;

    /// <summary><c>numeric(3, 1)</c>, and bathrooms come in halves.</summary>
    public const decimal MaxBathrooms = 99.5m;

    /// <summary>
    /// Validates and normalizes. Returns the basics, or the errors keyed by the request's
    /// property name, each written for the agent.
    /// </summary>
    public static (ListingBasics? Basics, Dictionary<string, string[]> Errors) Validate(ListingBasicsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var address = RequiredText(input.AddressLine1, "addressLine1", "Enter the street address.", errors);
        var city = RequiredText(input.City, "city", "Enter the city.", errors);

        var state = input.State?.Trim();
        if (state is null || !StateCode().IsMatch(state))
        {
            errors["state"] = ["Use the two-letter state code, e.g. CA."];
        }

        var postalCode = input.PostalCode?.Trim();
        if (postalCode is null || !ZipCode().IsMatch(postalCode))
        {
            errors["postalCode"] = ["Use a five-digit ZIP code, optionally with its four-digit extension (92618 or 92618-1234)."];
        }

        var propertyType = input.PropertyType?.Trim();
        if (!ResoPropertySubType.IsResidential(propertyType))
        {
            errors["propertyType"] = [
                "Choose a residential property type: " + string.Join(", ", ResoPropertySubType.Residential) + ".",
            ];
        }

        if (input.ListPrice is { } price && (price < 0 || price > MaxListPrice || decimal.Round(price, 2) != price))
        {
            errors["listPrice"] = ["The list price must be a non-negative amount in dollars and cents."];
        }

        if (input.Bedrooms is { } bedrooms && (bedrooms < 0 || bedrooms > MaxBedrooms))
        {
            errors["bedrooms"] = [$"Bedrooms must be a whole number from 0 to {MaxBedrooms}."];
        }

        if (input.Bathrooms is { } bathrooms
            && (bathrooms < 0 || bathrooms > MaxBathrooms || decimal.Remainder(bathrooms * 2, 1) != 0))
        {
            errors["bathrooms"] = [$"Bathrooms are counted in halves, from 0 to {MaxBathrooms} (e.g. 2.5)."];
        }

        var notes = string.IsNullOrWhiteSpace(input.AgentNotes) ? null : input.AgentNotes.Trim();
        if (notes is { Length: > MaxNotesLength })
        {
            errors["agentNotes"] = [$"Notes can be at most {MaxNotesLength} characters."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (
            new ListingBasics(
                address!,
                city!,
                state!,
                postalCode!,
                propertyType!,
                input.ListPrice,
                input.Bedrooms,
                input.Bathrooms,
                notes),
            errors);
    }

    private static string? RequiredText(string? value, string field, string message, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = [message];
            return null;
        }

        if (trimmed.Length > MaxTextLength)
        {
            errors[field] = [$"This can be at most {MaxTextLength} characters."];
            return null;
        }

        return trimmed;
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex StateCode();

    [GeneratedRegex("^[0-9]{5}(-[0-9]{4})?$")]
    private static partial Regex ZipCode();
}
