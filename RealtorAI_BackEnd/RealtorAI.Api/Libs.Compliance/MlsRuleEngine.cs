using System.Text.RegularExpressions;

namespace Libs.Compliance;

/// <summary>One remarks detector the profile turns on, with the severity the profile gives it.</summary>
/// <param name="Detector"><c>phone_number</c>, <c>email_address</c>, <c>url</c>, <c>agent_name</c> or <c>brokerage_name</c>.</param>
public sealed record RemarksDetector(string Detector, Severity Severity);

/// <summary>The names public remarks must not carry: the listing agent's and the brokerage's.</summary>
public sealed record RemarksNames(IReadOnlyList<string> AgentNames, IReadOnlyList<string> BrokerageNames);

/// <summary>An MLS rule profile as the checks read it (<c>mls_rule_profiles</c>, research R2).</summary>
public sealed record MlsRules(
    bool Verified,
    int? PublicRemarksMaxChars,
    int? PrivateRemarksMaxChars,
    int? PhotoMinWidth,
    int? PhotoMinHeight,
    int? PhotoMinCount,
    int? PhotoMaxCount,
    IReadOnlyList<RemarksDetector> ForbiddenInPublicRemarks,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> AllowedAlterationTypes,
    IReadOnlyList<string> DisclosureLabels);

/// <summary>
/// T132 — the MLS rule engine (FR-019; Story 4 scenario 4; research R2).
///
/// <para>
/// <b>Rules as data.</b> Every limit, which detectors run and how severe each is, the required
/// fields and the photo bounds come from the profile. Changing a rule is a data change, not a
/// code change. The detector patterns themselves are code.
/// </para>
/// <para>
/// <b>Fails closed.</b> With no profile, an unverified one, or a verified one missing any value,
/// the listing carries one blocking <c>mls.profile_incomplete</c> finding naming what is
/// missing, and no rule is checked against a guessed value: no length, count or resolution is
/// compared against a number nobody verified. The remarks detectors still run, because which are
/// on comes from the specification (FR-019), not the MLS's numbers.
/// </para>
/// </summary>
public static partial class MlsRuleEngine
{
    public const string ProfileIncomplete = "mls.profile_incomplete";

    /// <param name="fields">Each field the listing holds, and whether it counts as confirmed.</param>
    public static IReadOnlyList<Finding> CheckListing(MlsRules? rules, IReadOnlyCollection<(string Key, bool Confirmed)> fields, int photoCount)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (Incomplete(rules) is { } incomplete)
        {
            return [incomplete];
        }

        var findings = new List<Finding>();

        foreach (var key in rules!.RequiredFields)
        {
            var field = fields.FirstOrDefault(candidate => candidate.Key == key);

            if (field.Key is null)
            {
                findings.Add(new Finding(
                    "mls.required_field_missing",
                    CheckType.MlsRule,
                    Severity.Blocking,
                    FindingSource.Deterministic,
                    $"The MLS requires {key}, and the listing does not have it."));
            }
            else if (!field.Confirmed)
            {
                findings.Add(new Finding(
                    "mls.required_field_unconfirmed",
                    CheckType.MlsRule,
                    Severity.Blocking,
                    FindingSource.Deterministic,
                    $"The MLS requires {key}, and its value has not been confirmed."));
            }
        }

        if (photoCount < rules.PhotoMinCount)
        {
            findings.Add(new Finding(
                "mls.photo_count_below_minimum",
                CheckType.MlsRule,
                Severity.Blocking,
                FindingSource.Deterministic,
                $"The MLS requires at least {rules.PhotoMinCount} photos; the listing has {photoCount}."));
        }

        if (photoCount > rules.PhotoMaxCount)
        {
            findings.Add(new Finding(
                "mls.photo_count_above_maximum",
                CheckType.MlsRule,
                Severity.Blocking,
                FindingSource.Deterministic,
                $"The MLS allows at most {rules.PhotoMaxCount} photos; the listing has {photoCount}."));
        }

