using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Libs.Compliance;

/// <summary>One lexicon rule: a phrase pattern, the class it concerns, and what to write instead.</summary>
/// <param name="Jurisdiction"><c>federal</c>, or the state whose law adds the class (<c>CA</c>).</param>
public sealed record LexiconRule(
    string Id,
    string Pattern,
    string ProtectedClass,
    string Jurisdiction,
    Severity Severity,
    string Explanation,
    string Rewrite);

/// <summary>
/// T128 — the deterministic Fair Housing layer (FR-017; Story 4 scenarios 1, 2).
///
/// <para>
/// <b>The rules are data.</b> They live in a versioned file, <c>Lexicon/fair-housing.v1.json</c>,
/// so a compliance reviewer can read and change them without reading code, and a finding can be
/// traced to the exact version that raised it. The file says it has not been reviewed by counsel
/// (<see cref="Reviewed"/>); research R36 records that.
/// </para>
/// <para>
/// <b>Matching.</b> Case-insensitive, on word boundaries, every occurrence. Where two rules match
/// overlapping text, the one starting first is kept, so one phrase is one finding. The suggested
/// rewrite takes the case of the phrase it replaces.
/// </para>
/// </summary>
public sealed class LexiconMatcher
{
    private readonly IReadOnlyList<(LexiconRule Rule, Regex Pattern)> compiled;

    private LexiconMatcher(string version, bool reviewed, IReadOnlyList<LexiconRule> rules)
    {
        Version = version;
        Reviewed = reviewed;
        Rules = rules;
        compiled = rules
            .Select(rule => (rule, new Regex(
                $@"\b(?:{rule.Pattern})\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1))))
            .ToList();
    }

    /// <summary>The Fair Housing lexicon shipped with this build.</summary>
    public static LexiconMatcher FairHousing { get; } = Load("Libs.Compliance.Lexicon.fair-housing.v1.json");

    public string Version { get; }

    /// <summary>Whether counsel or the MLS's compliance staff have reviewed the rules.</summary>
    public bool Reviewed { get; }

    public IReadOnlyList<LexiconRule> Rules { get; }

    public IReadOnlyList<Finding> Match(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var matches = compiled.SelectMany(entry => entry.Pattern.Matches(text).Select(match => new Finding(
            entry.Rule.Id,
            CheckType.FairHousing,
            entry.Rule.Severity,
            FindingSource.Lexicon,
            entry.Rule.Explanation,
            match.Index,
            match.Index + match.Length,
            match.Value,
            Spans.MatchCase(match.Value, entry.Rule.Rewrite))));

        return Spans.WithoutOverlaps(matches);
    }

    private static LexiconMatcher Load(string resource)
    {
        using var stream = typeof(LexiconMatcher).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The lexicon {resource} is not embedded in Libs.Compliance.");

        var document = JsonNode.Parse(stream)!.AsObject();

        var rules = document["rules"]!.AsArray()
            .Select(node => new LexiconRule(
                node!["id"]!.GetValue<string>(),
                node["pattern"]!.GetValue<string>(),
                node["protectedClass"]!.GetValue<string>(),
                node["jurisdiction"]!.GetValue<string>(),
                node["severity"]!.GetValue<string>() switch
                {
                    "blocking" => Severity.Blocking,
                    "warning" => Severity.Warning,
                    var other => throw new InvalidOperationException($"Lexicon rule {node["id"]} has an unknown severity '{other}'."),
                },
                node["explanation"]!.GetValue<string>(),
                node["rewrite"]!.GetValue<string>()))
            .ToList();

        return new LexiconMatcher(document["version"]!.GetValue<string>(), document["reviewed"]!.GetValue<bool>(), rules);
    }
}
