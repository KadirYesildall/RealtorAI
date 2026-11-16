using System.Text.Json.Nodes;
using AiEval;
using Api.Endpoints;
using Libs.Ai;
using Libs.Compliance;
using Microsoft.Extensions.AI;
using Modules.Media;
using ContentKind = Api.Data.ContentKind;

namespace Tests.Unit;

/// <summary>
/// T157 — the Fair Housing suite measures SC-003 on the check production runs, and its shipped
/// evaluation set is well-formed.
///
/// <para>
/// No network: the judge is a scripted chat client behind the real <see cref="EvalModelInvoker"/>,
/// with the repository's real <c>compliance.judge</c> prompt and schema. The properties that matter
/// most are parity ones, which a real run cannot show: the suite sends the judge the request
/// production sends, reads its answer as production reads it, and merges it with the lexicon as
/// production merges them. Without them, a measured recall would be the recall of a different
/// check.
/// </para>
/// </summary>
public sealed class AiEvalComplianceTests : IDisposable
{
    private static readonly ImageNormalizer Normalizer = new(new NormalizationOptions(1280, 85, 100_000_000));

    private static readonly RegistryTarget JudgeTarget = new(
        Guid.Parse("0190f6a1-0000-7000-8000-0000000000c1"),
        AiTaskKey.ComplianceJudge,
        "eval-provider",
        "eval-org/judge-model",
        "https://models.example.test/v1",
        """{"temperature":0}""",
        Enabled: false);

    private static readonly Lazy<IPromptRegistry> RepositoryPrompts = new(() =>
        PromptRegistry.Load(Path.Combine(
            Path.GetDirectoryName(EvalEnvironment.FindApiDirectory(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("The API host directory was not found above the test binaries."))!,
            "prompts")));

    private readonly string root = Path.Combine(Path.GetTempPath(), "aieval-compliance-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ───────────────────────────── Test doubles ─────────────────────────────

    /// <summary>A judge that answers each request from its text, or throws.</summary>
    private sealed class ScriptedJudge(Func<string, string> answer) : IChatClientFactory
    {
        internal Func<string, string> Respond { get; } = answer;

        internal List<string> Inputs { get; } = [];

        internal Exception? Throw { get; init; }

        public IChatClient Create(ModelRegistryEntry entry) => new Client(this);

        private sealed class Client(ScriptedJudge owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var input = messages.Last(message => message.Role == ChatRole.User).Text;
                owner.Inputs.Add(input);

                if (owner.Throw is not null)
                {
                    throw owner.Throw;
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Respond(input)))
                {
                    Usage = new UsageDetails { InputTokenCount = 300, OutputTokenCount = 40 },
                });
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The harness never streams.");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Production's side of the parity checks: an <see cref="IAiService"/> answering the same way.</summary>
    private sealed class ScriptedAiService(Func<string, string> answer) : IAiService
    {
        internal List<AiRequest> Requests { get; } = [];

        public Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AiResult(JsonNode.Parse(answer(request.Input))!, "eval-org/judge-model", 1, "hash", FromCache: false));
        }
    }

    private static EvalModelInvoker Invoker(IChatClientFactory judge) =>
        new(JudgeTarget, RepositoryPrompts.Value, judge, Normalizer);

    private static string TextOf(string input) => JsonNode.Parse(input)!["text"]!.GetValue<string>();

    /// <summary>A schema-valid judge answer reporting <paramref name="findings"/>.</summary>
    private static string Answer(params (string Phrase, string Class, string Severity)[] findings) =>
        new JsonObject
        {
            ["findings"] = new JsonArray([.. findings.Select(finding => (JsonNode)new JsonObject
            {
                ["phrase"] = finding.Phrase,
                ["protectedClass"] = finding.Class,
                ["severity"] = finding.Severity,
                ["reason"] = "States a preference based on a protected class.",
                ["rewrite"] = "Spacious home",
            })]),
        }.ToJsonString();

    private static FairHousingSample Sample(string id, string text, string label, string? protectedClass = "familial_status") =>
        new(id, text, label, protectedClass, label == ComplianceEval.Violation ? "preference" : "near_miss", "Rationale.", "42 U.S.C. 3604(c)");