        return findings;
    }

    /// <summary>A photo's original against the profile's minimum resolution.</summary>
    public static IReadOnlyList<Finding> CheckPhoto(int? width, int? height, MlsRules? rules)
    {
        if (Incomplete(rules) is not null)
        {
            return [];
        }

        if (width is null || height is null || width < rules!.PhotoMinWidth || height < rules.PhotoMinHeight)
        {
            return
            [
                new Finding(
                    "mls.photo_below_min_resolution",
                    CheckType.MlsRule,
                    Severity.Blocking,
                    FindingSource.Deterministic,
                    $"The MLS requires photos of at least {rules!.PhotoMinWidth}×{rules.PhotoMinHeight}; this one is {width?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}×{height?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}."),
            ];
        }

        return [];
    }

    /// <param name="kind">The contract's content kind: <c>public_remarks</c>, <c>private_remarks</c> or <c>headline</c>.</param>
    public static IReadOnlyList<Finding> CheckRemarks(string kind, string text, MlsRules? rules, RemarksNames names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        if (rules is null)
        {
            return [];
        }

        var findings = new List<Finding>();
        var limit = kind switch
        {
            "public_remarks" => rules.PublicRemarksMaxChars,
            "private_remarks" => rules.PrivateRemarksMaxChars,
            _ => null,
        };

        // A limit is a number someone must have verified; with none, the listing's fail-closed
        // finding stands instead.
        if (Incomplete(rules) is null && limit is { } max && text.Length > max)
        {
            findings.Add(new Finding(
                "mls.remarks_too_long",
                CheckType.MlsRule,
                Severity.Blocking,
                FindingSource.Deterministic,
                $"The MLS allows at most {max} characters here; this is {text.Length}."));
        }

        // Private remarks are for agents, who need the contact details public remarks must not carry.
        if (kind == "private_remarks")
        {
            return findings;
        }

        var detected = new List<Finding>();

        // The detectors run on an unverified profile too: which ones are on comes from the
        // specification itself (FR-019), not from the MLS's numeric rulebook (0010).
        foreach (var detector in rules.ForbiddenInPublicRemarks)
        {
            var (patterns, reason) = detector.Detector switch
            {
                "phone_number" => ((IEnumerable<Regex>)[Phone()], "Public remarks must not carry a phone number."),
                "email_address" => ([Email()], "Public remarks must not carry an email address."),
                "url" => ([Url()], "Public remarks must not carry a web address."),
                "agent_name" => (Literals(names.AgentNames), "Public remarks must not name the listing agent."),
                "brokerage_name" => (Literals(names.BrokerageNames), "Public remarks must not name the brokerage."),
                _ => throw new InvalidOperationException($"The MLS profile names an unknown detector '{detector.Detector}'."),
            };

            foreach (var match in patterns.SelectMany(pattern => pattern.Matches(text)))
            {
                var value = detector.Detector == "url" ? match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')') : match.Value;

                detected.Add(new Finding(
                    $"mls.forbidden.{detector.Detector}",
                    CheckType.MlsRule,
                    detector.Severity,
                    FindingSource.Deterministic,
                    reason,
                    match.Index,
                    match.Index + value.Length,
                    value));
            }
        }

        return findings.Concat(Spans.WithoutOverlaps(detected)).ToList();
    }

    /// <summary>The fail-closed finding, or null when the profile is verified and complete.</summary>
    private static Finding? Incomplete(MlsRules? rules)
    {
        if (rules is null)
        {
            return IncompleteFinding("The listing has no MLS rule profile, so nothing can be checked against the MLS's rules.");
        }

        if (!rules.Verified)
        {
            return IncompleteFinding("The MLS rule profile has not been verified against the MLS's published rules, so its rules cannot be relied on.");
        }

        var missing = new (string Name, int? Value)[]
            {
                ("public_remarks_max_chars", rules.PublicRemarksMaxChars),
                ("private_remarks_max_chars", rules.PrivateRemarksMaxChars),
                ("photo_min_width", rules.PhotoMinWidth),
                ("photo_min_height", rules.PhotoMinHeight),
                ("photo_min_count", rules.PhotoMinCount),
                ("photo_max_count", rules.PhotoMaxCount),
            }
            .Where(value => value.Value is null)
            .Select(value => value.Name)
            .ToList();

        return missing.Count == 0
            ? null
            : IncompleteFinding("The MLS rule profile is missing " + string.Join(", ", missing) + ", so those rules cannot be checked.");
    }

    private static Finding IncompleteFinding(string reason) =>
        new(ProfileIncomplete, CheckType.MlsRule, Severity.Blocking, FindingSource.Deterministic, reason);

    private static IEnumerable<Regex> Literals(IEnumerable<string> names) =>
        names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new Regex($@"\b{Regex.Escape(name.Trim())}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    [GeneratedRegex(@"(?<![\w(])(?:\+?1[\s.-]?)?(?:\(\d{3}\)|\d{3})[\s.-]?\d{3}[\s.-]\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
