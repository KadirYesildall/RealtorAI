namespace Modules.Content;

/// <summary>The kinds of listing copy, in the contract's spelling (<c>ContentKind</c>).</summary>
public static class ContentKinds
{
    public const string PublicRemarks = "public_remarks";
    public const string PrivateRemarks = "private_remarks";
    public const string Headline = "headline";

    /// <summary>Every kind, in the order a full pipeline run generates them.</summary>
    public static IReadOnlyList<string> All { get; } = [PublicRemarks, PrivateRemarks, Headline];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>The MLS rule profile as content generation needs it (research R2).</summary>
/// <param name="Verified">Whether the profile's rulebook has been recorded as verified.</param>
public sealed record ContentProfile(bool Verified, int? PublicRemarksMaxChars, int? PrivateRemarksMaxChars);

/// <summary>
/// The rules content is generated and edited by (FR-013).
///
/// <para>
/// <b>Limits come from the MLS, verified.</b> Public and private remarks are limited by the
/// profile's own numbers, and only once the profile is verified. Anything less is no limit, and
/// with no limit nothing is generated (research R2): a description written to a guessed limit
/// is one the MLS may truncate after the agent approved it.
/// </para>
/// <para>
/// <b>The headline</b> is not an MLS field. It is a marketing line with no MLS limit to read,
/// so it has a fixed one, the length of a line that fits a listing card. It still needs a
/// verified profile, because it is generated in the same run under the same rules.
/// </para>
/// </summary>
public static class ContentRules
{
    public const int HeadlineMaxChars = 100;

    public const int DefaultPublicVariants = 3;

    /// <summary>The character limit for <paramref name="kind"/>, or null when there is none to generate to.</summary>
    public static int? LimitFor(string kind, ContentProfile? profile)
    {
        if (profile is not { Verified: true })
        {
            return null;
        }

        return kind switch
        {
            ContentKinds.PublicRemarks => profile.PublicRemarksMaxChars,
            ContentKinds.PrivateRemarks => profile.PrivateRemarksMaxChars,
            ContentKinds.Headline => HeadlineMaxChars,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a content kind."),
        };
    }

    /// <summary>
    /// How many alternatives to write. Only the public description is offered in 2–3 versions
    /// (FR-013); the private remarks and the headline are one draft each.
    /// </summary>
    public static int VariantsFor(string kind, int? requested) =>
        kind == ContentKinds.PublicRemarks ? requested ?? DefaultPublicVariants : 1;

    /// <summary>
    /// A kind with a single version is selected for the agent. Choosing between alternatives is
    /// theirs to make (Story 3 scenario 4).
    /// </summary>
    public static bool SelectedOnArrival(string kind) => kind != ContentKinds.PublicRemarks;

    public const string ProfileIncompleteReason =
        "The MLS's character limits are not verified yet, so no content was generated. Write the "
        + "description by hand, or generate it once the MLS rule profile is verified.";
}