    private static FairHousingSampleResult Result(string label, bool flagged, EvalCallOutcome outcome = EvalCallOutcome.ValidFirstAttempt) =>
        new(
            Sample(Guid.NewGuid().ToString("N"), "text", label),
            new EvalCallRecord(outcome, TimeSpan.FromSeconds(1), 300, 40, UsageReported: true, ImagesSent: 0,
                outcome == EvalCallOutcome.ValidFirstAttempt ? 1 : 2, outcome == EvalCallOutcome.ProviderFailed ? "503 from provider" : null),
            flagged ? [new Finding("fair_housing.judge.familial_status", CheckType.FairHousing, Severity.Warning, FindingSource.Judge, "Reason.", 0, 4, "text", "Home")] : []);

    private EvalSet WriteSet(params JsonObject[] items)
    {
        var folder = Path.Combine(root, AiTaskKey.ComplianceJudge);
        Directory.CreateDirectory(folder);

        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = "fair-housing-test",
            ["task"] = AiTaskKey.ComplianceJudge,
            ["version"] = 1,
            ["description"] = "Samples written for this test.",
            ["provenance"] = new JsonObject
            {
                ["source"] = "Written for this test.",
                ["rights"] = "Test data.",
                ["labelledBy"] = "Test",
                ["labellingMethod"] = "By hand.",
                ["createdOn"] = "2026-11-16",
            },
            ["review"] = new JsonObject { ["reviewed"] = false, ["reviewer"] = null, ["reviewedAt"] = null, ["notes"] = null },
            ["items"] = new JsonArray([.. items]),
        };

        var path = Path.Combine(folder, "fair-housing-test.json");
        File.WriteAllText(path, manifest.ToJsonString());

