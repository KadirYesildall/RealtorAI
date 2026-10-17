namespace Libs.Compliance;

/// <summary>One photo as the integrity check reads it.</summary>
/// <param name="AlterationType">The contract's spelling: <c>virtual_staging</c> or <c>other</c>.</param>
/// <param name="ContentFlags">The analysis's content flags for the photo, when it was analysed.</param>
public sealed record PhotoFacts(
    Guid Id,
    int OrderIndex,
    bool Altered,
    string? AlterationType,
    string? DisclosureLabel,
    Guid? PairedOriginalId,
    IReadOnlyList<string> ContentFlags);

/// <summary>
/// T133 — AB 723 photo integrity (FR-020; Story 4 scenarios 5, 6; research R4).
///
/// <para>
/// <b>Nothing here was generated.</b> The system never alters a pixel (VIII.6), and a
/// prohibitions test fails the build if an image-generation dependency appears. So every photo is
/// an upload, and the check is about the photos the agent altered elsewhere. Each needs all three
/// parts — the flag with its type, a label from the MLS's approved list, and the unaltered
/// original adjacent in the order. An alteration type the MLS does not permit is blocking.
/// </para>
/// <para>
/// <b>Fails closed.</b> With no profile there is no list of permitted alterations or approved
/// labels, so a declared alteration cannot pass. The same holds with no approved label recorded,
/// which is CRMLS's state until counsel confirms the label text (T161).
/// </para>
/// </summary>
public static class PhotoIntegrityChecker
{
    /// <summary>Content the agent must resolve before the photo is published (Story 2 scenario 4).</summary>
    private static readonly Dictionary<string, string> ContentFlagReasons = new(StringComparer.Ordinal)
    {
        ["people_visible"] = "People are visible in this photo. Retake it, or crop them out, before publishing.",
        ["personal_documents"] = "Personal documents or photographs are visible in this photo.",
        ["house_number"] = "A house number, street sign or address is legible in this photo.",
        ["license_plate"] = "A vehicle license plate is legible in this photo.",
        ["watermark_or_branding"] = "This photo carries a watermark or another company's branding.",
    };

    public static IReadOnlyList<Finding> Check(PhotoFacts photo, IReadOnlyList<PhotoFacts> all, MlsRules? rules)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(all);

        var findings = new List<Finding>();

        if (photo.Altered)
        {
            if (Alteration(photo, all, rules) is { } finding)
            {
                findings.Add(finding);
            }
        }
        else if (photo.ContentFlags.Contains("possible_prior_alteration"))
        {
            findings.Add(Warning(
                "photo.possible_alteration",
                "This photo shows signs of having been edited. Confirm it is unaltered, or declare the alteration."));
        }

        foreach (var flag in photo.ContentFlags.Where(ContentFlagReasons.ContainsKey).Order(StringComparer.Ordinal))
        {
            findings.Add(Warning($"photo.content.{flag}", ContentFlagReasons[flag]));
        }

        return findings;
    }

    private static Finding? Alteration(PhotoFacts photo, IReadOnlyList<PhotoFacts> all, MlsRules? rules)
    {
        if (photo.AlterationType is null || photo.DisclosureLabel is null || photo.PairedOriginalId is null)
        {
            return Blocking(
                "photo.alteration_incomplete",
                "An altered photo needs the type of alteration, an approved disclosure label, and the unaltered original beside it.");
        }

        if (rules is null)
        {
            return Blocking(
                "photo.alteration_unverifiable",
                "The listing has no MLS rule profile, so there is no list of permitted alterations or approved labels to check this photo against.");
        }

        if (!rules.AllowedAlterationTypes.Contains(photo.AlterationType, StringComparer.Ordinal))
        {
            return Blocking(
                "photo.alteration_not_permitted",
                "The MLS does not permit this kind of alteration to be presented. Only " + string.Join(", ", rules.AllowedAlterationTypes) + " may be.");
        }

        if (!rules.DisclosureLabels.Contains(photo.DisclosureLabel, StringComparer.Ordinal))
        {
            return Blocking(
                "photo.label_not_approved",
                rules.DisclosureLabels.Count == 0
                    ? "No disclosure label has been approved for this MLS yet, so an altered photo cannot be disclosed correctly."
                    : "The disclosure label is not one the MLS has approved.");
        }

        var original = all.FirstOrDefault(candidate => candidate.Id == photo.PairedOriginalId);

        if (original is null || Math.Abs(original.OrderIndex - photo.OrderIndex) != 1)
        {
            return Blocking(
                "photo.original_not_adjacent",
                "The unaltered original must be on the listing, directly beside the altered photo in the order.");
        }

        if (original.Altered)
        {
            return Blocking("photo.original_altered", "The photo paired as the original is itself declared altered.");
        }

        return null;
    }

    private static Finding Blocking(string ruleId, string reason) =>
        new(ruleId, CheckType.PhotoIntegrity, Severity.Blocking, FindingSource.Deterministic, reason);

    private static Finding Warning(string ruleId, string reason) =>
        new(ruleId, CheckType.PhotoIntegrity, Severity.Warning, FindingSource.Deterministic, reason);
}
