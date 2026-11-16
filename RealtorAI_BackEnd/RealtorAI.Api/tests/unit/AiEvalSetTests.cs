using System.Text.Json;
using System.Text.Json.Nodes;
using AiEval;

namespace Tests.Unit;

/// <summary>
/// The labelled evaluation-set manifest shared by T156 and T157, and the gate that keeps an
/// unreviewed set from producing a recorded verdict.
///
/// <para>
/// The property under test: labels nobody but their author has checked can never become a pass
/// or a failure in the registry. An unreviewed set is still runnable — that is how a set is
/// developed — so its measurements are shown, but the verdict is <see cref="Verdict.Incomplete"/>
/// with the reason spelled out, and the writer refuses an incomplete verdict.
/// </para>
/// </summary>
public sealed class AiEvalSetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aieval-sets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonArray Items() => new(
        new JsonObject { ["text"] = "Perfect for a young couple.", ["violation"] = true },
        new JsonObject { ["text"] = "Three bedrooms and a yard.", ["violation"] = false });

    /// <summary>
    /// The items hash as a reviewer records it. Computed by the loader's own canonicaliser: the
    /// tests below pin what that canonical form ignores and what it does not.
    /// </summary>
    private static string ItemsHashOf(JsonArray items) =>
        EvalSet.ComputeItemsHash(JsonSerializer.SerializeToElement(items).EnumerateArray());

    private static JsonObject Manifest(bool reviewed = false, string task = "compliance.judge") => new()
    {
        ["format"] = 1,
        ["id"] = "fair-housing-samples",
        ["task"] = task,
        ["version"] = 2,
        ["description"] = "Listing sentences labelled for Fair Housing violations.",
        ["provenance"] = new JsonObject
        {
            ["source"] = "Written for this set by the compliance team.",
            ["rights"] = "Internal work product.",
            ["labelledBy"] = "Compliance team",
            ["labellingMethod"] = "Two labellers, disagreements adjudicated.",
            ["createdOn"] = "2026-11-01",
        },
        ["review"] = reviewed
            ? new JsonObject
            {
                ["reviewed"] = true,
                ["reviewer"] = "A. Reviewer",
                ["reviewedAt"] = "2026-11-10T09:00:00Z",
                ["itemsSha256"] = ItemsHashOf(Items()),
                ["notes"] = "Every label checked against the guide.",
            }
            : new JsonObject
            {
                ["reviewed"] = false,
                ["reviewer"] = null,
                ["reviewedAt"] = null,
                ["notes"] = null,
            },
        ["items"] = Items(),
    };

    private string Write(JsonObject manifest, string? directory = null)
    {
        var folder = Path.Combine(root, directory ?? manifest["task"]!.GetValue<string>());
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "samples.v2.json");
        File.WriteAllText(path, manifest.ToJsonString());

        return path;
    }

    private EvalSet Load(JsonObject manifest) => EvalSet.Load(Write(manifest));

    private sealed record Sample(string Text, bool Violation);

    private static readonly EvalMetric Recall = new("recall", "recall", "TEST", Comparison.AtLeast, 0.95);

    // ───────────────────────────── Manifest ─────────────────────────────

    [Fact]
    public void Load_ReadsTheManifest()
    {
        var set = Load(Manifest(reviewed: true));

        Assert.Equal("fair-housing-samples v2", set.Name);
        Assert.Equal("compliance.judge", set.TaskKey);
        Assert.True(set.IsReviewed);
        Assert.Equal("A. Reviewer", set.Review.Reviewer);
        Assert.Equal(new DateOnly(2026, 11, 1), set.Provenance.CreatedOn);
        Assert.Equal(64, set.ContentHash.Length);

        var samples = set.ItemsAs<Sample>();
        Assert.Equal(2, samples.Count);
        Assert.True(samples[0].Violation);
    }

    [Fact]
    public void Load_AnUnreviewedSet_IsLoadable_AndSaysSo()
    {
        Assert.False(Load(Manifest()).IsReviewed);
    }

    /// <summary>A missing review block must not read as anything — not even as "unreviewed".</summary>
    [Fact]
    public void Load_RejectsAMissingReviewBlock()
    {
        var manifest = Manifest();
        manifest.Remove("review");

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("reviewedAt")]
    [InlineData("itemsSha256")]
    public void Load_RejectsAReviewWithNoReviewerDateOrItemsHash(string field)
    {
        var manifest = Manifest(reviewed: true);
        manifest["review"]![field] = null;

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    [Fact]
    public void Load_RejectsAReviewDatedInTheFuture()
    {
        var manifest = Manifest(reviewed: true);
        manifest["review"]!["reviewedAt"] = DateTimeOffset.UtcNow.AddDays(2).ToString("O");

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("not-a-hash")]
    public void Load_RejectsAMalformedItemsHash(string value)
    {
        var manifest = Manifest(reviewed: true);
        manifest["review"]!["itemsSha256"] = value + new string('0', 64 - value.Length);

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    /// <summary>
    /// The review is bound to the labels it saw. One relabelled item under an unchanged review
    /// block must not keep the set reviewed — otherwise any edit after sign-off could reach a
    /// recorded verdict.
    /// </summary>
    [Fact]
    public void Load_AReviewedSetWhoseItemsChanged_IsNotReviewed()
    {
        var manifest = Manifest(reviewed: true);
        manifest["items"]![0]!["violation"] = false;

        var set = Load(manifest);

        Assert.True(set.Review.Reviewed);
        Assert.False(set.IsReviewed);
        Assert.Contains(set.ItemsHash, set.ReviewMismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_AReviewedSetWhoseItemsChanged_IsIncomplete_AndSaysWhy()
    {
        var manifest = Manifest(reviewed: true);
        manifest["items"]!.AsArray().Add(new JsonObject { ["text"] = "Walk to the church.", ["violation"] = false });

        var run = new SuiteRun([Load(manifest)], new Dictionary<string, double> { ["recall"] = 0.99 });

        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Contains(outcome.Reasons, reason => reason.Contains("changed after review", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reformatting is not relabelling: indentation, CRLF line endings and the spelling of a
    /// string escape leave the items hash, and so the review, intact.
    /// </summary>
    [Fact]
    public void ItemsHash_IgnoresFormatting_ButNotContent()
    {
        var compact = Load(Manifest(reviewed: true));

        var path = Write(Manifest(reviewed: true));
        var reformatted = JsonNode.Parse(File.ReadAllText(path))!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("young couple", "young\\u0020couple", StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
        File.WriteAllText(path, reformatted);

        // Preconditions: the file really is reformatted, not just re-serialised the same way.
        Assert.Contains("young\\u0020couple", reformatted, StringComparison.Ordinal);
        Assert.Contains("\r\n  ", reformatted, StringComparison.Ordinal);

        var loaded = EvalSet.Load(path);

        Assert.Equal(compact.ItemsHash, loaded.ItemsHash);
        Assert.True(loaded.IsReviewed);

        var relabelled = Items();
        relabelled[1]!["violation"] = true;
        Assert.NotEqual(compact.ItemsHash, ItemsHashOf(relabelled));
    }

    /// <summary>A misspelt key fails loudly instead of silently reading as absent.</summary>
    [Fact]
    public void Load_RejectsAnUnknownProperty()
    {
        var manifest = Manifest(reviewed: true);
        manifest["review"]!["reviwed"] = true;

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("rights")]
    [InlineData("labelledBy")]
    [InlineData("labellingMethod")]
    [InlineData("createdOn")]
    public void Load_RequiresProvenance(string field)
    {
        var manifest = Manifest();
        manifest["provenance"]!.AsObject().Remove(field);

        Assert.Throws<InvalidDataException>(() => Load(manifest));
    }

    [Fact]
    public void Load_RejectsASetFiledUnderAnotherTask()
    {
        Assert.Throws<InvalidDataException>(() => EvalSet.Load(Write(Manifest(), directory: "photo.analysis")));
    }

    [Fact]
    public void Load_RejectsAnUnknownTask_AnEmptySet_AndAnotherFormat()
    {
        var unknown = Manifest(task: "photo.staging");
        Assert.Throws<InvalidDataException>(() => Load(unknown));

        var empty = Manifest();
        empty["items"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => Load(empty));

        var format = Manifest();
        format["format"] = 2;
        Assert.Throws<InvalidDataException>(() => Load(format));
    }

    [Fact]
    public void ItemsAs_RejectsAnUndeclaredItemProperty()
    {
        var manifest = Manifest();
        manifest["items"]![0]!["violaton"] = true;

        Assert.Throws<InvalidDataException>(() => Load(manifest).ItemsAs<Sample>());
    }

    [Fact]
    public void ResolveFile_StaysInsideTheSetsDirectory()
    {
        var set = Load(Manifest());

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(set.ManifestPath)!, "images", "kitchen.jpg"),
            set.ResolveFile("images/kitchen.jpg"));
        Assert.Throws<InvalidDataException>(() => set.ResolveFile("../photo.analysis/other.jpg"));
        Assert.Throws<InvalidDataException>(() => set.ResolveFile(Path.Combine(root, "elsewhere.jpg")));
    }

    // ───────────────────────────── The review gate ─────────────────────────────

    [Fact]
    public void SuiteRun_RequiresTheSetsItMeasuredOn()
    {
        Assert.Throws<ArgumentException>(() => new SuiteRun([], new Dictionary<string, double>()));
    }

    /// <summary>Measurements that would pass on reviewed labels…</summary>
    [Fact]
    public void Gate_AReviewedSet_LetsAPassThrough()
    {
        var run = new SuiteRun([Load(Manifest(reviewed: true))], new Dictionary<string, double> { ["recall"] = 0.97 });

        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");

        Assert.Equal(Verdict.Passed, outcome.Verdict);
        Assert.Empty(outcome.Reasons);
    }

    /// <summary>…are Incomplete on unreviewed ones, with the reason named, and still shown.</summary>
    [Fact]
    public void Gate_AnUnreviewedSet_CannotPass_AndSaysWhy()
    {
        var run = new SuiteRun([Load(Manifest())], new Dictionary<string, double> { ["recall"] = 0.97 });

        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Contains(
            "evaluation set fair-housing-samples v2 has not been reviewed",
            Assert.Single(outcome.Reasons),
            StringComparison.Ordinal);
        Assert.Equal(0.97, Assert.Single(outcome.Results).Value);
    }

    /// <summary>
    /// Unchecked labels cannot show a model is worse than the bar either, so a measured miss on
    /// an unreviewed set is not recorded as a failure.
    /// </summary>
    [Fact]
    public void Gate_AnUnreviewedSet_CannotFail_Either()
    {
        var run = new SuiteRun([Load(Manifest())], new Dictionary<string, double> { ["recall"] = 0.5 });

        Assert.Equal(Verdict.Incomplete, EvalVerdict.Evaluate([Recall], run, "compliance.judge").Verdict);
    }

    /// <summary>One unreviewed set among several is enough to withhold the verdict.</summary>
    [Fact]
    public void Gate_OneUnreviewedSetAmongReviewedOnes_StillBlocks()
    {
        var reviewed = Load(Manifest(reviewed: true));
        var unreviewedManifest = Manifest();
        unreviewedManifest["id"] = "compliant-samples";
        var unreviewed = Load(unreviewedManifest);

        var run = new SuiteRun([reviewed, unreviewed], new Dictionary<string, double> { ["recall"] = 0.99 });

        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Contains("compliant-samples v2", Assert.Single(outcome.Reasons), StringComparison.Ordinal);
    }

    /// <summary>--write records nothing for it: the writer refuses an incomplete verdict outright.</summary>
    [Fact]
    public async Task Gate_AnUnreviewedOutcome_IsRefusedByTheWriter()
    {
        var run = new SuiteRun([Load(Manifest())], new Dictionary<string, double> { ["recall"] = 0.97 });
        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");
        var target = new RegistryTarget(Guid.NewGuid(), "compliance.judge", "p", "m", null, "{}", false);

        // No database is reachable at this connection string; the refusal comes before any connection.
        var writer = new RegistryResultWriter("Host=127.0.0.1;Port=1;Timeout=1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(
            target, outcome.Verdict, outcome.Results, Guid.NewGuid(), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public void Gate_ASetOfAnotherTask_IsAWiringError()
    {
        var run = new SuiteRun([Load(Manifest(reviewed: true))], new Dictionary<string, double>());

        Assert.Throws<ArgumentException>(() => EvalVerdict.Evaluate([Recall], run, "photo.analysis"));
    }

    [Fact]
    public void Gate_NoSuite_IsIncomplete_WithTheReason()
    {
        var outcome = EvalVerdict.Evaluate([Recall], run: null, "compliance.judge");

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Contains("No evaluation suite exists", Assert.Single(outcome.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_ASuitesUnmeasuredReasons_AreReported()
    {
        var run = new SuiteRun(
            [Load(Manifest(reviewed: true))],
            new Dictionary<string, double>(),
            ["recall: the provider was down."]);

        var outcome = EvalVerdict.Evaluate([Recall], run, "compliance.judge");

        Assert.Equal(Verdict.Incomplete, outcome.Verdict);
        Assert.Equal("recall: the provider was down.", Assert.Single(outcome.Reasons));
    }
}