        return EvalSet.Load(path);
    }

    private static JsonObject Item(
        string id,
        string text,
        string label,
        string? protectedClass = "familial_status",
        string? category = null,
        string? citation = "42 U.S.C. 3604(c)") => new()
        {
            ["id"] = id,
            ["text"] = text,
            ["label"] = label,
            ["protectedClass"] = protectedClass,
            ["category"] = category ?? (label == ComplianceEval.Violation ? "preference" : "near_miss"),
            ["rationale"] = "Rationale.",
            ["citation"] = citation,
        };

    // ───────────────────────────── Metric math ─────────────────────────────

    [Fact]
    public void Measure_RecallIsFlaggedViolationsOverViolations_AndFalsePositivesAreFlaggedCompliantOverCompliant()
    {
        List<FairHousingSampleResult> results =
        [
            Result(ComplianceEval.Violation, flagged: true),
            Result(ComplianceEval.Violation, flagged: true),
            Result(ComplianceEval.Violation, flagged: true),
            Result(ComplianceEval.Violation, flagged: false),
            Result(ComplianceEval.Compliant, flagged: true),
            Result(ComplianceEval.Compliant, flagged: false),
            Result(ComplianceEval.Compliant, flagged: false),
            Result(ComplianceEval.Compliant, flagged: false),
            Result(ComplianceEval.Compliant, flagged: false, EvalCallOutcome.ValidAfterRetry),
        ];

        var measurement = ComplianceEval.Measure(results);

        Assert.Empty(measurement.Unmeasured);
        Assert.Equal(0.75, measurement.Values[MetricCatalog.FairHousingRecall]);
        Assert.Equal(0.2, measurement.Values[MetricCatalog.FairHousingFalsePositiveRate]);

        // Eight of nine answers were valid when first asked; the rescued one is not.
        Assert.Equal(8.0 / 9, measurement.Values[MetricCatalog.SchemaValidityRate], 12);
        Assert.Equal(3, measurement.Values.Count);
    }

    /// <summary>Scoring unjudged samples on the lexicon alone would measure an outage, not the model.</summary>
    [Fact]
    public void Measure_AnyProviderFailure_WithholdsEveryMetric_AndSaysWhy()
    {
        var measurement = ComplianceEval.Measure(
        [
            Result(ComplianceEval.Violation, flagged: true),
            Result(ComplianceEval.Violation, flagged: false, EvalCallOutcome.ProviderFailed),
            Result(ComplianceEval.Compliant, flagged: false),
        ]);

        Assert.Empty(measurement.Values);
        Assert.Contains(measurement.Unmeasured, reason => reason.Contains(MetricCatalog.FairHousingRecall, StringComparison.Ordinal));
        Assert.Contains(measurement.Unmeasured, reason => reason.Contains(MetricCatalog.SchemaValidityRate, StringComparison.Ordinal));
    }

    [Fact]
    public void Measure_ASetWithoutOneLabel_LeavesThatRateUnmeasured()
    {
        var measurement = ComplianceEval.Measure([Result(ComplianceEval.Violation, flagged: true)]);

        Assert.Equal(1.0, measurement.Values[MetricCatalog.FairHousingRecall]);
        Assert.False(measurement.Values.ContainsKey(MetricCatalog.FairHousingFalsePositiveRate));
        Assert.Contains(measurement.Unmeasured, reason => reason.Contains(MetricCatalog.FairHousingFalsePositiveRate, StringComparison.Ordinal));
    }

    [Fact]
    public void FlaggedShare_OfNoSamples_IsUndefined() =>
        Assert.Throws<ArgumentException>(() => ComplianceEval.FlaggedShare([]));

    // ───────────────────────────── What "flagged" means ─────────────────────────────

    /// <summary>A warning is an open finding the agent must acknowledge (FR-023), so it catches.</summary>
    [Fact]
    public async Task Check_AJudgeWarning_Flags()
    {
        var sample = Sample("v1", "Lovely home, great for empty nesters.", ComplianceEval.Violation);
        var judge = new ScriptedJudge(_ => Answer(("great for empty nesters", "familial_status", "warning")));

        var result = await ComplianceEval.CheckAsync(Invoker(judge), sample, default);

        Assert.True(result.Flagged);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(FindingSource.Judge, finding.Source);
    }

    /// <summary>A phrase nobody can find in the text is dropped by production, so it flags nothing here.</summary>
    [Fact]
    public async Task Check_AJudgePhraseNotInTheText_DoesNotFlag()
    {
        var sample = Sample("c1", "Sunny family room with a fireplace.", ComplianceEval.Compliant);
        var judge = new ScriptedJudge(_ => Answer(("perfect for families", "familial_status", "blocking")));

        var result = await ComplianceEval.CheckAsync(Invoker(judge), sample, default);

        Assert.False(result.Flagged);
        Assert.Equal(EvalCallOutcome.ValidFirstAttempt, result.Call.Outcome);
    }

    /// <summary>The lexicon is part of the check SC-003 measures: what it catches counts even when the judge sees nothing.</summary>
    [Fact]
    public async Task Check_TheLexiconAlone_Flags()
    {
        var sample = Sample("v2", "Two-bedroom flat. No Section 8.", ComplianceEval.Violation, "source_of_income");
        var judge = new ScriptedJudge(_ => Answer());

        var result = await ComplianceEval.CheckAsync(Invoker(judge), sample, default);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(FindingSource.Lexicon, finding.Source);
    }

    /// <summary>
    /// An answer that fails validation twice leaves the deterministic layer standing, as in
    /// production, and is counted against schema validity.
    /// </summary>
    [Fact]
    public async Task Check_AnAnswerInvalidTwice_LeavesTheLexiconsFindings_AndIsRecordedAsInvalid()
    {
        var judge = new ScriptedJudge(_ => """{"verdict":"fine"}""");
        var invoker = Invoker(judge);

        var lexiconCaught = await ComplianceEval.CheckAsync(invoker, Sample("v3", "Adults only building.", ComplianceEval.Violation), default);
        var nothingCaught = await ComplianceEval.CheckAsync(invoker, Sample("v4", "Ideal for an observant family of one faith.", ComplianceEval.Violation, "religion"), default);

        Assert.Equal(EvalCallOutcome.InvalidAfterRetry, lexiconCaught.Call.Outcome);
        Assert.True(lexiconCaught.Flagged);
        Assert.All(lexiconCaught.Findings, finding => Assert.Equal(FindingSource.Lexicon, finding.Source));
        Assert.False(nothingCaught.Flagged);

        var measurement = ComplianceEval.Measure([lexiconCaught, nothingCaught]);
        Assert.Equal(0.0, measurement.Values[MetricCatalog.SchemaValidityRate]);
        Assert.Equal(0.5, measurement.Values[MetricCatalog.FairHousingRecall]);
    }

    // ───────────────────────────── Parity with production ─────────────────────────────

    /// <summary>The kind the suite judges samples as is the host's public-remarks label.</summary>
    [Fact]
    public void ContentKind_IsTheHostsPublicRemarksLabel() =>
        Assert.Equal(ComplianceEval.ContentKind, ContentRepresentation.Label(ContentKind.PublicRemarks));

    public static TheoryData<string, string> ParityCases() => new()
    {
        // Judge and lexicon overlap: the lexicon's finding stands.
        { "Perfect for young families, close to the park.", Answer(("Perfect for young families", "familial_status", "blocking")) },
        // Judge-only finding beside a lexicon finding, and one phrase the text does not contain.
        { "Walking distance to church. Ideal for a churchgoing household.", Answer(("Ideal for a churchgoing household", "religion", "warning"), ("Christian buyers", "religion", "blocking")) },
        // A repeated phrase is placed at each occurrence once; the class is normalised.
        { "Great for newlyweds. Truly great for newlyweds.", Answer(("great for newlyweds", "Familial Status", "warning"), ("great for newlyweds", "familial status", "warning")) },
        // Nothing found by either layer.
        { "Three bedrooms, two baths, and a two-car garage.", Answer() },
        // Overlapping judge phrases: the earlier, longer one is kept.
        { "A home for a mature professional couple.", Answer(("mature professional couple", "age", "warning"), ("professional couple", "marital_status", "warning")) },
    };

    /// <summary>
    /// Production's text check is <c>FindingMerger.Merge(lexicon, judge findings)</c> with the
    /// judge called through <see cref="JudgeChecker.JudgeAsync"/>. The suite must send the same
    /// request and arrive at the same findings from the same answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityCases))]
    public async Task Check_SendsProductionsRequest_AndReachesProductionsFindings(string text, string answer)
    {
        var production = new ScriptedAiService(_ => answer);
        var judged = await production.JudgeAsync(Guid.NewGuid(), Guid.NewGuid(), ContentRepresentation.Label(ContentKind.PublicRemarks), text, 1000, default);
        var expected = FindingMerger.Merge(LexiconMatcher.FairHousing.Match(text), judged.Findings);

        var judge = new ScriptedJudge(_ => answer);
        var result = await ComplianceEval.CheckAsync(Invoker(judge), Sample("p", text, ComplianceEval.Violation), default);

        Assert.Equal(Assert.Single(production.Requests).Input, Assert.Single(judge.Inputs));
        Assert.Equal(expected, result.Findings);
        Assert.Equal(expected.Count > 0, result.Flagged);
    }

    // ───────────────────────────── The suite end to end ─────────────────────────────

    [Fact]
    public async Task Run_MeasuresTheSet_AndAnUnreviewedSetIsIncomplete()
    {
        var set = WriteSet(
            Item("v-1", "Perfect for young families.", ComplianceEval.Violation),
            Item("v-2", "Owner prefers tenants from one church.", ComplianceEval.Violation, "religion"),
            Item("c-1", "Sunny family room.", ComplianceEval.Compliant),
            Item("c-2", "Master bedroom with a view.", ComplianceEval.Compliant, "sex"));

        // Misses v-2, wrongly flags c-2.
        var judge = new ScriptedJudge(input => TextOf(input) switch
        {
            "Master bedroom with a view." => Answer(("Master bedroom", "sex", "warning")),
            _ => Answer(),
        });

        using var report = new StringWriter();
        var suite = new ComplianceEval(() => set, report);

        var run = await suite.RunAsync(Invoker(judge), default);

        Assert.Equal(4, judge.Inputs.Count);
        Assert.Equal(0.5, run.Measurements[MetricCatalog.FairHousingRecall]);
        Assert.Equal(0.5, run.Measurements[MetricCatalog.FairHousingFalsePositiveRate]);
        Assert.Equal(1.0, run.Measurements[MetricCatalog.SchemaValidityRate]);
        Assert.Same(set, Assert.Single(run.EvalSets));
        Assert.Contains("Missed violations: v-2", report.ToString(), StringComparison.Ordinal);
        Assert.Contains("Flagged compliant samples: c-2", report.ToString(), StringComparison.Ordinal);

        var catalog = new MetricCatalog(EvalBudget.Default);
        var outcome = EvalVerdict.Evaluate(catalog.For(AiTaskKey.ComplianceJudge), run, AiTaskKey.ComplianceJudge);

        // Recall missed its bar, but labels nobody has checked cannot establish a failure either.
        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Contains(outcome.Reasons, reason => reason.Contains("has not been reviewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RefusesAModelForAnotherTask()
    {
        var target = JudgeTarget with { TaskKey = AiTaskKey.ListingContent };
        var invoker = new EvalModelInvoker(target, RepositoryPrompts.Value, new ScriptedJudge(_ => Answer()), Normalizer);
        var suite = new ComplianceEval(() => throw new InvalidOperationException("must not load"), TextWriter.Null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => suite.RunAsync(invoker, default));
        Assert.Contains(AiTaskKey.ComplianceJudge, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AProviderOutage_MeasuresNothing()
    {
        var set = WriteSet(
            Item("v-1", "Perfect for young families.", ComplianceEval.Violation),
            Item("c-1", "Sunny family room.", ComplianceEval.Compliant));

        var judge = new ScriptedJudge(_ => Answer()) { Throw = new HttpRequestException("503 Service Unavailable") };

        var run = await new ComplianceEval(() => set, TextWriter.Null).RunAsync(Invoker(judge), default);

        Assert.Empty(run.Measurements);
        Assert.NotEmpty(run.Unmeasured);
    }

    // ───────────────────────────── Sample validation ─────────────────────────────

    private static JsonObject[] MalformedItems(string malformation) => malformation switch
    {
        "duplicate id" => [Item("x", "One.", ComplianceEval.Violation), Item("x", "Two.", ComplianceEval.Compliant)],
        "unknown label" => [Item("x", "One.", "unsure")],
        "violation without class" => [Item("x", "One.", ComplianceEval.Violation, protectedClass: null)],
        "violation with unknown class" => [Item("x", "One.", ComplianceEval.Violation, protectedClass: "height")],
        "compliant with unknown class" => [Item("x", "One.", ComplianceEval.Compliant, protectedClass: "height")],
        "violation with compliant category" => [Item("x", "One.", ComplianceEval.Violation, category: "near_miss")],
        "compliant with violation category" => [Item("x", "One.", ComplianceEval.Compliant, category: "explicit")],
        "no citation" => [Item("x", "One.", ComplianceEval.Violation, citation: null)],
        "blank citation" => [Item("x", "One.", ComplianceEval.Violation, citation: " ")],
        _ => throw new ArgumentOutOfRangeException(nameof(malformation), malformation, null),
    };

    [Theory]
    [InlineData("duplicate id", "used more than once")]
    [InlineData("unknown label", "label must be")]
    [InlineData("violation without class", "must name its protected class")]
    [InlineData("violation with unknown class", "must name its protected class")]
    [InlineData("compliant with unknown class", "not a known class")]
    [InlineData("violation with compliant category", "category must be one of")]
    [InlineData("compliant with violation category", "category must be one of")]
    [InlineData("no citation", "citation is missing")]
    [InlineData("blank citation", "citation is missing")]
    public void ReadSamples_RejectsAMalformedSample(string malformation, string expected)
    {
        var set = WriteSet(MalformedItems(malformation));

        var exception = Assert.Throws<InvalidDataException>(() => ComplianceEval.ReadSamples(set));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSamples_RejectsAnUnknownItemProperty()
    {
        var item = Item("x", "One.", ComplianceEval.Violation);
        item["severity"] = "blocking";

        Assert.Throws<InvalidDataException>(() => ComplianceEval.ReadSamples(WriteSet(item)));
    }

    // ───────────────────────────── The shipped set ─────────────────────────────

    private static readonly string[] FederalClasses =
        ["race", "color", "religion", "sex", "disability", "familial_status", "national_origin"];

    /// <summary>The authorities a label may rest on.</summary>
    private static readonly string[] Authorities =
    [
        "42 U.S.C. 3604(c)",
        "24 CFR 100.75",
        "Guidance Regarding Advertisements Under Section 804(c) of the Fair Housing Act",
        "Cal. Gov. Code 12955",
        "Cal. Civ. Code 51.2",
        "42 U.S.C. 3607(b)",
    ];

    private static EvalSet Shipped() => EvalSet.LoadShipped(AiTaskKey.ComplianceJudge, ComplianceEval.SetFileName);

    [Fact]
    public void ShippedSet_LoadsAndEverySamplePassesValidation()
    {
        var set = Shipped();

        Assert.Equal(AiTaskKey.ComplianceJudge, set.TaskKey);
        Assert.Equal(set.Items.Count, ComplianceEval.ReadSamples(set).Count);
    }

    /// <summary>Drafted by an AI assistant: it must stay unreviewed until a person records a review.</summary>
    [Fact]
    public void ShippedSet_IsUnreviewed()
    {
        var set = Shipped();

        Assert.False(set.IsReviewed);
        Assert.Null(set.Review.Reviewer);
        Assert.Null(set.Review.ReviewedAt);
    }

    /// <summary>
    /// Enough of each label that a single sample moves a rate by under one percentage point, so
    /// 95% recall and a 10% false-positive rate are resolved rather than rounded.
    /// </summary>
    [Fact]
    public void ShippedSet_HasAtLeast120OfEachLabel()
    {
        var samples = ComplianceEval.ReadSamples(Shipped());

        Assert.True(samples.Count(sample => sample.Label == ComplianceEval.Violation) >= 120);
        Assert.True(samples.Count(sample => sample.Label == ComplianceEval.Compliant) >= 120);
        Assert.All(samples, sample => Assert.Contains(sample.Label, new[] { ComplianceEval.Violation, ComplianceEval.Compliant }));
    }

    [Fact]
    public void ShippedSet_IdsAndTextsAreUnique()
    {
        var samples = ComplianceEval.ReadSamples(Shipped());

        Assert.Equal(samples.Count, samples.Select(sample => sample.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(samples.Count, samples.Select(sample => sample.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ShippedSet_EverySampleCitesAnAuthority()
    {
        Assert.All(ComplianceEval.ReadSamples(Shipped()), sample =>
            Assert.Contains(Authorities, authority => sample.Citation.Contains(authority, StringComparison.Ordinal)));
    }

    /// <summary>FR-017's federal classes and every configured California class have violations to catch.</summary>
    [Fact]
    public void ShippedSet_CoversEveryProtectedClass()
    {
        var covered = ComplianceEval.ReadSamples(Shipped())
            .Where(sample => sample.Label == ComplianceEval.Violation)
            .Select(sample => sample.ProtectedClass!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Superset(FederalClasses.ToHashSet(StringComparer.Ordinal), covered);
        Assert.Equal(ComplianceEval.ProtectedClasses.OrderBy(name => name, StringComparer.Ordinal), covered.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ShippedSet_MixesViolationStyles_AndIncludesNearMissControls()
    {
        var samples = ComplianceEval.ReadSamples(Shipped());

        Assert.Equal(
            ComplianceEval.ViolationCategories.OrderBy(name => name, StringComparer.Ordinal),
            samples.Where(sample => sample.Label == ComplianceEval.Violation).Select(sample => sample.Category).Distinct().OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            ComplianceEval.CompliantCategories.OrderBy(name => name, StringComparer.Ordinal),
            samples.Where(sample => sample.Label == ComplianceEval.Compliant).Select(sample => sample.Category).Distinct().OrderBy(name => name, StringComparer.Ordinal));
    }
}
