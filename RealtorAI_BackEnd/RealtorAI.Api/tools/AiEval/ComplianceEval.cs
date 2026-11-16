using System.Globalization;
using Libs.Ai;
using Libs.Compliance;

namespace AiEval;

/// <summary>
/// One labelled passage of the Fair Housing evaluation set, as committed in
/// <c>EvalSets/compliance.judge/fair-housing-v1.json</c>.
/// </summary>
/// <param name="Id">Stable identifier (<c>fh-v-001</c>, <c>fh-c-001</c>), unique in the set.</param>
/// <param name="Text">The listing-remark passage the check is run on, exactly as an agent's draft would hold it.</param>
/// <param name="Label"><see cref="ComplianceEval.Violation"/> or <see cref="ComplianceEval.Compliant"/>.</param>
/// <param name="ProtectedClass">
/// For a violation, the class it concerns (one of <see cref="ComplianceEval.ProtectedClasses"/>).
/// For a compliant passage, the class its near miss resembles, or null for ordinary copy — so a
/// false positive can be traced to the words that invited it.
/// </param>
/// <param name="Category">How the passage is built: one of <see cref="ComplianceEval.ViolationCategories"/> or <see cref="ComplianceEval.CompliantCategories"/>.</param>
/// <param name="Rationale">Why the label is what it is, for the reviewer.</param>
/// <param name="Citation">The authority the label rests on.</param>
internal sealed record FairHousingSample(
    string Id,
    string Text,
    string Label,
    string? ProtectedClass,
    string Category,
    string Rationale,
    string Citation)
{
    internal bool IsViolation => Label == ComplianceEval.Violation;
}

/// <summary>What the Fair Housing check produced for one sample.</summary>
/// <param name="Call">The judge call's record, for the schema-validity rate.</param>
/// <param name="Findings">
/// The check's Fair Housing findings: lexicon and located judge findings, merged as production
/// merges them. Empty when the check raised nothing.
/// </param>
internal sealed record FairHousingSampleResult(
    FairHousingSample Sample,
    EvalCallRecord Call,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>The check raised at least one Fair Housing finding, of either severity — see <see cref="ComplianceEval"/>.</summary>
    internal bool Flagged => Findings.Count > 0;
}

/// <summary>
/// T157 — measures SC-003 for a <c>compliance.judge</c> model: Fair Housing recall, the
/// false-positive rate, and the judge's JSON-schema validity rate (FR-015).
///
/// <para>
/// <b>What SC-003 measures: the Fair Housing check, lexicon and judge together.</b> SC-003 reads
/// "The Fair Housing check catches at least 95% of violations on the internal labeled test set,
/// with a false-positive rate of 10% or less." The specification says what that check is.
/// FR-017: "System MUST run Fair Housing checks on all text using both a deterministic phrase
/// lexicon and a context-sensitive AI classifier". Story 4 scenario 2: "Given the Fair Housing
/// check, When it evaluates text, Then it applies both a deterministic, maintainable phrase lexicon
/// and a context-sensitive AI classifier". So the measured object is the two-layer check, not the
/// judge alone. Each sample goes through production's own pieces, in production's order
/// (<c>ComplianceService.CheckTextAsync</c>): <see cref="LexiconMatcher.FairHousing"/>, the judge
/// request <see cref="JudgeChecker.BuildInput"/> sent to the model under evaluation, its answer
/// read by <see cref="JudgeChecker.ReadFindings"/>, and the two merged by
/// <see cref="FindingMerger.Merge"/>.
/// </para>
/// <para>
/// The verdict is still recorded against the judge's registry row, because the judge is the only
/// part of the check that can change without a reviewed code change. The question the harness
/// answers is "with this judge model, does the Fair Housing check meet SC-003?". So the lexicon's
/// own misses and false positives count against every judge. They are false positives an agent
/// would see.
/// </para>
/// <para>
/// <b>What "flagged" means.</b> A sample is flagged when the merged check yields at least one
/// Fair Housing finding, of either severity. Both severities count because the judge's schema and
/// the lexicon have two (<c>blocking</c> and <c>warning</c>), and production raises both as open
/// findings on the draft. A warning must be acknowledged with a recorded reason (FR-023), so it
/// catches a violation and it costs an agent time on compliant copy. A judge phrase that cannot be
/// found in the text is dropped by <see cref="JudgeChecker.ReadFindings"/> and flags nothing,
/// because production drops it too. The judge's free-text <c>protectedClass</c> is not compared
/// with the label: production stops copy for review whatever class the judge names. Scoring is
/// per passage, not per span: a passage is one draft's text, and the set labels passages.
/// </para>
/// <list type="bullet">
///   <item><description><c>fair_housing_recall</c> = flagged violations / violations.</description></item>
///   <item><description><c>fair_housing_false_positive_rate</c> = flagged compliant / compliant.</description></item>
///   <item><description><c>schema_validity_rate</c> = judge answers valid on the first attempt / judge
///   calls (<see cref="CallMetrics.SchemaValidityRate"/>). Its threshold stays unset (research R27).</description></item>
/// </list>
/// <para>
/// <b>A judge answer that fails validation twice</b> leaves the sample with the lexicon's findings
/// only. Production does the same: it catches the <see cref="StructuredOutputException"/> and
/// keeps the deterministic layers (research R36). The failure is also counted against the
/// schema-validity rate.
/// </para>
/// <para>
/// <b>Fail closed on a provider failure.</b> If any judge call failed at the provider, recall and
/// the false-positive rate are withheld as well as schema validity. Scoring those samples on the
/// lexicon alone would describe an outage, and dropping them would bias the sample. A withheld
/// metric cannot pass (<see cref="EvalVerdict"/>).
/// </para>
/// <para>
/// <b>One call at a time</b>, as production judges one draft per call. No latency metric is
/// defined for this task, so the order changes nothing measured. It keeps the run's load on the
/// provider modest.
/// </para>
/// </summary>
internal sealed class ComplianceEval : IEvalSuite
{
    /// <summary>The shipped evaluation set this suite measures on.</summary>
    internal const string SetFileName = "fair-housing-v1.json";

    internal const string Violation = "violation";
    internal const string Compliant = "compliant";

    /// <summary>
    /// The <c>content_kind</c> label every sample is judged as. The samples are public-remarks
    /// passages, so the judge receives exactly what it receives for a public-remarks draft
    /// (<c>ContentKind.PublicRemarks</c>, 0007_content_compliance.sql). A unit test pins this to
    /// the host's label.
    /// </summary>
    internal const string ContentKind = "public_remarks";

    /// <summary>
    /// The classes a violation may name. They are the classes the check is configured to cover:
    /// the federal seven (FR-017) and the California additions the judge prompt
    /// (<c>prompts/compliance.judge/v1.md</c>) lists. The names are the prompt's lower-case,
    /// underscored form, and gender identity and gender expression are one class there, so here
    /// too. They match Cal. Gov. Code 12955(c)'s list, plus age, which 12955(c) does not name and
    /// Cal. Civ. Code 51.2(a) protects in housing. Medical condition is a FEHA class in employment
    /// but is not in 12955(c), and the prompt does not list it, so no sample may name it: the set
    /// measures the classes the check is configured for, not a wider list.
    /// </summary>
    internal static readonly IReadOnlySet<string> ProtectedClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "race",
        "color",
        "religion",
        "sex",
        "disability",
        "familial_status",
        "national_origin",
        "sexual_orientation",
        "gender_identity_or_expression",
        "marital_status",
        "ancestry",
        "source_of_income",
        "military_or_veteran_status",
        "genetic_information",
        "age",
    };

    /// <summary>
    /// How a violation is phrased: an explicit exclusion or limitation, a description of the
    /// ideal occupant, a neighborhood described by its residents, a place of worship (or other
    /// group landmark) as a selling point, or coded wording.
    /// </summary>
    internal static readonly IReadOnlySet<string> ViolationCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "explicit",
        "preference",
        "neighborhood",
        "landmark",
        "coded",
    };

    /// <summary>
    /// What a compliant passage is: a near miss (wording a keyword match would trip on that the
    /// law treats as lawful), a stated housing-for-older-persons exemption, or ordinary copy.
    /// </summary>
    internal static readonly IReadOnlySet<string> CompliantCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "near_miss",
        "lawful_exemption",
        "ordinary",
    };

    private readonly Func<EvalSet> loadSet;
    private readonly TextWriter report;

    /// <summary>The shipped suite: the committed set, reported to the console.</summary>
    internal ComplianceEval()
        : this(() => EvalSet.LoadShipped(AiTaskKey.ComplianceJudge, SetFileName), Console.Out)
    {
    }

    /// <param name="loadSet">Loads the set to measure on. Tests pass a manifest written for the test.</param>
    /// <param name="report">Where the per-layer breakdown and the misses are written.</param>
    internal ComplianceEval(Func<EvalSet> loadSet, TextWriter report)
    {
        ArgumentNullException.ThrowIfNull(loadSet);
        ArgumentNullException.ThrowIfNull(report);

        this.loadSet = loadSet;
        this.report = report;
    }

    public string TaskKey => AiTaskKey.ComplianceJudge;

    public async Task<SuiteRun> RunAsync(EvalModelInvoker model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Target.TaskKey != TaskKey)
        {
            throw new InvalidOperationException(
                $"The Fair Housing suite judges '{TaskKey}' models; registry entry {model.Target.Id} serves '{model.Target.TaskKey}'.");
        }

        var set = loadSet();
        var samples = ReadSamples(set);
        var results = new List<FairHousingSampleResult>(samples.Count);

        foreach (var sample in samples)
        {
            results.Add(await CheckAsync(model, sample, cancellationToken));
        }

        var measurement = Measure(results);

        WriteBreakdown(set, results);

        return new SuiteRun([set], measurement.Values, measurement.Unmeasured);
    }

    /// <summary>
    /// Runs one sample through the Fair Housing check as <c>ComplianceService</c> runs it on a
    /// draft, with the model under evaluation as the judge.
    /// </summary>
    internal static async Task<FairHousingSampleResult> CheckAsync(
        EvalModelInvoker model,
        FairHousingSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sample);

        var lexicon = LexiconMatcher.FairHousing.Match(sample.Text);

        var call = await model.InvokeAsync(JudgeChecker.BuildInput(ContentKind, sample.Text), [], cancellationToken);

        IReadOnlyList<Finding> judged = call.Document is { } document
            ? JudgeChecker.ReadFindings(sample.Text, document)
            : [];

        return new FairHousingSampleResult(sample, call.Record, FindingMerger.Merge(lexicon, judged));
    }

    /// <summary>
    /// The task's three metrics from the per-sample results. Nothing here compares a value with a
    /// threshold; <see cref="EvalVerdict"/> does.
    /// </summary>
    internal static CallMeasurement Measure(IReadOnlyList<FairHousingSampleResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var callLevel = CallMetrics.Measure(
            [.. results.Select(result => result.Call)],
            [],
            [MetricCatalog.SchemaValidityRate]);

        var values = new Dictionary<string, double>(callLevel.Values, StringComparer.Ordinal);
        var unmeasured = new List<string>(callLevel.Unmeasured);

        if (results.Any(result => result.Call.Outcome == EvalCallOutcome.ProviderFailed))
        {
            unmeasured.Add(
                $"{MetricCatalog.FairHousingRecall}, {MetricCatalog.FairHousingFalsePositiveRate}: some samples "
                + "were never judged, and scoring them on the lexicon alone would measure the outage, not "
                + "the model. Run the evaluation again when the provider is healthy.");

            return new CallMeasurement(values, unmeasured);
        }

        var violations = results.Where(result => result.Sample.IsViolation).ToList();
        var compliant = results.Where(result => !result.Sample.IsViolation).ToList();

        if (violations.Count == 0)
        {
            unmeasured.Add($"{MetricCatalog.FairHousingRecall}: the set has no violation samples.");
        }
        else
        {
            values[MetricCatalog.FairHousingRecall] = FlaggedShare(violations);
        }

        if (compliant.Count == 0)
        {
            unmeasured.Add($"{MetricCatalog.FairHousingFalsePositiveRate}: the set has no compliant samples.");
        }
        else
        {
            values[MetricCatalog.FairHousingFalsePositiveRate] = FlaggedShare(compliant);
        }

        return new CallMeasurement(values, unmeasured);
    }

    /// <summary>Flagged samples over all samples given: recall over violations, the false-positive rate over compliant ones.</summary>
    /// <exception cref="ArgumentException">No samples — a rate over none is undefined.</exception>
    internal static double FlaggedShare(IReadOnlyList<FairHousingSampleResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            throw new ArgumentException("A rate over no samples is undefined.", nameof(results));
        }

        return (double)results.Count(result => result.Flagged) / results.Count;
    }

    /// <summary>
    /// The set's samples, checked against the closed sets above. A malformed sample would silently
    /// change a rate's denominator, so it stops the run before any model is called.
    /// </summary>
    /// <exception cref="InvalidDataException">A sample is missing a field, uses an unknown value, or repeats an id.</exception>
    internal static IReadOnlyList<FairHousingSample> ReadSamples(EvalSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var samples = set.ItemsAs<FairHousingSample>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var where = $"Evaluation set {set.Name}: items[{index.ToString(CultureInfo.InvariantCulture)}]";

            Require(where, "id", sample.Id);
            Require(where, "text", sample.Text);
            Require(where, "rationale", sample.Rationale);
            Require(where, "citation", sample.Citation);

            if (!ids.Add(sample.Id))
            {
                throw new InvalidDataException($"{where}: id '{sample.Id}' is used more than once.");
            }

            switch (sample.Label)
            {
                case Violation:
                    if (sample.ProtectedClass is null || !ProtectedClasses.Contains(sample.ProtectedClass))
                    {
                        throw new InvalidDataException(
                            $"{where} ({sample.Id}): a violation must name its protected class, one of "
                            + $"{string.Join(", ", ProtectedClasses)}; got '{sample.ProtectedClass}'.");
                    }

                    RequireCategory(where, sample, ViolationCategories);
                    break;

                case Compliant:
                    if (sample.ProtectedClass is { } resembles && !ProtectedClasses.Contains(resembles))
                    {
                        throw new InvalidDataException(
                            $"{where} ({sample.Id}): protectedClass '{resembles}' is not a known class.");
                    }

                    RequireCategory(where, sample, CompliantCategories);
                    break;

                default:
                    throw new InvalidDataException(
                        $"{where} ({sample.Id}): label must be '{Violation}' or '{Compliant}', not '{sample.Label}'.");
            }
        }

        return samples;
    }

    /// <summary>
    /// Which layer caught what, and which samples were missed or wrongly flagged. Printed so an
    /// operator developing or reviewing the set can see the evidence behind the two rates.
    /// </summary>
    private void WriteBreakdown(EvalSet set, IReadOnlyList<FairHousingSampleResult> results)
    {
        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

        var violations = results.Where(result => result.Sample.IsViolation).ToList();
        var compliant = results.Where(result => !result.Sample.IsViolation).ToList();

        int ByLayer(IEnumerable<FairHousingSampleResult> group, FindingSource source) =>
            group.Count(result => result.Findings.Any(finding => finding.Source == source));

        report.WriteLine(
            $"Fair Housing check on {set.Name}: {Count(violations.Count(result => result.Flagged))} of "
            + $"{Count(violations.Count)} violations flagged (lexicon {Count(ByLayer(violations, FindingSource.Lexicon))}, "
            + $"judge {Count(ByLayer(violations, FindingSource.Judge))}); {Count(compliant.Count(result => result.Flagged))} of "
            + $"{Count(compliant.Count)} compliant samples flagged (lexicon {Count(ByLayer(compliant, FindingSource.Lexicon))}, "
            + $"judge {Count(ByLayer(compliant, FindingSource.Judge))}).");

        var missed = violations.Where(result => !result.Flagged).Select(result => result.Sample.Id).ToList();
        var falsePositives = compliant.Where(result => result.Flagged).Select(result => result.Sample.Id).ToList();

        if (missed.Count > 0)
        {
            report.WriteLine($"  Missed violations: {string.Join(", ", missed)}");
        }

        if (falsePositives.Count > 0)
        {
            report.WriteLine($"  Flagged compliant samples: {string.Join(", ", falsePositives)}");
        }
    }

    private static void Require(string where, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{where}: {name} is missing or blank.");
        }
    }

    private static void RequireCategory(string where, FairHousingSample sample, IReadOnlySet<string> allowed)
    {
        if (sample.Category is null || !allowed.Contains(sample.Category))
        {
            throw new InvalidDataException(
                $"{where} ({sample.Id}): a {sample.Label} sample's category must be one of "
                + $"{string.Join(", ", allowed)}; got '{sample.Category}'.");
        }
    }
}
