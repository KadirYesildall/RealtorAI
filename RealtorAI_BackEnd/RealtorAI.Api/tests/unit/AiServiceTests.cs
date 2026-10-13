using System.Text.Json.Nodes;
using Libs.Ai;

namespace RealtorAI.Tests.Unit;

/// <summary>
/// T052 — schema-retry and fallback behaviour (FR-015, Principle VIII).
///
/// <para>
/// These test the rules that are easy to get subtly wrong and impossible to notice in
/// production: that a retry happens <i>once</i> and carries the validation errors, that a
/// schema failure does not fail over, that a budget failure does not fail over either, and that
/// a disabled or unlicensed registry entry is never dispatched to.
/// </para>
/// </summary>
public sealed class AiServiceTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": ["roomType", "confidence"],
          "properties": {
            "roomType": { "type": "string" },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
          }
        }
        """;

    private const string ValidResponse = """{"roomType":"kitchen","confidence":0.92}""";

    /// <summary>
    /// Copied from the <c>registry_task_key_known</c> check constraint in
    /// 0008_ai_platform.sql, sorted. Duplicated on purpose: if the constraint is narrowed or
    /// the code adds a key, the two disagree and this test says so.
    /// </summary>
    private static readonly string[] ConstraintTaskKeys =
        ["compliance.judge", "listing.content", "photo.analysis", "photo.embedding"];

    // ═════════════════════ Schema validation ═════════════════════

    [Fact]
    public void Validate_AcceptsAConformingResponse()
    {
        var outcome = StructuredOutputValidator.Validate(ValidResponse, Schema);

        Assert.True(outcome.IsValid, string.Join("; ", outcome.Errors));
        Assert.Equal("kitchen", outcome.Document!["roomType"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"roomType":"kitchen"}""", "missing required property")]
    [InlineData("""{"confidence":0.5}""", "missing required property")]
    [InlineData("""{"roomType":"kitchen","confidence":2}""", "out of range")]
    [InlineData("""{"roomType":123,"confidence":0.5}""", "wrong type")]
    [InlineData("not json at all", "unparseable")]
    [InlineData("", "empty")]
    public void Validate_RejectsNonConformingResponses(string response, string why)
    {
        var outcome = StructuredOutputValidator.Validate(response, Schema);

        Assert.False(outcome.IsValid, $"Expected rejection ({why}) but the response validated.");
        Assert.NotEmpty(outcome.Errors);
    }

    /// <summary>
    /// Models wrap JSON in prose or a fenced code block despite instructions. Rejecting an
    /// otherwise-correct response over a markdown fence would burn the single retry FR-015
    /// allows on formatting rather than substance.
    /// </summary>
    [Theory]
    [InlineData("```json\n{\"roomType\":\"kitchen\",\"confidence\":0.9}\n```")]
    [InlineData("Here is the analysis:\n{\"roomType\":\"kitchen\",\"confidence\":0.9}")]
    [InlineData("{\"roomType\":\"kitchen\",\"confidence\":0.9}\nLet me know if you need more.")]
    public void Validate_ToleratesJsonWrappedInProseOrAFence(string response)
    {
        Assert.True(StructuredOutputValidator.Validate(response, Schema).IsValid);
    }

    /// <summary>
    /// Extraction must never repair JSON — only trim around it. A genuinely malformed body
    /// still has to fail, or validation would be accepting guesses about model output.
    /// </summary>
    [Fact]
    public void Validate_DoesNotRepairMalformedJson()
    {
        Assert.False(
            StructuredOutputValidator.Validate("{\"roomType\":\"kitchen\",", Schema).IsValid);
    }

    [Fact]
    public void RetryFeedback_NamesTheActualProblems()
    {
        var outcome = StructuredOutputValidator.Validate("""{"roomType":"kitchen"}""", Schema);

        var feedback = outcome.ToRetryFeedback();

        // The feedback has to be specific; "try again" produces the same answer and wastes the
        // one retry.
        Assert.Contains("schema", feedback, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(outcome.Errors);
        Assert.Contains(outcome.Errors[0], feedback, StringComparison.Ordinal);
    }

    // ═════════════════════ The single retry ═════════════════════

    [Fact]
    public async Task ValidateWithSingleRetry_DoesNotRetryWhenTheFirstAttemptIsValid()
    {
        var attempts = 0;

        var document = await StructuredOutputValidator.ValidateWithSingleRetryAsync(
            AiTaskKey.PhotoAnalysis,
            Schema,
            (feedback, token) =>
            {
                attempts++;
                Assert.Null(feedback);
                return Task.FromResult(ValidResponse);
            },
            default);

        Assert.Equal(1, attempts);
        Assert.Equal("kitchen", document["roomType"]!.GetValue<string>());
    }

    /// <summary>
    /// The retry must carry the validation errors, not merely re-ask. Re-asking an identical
    /// question tends to produce an identical answer, which would make the retry pure cost.
    /// </summary>
    [Fact]
    public async Task ValidateWithSingleRetry_PassesTheValidationErrorsToTheSecondAttempt()
    {
        var attempts = 0;
        string? feedbackSeen = null;

        var document = await StructuredOutputValidator.ValidateWithSingleRetryAsync(
            AiTaskKey.PhotoAnalysis,
            Schema,
            (feedback, token) =>
            {
                attempts++;
                feedbackSeen = feedback;

                return Task.FromResult(attempts == 1 ? """{"roomType":"kitchen"}""" : ValidResponse);
            },
            default);

        Assert.Equal(2, attempts);
        Assert.NotNull(feedbackSeen);
        Assert.Contains("schema", feedbackSeen, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(document);
    }

    /// <summary>
    /// <b>Exactly one retry.</b> FR-015 specifies this, and the bound is the point: a model
    /// failing twice on the same schema is usually wrong about the task, not the formatting, and
    /// looping would spend the listing's token budget discovering that slowly.
    /// </summary>
    [Fact]
    public async Task ValidateWithSingleRetry_StopsAfterExactlyTwoAttempts()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<StructuredOutputException>(() =>
            StructuredOutputValidator.ValidateWithSingleRetryAsync(
                AiTaskKey.PhotoAnalysis,
                Schema,
                (feedback, token) =>
                {
                    attempts++;
                    return Task.FromResult("""{"roomType":"kitchen"}""");
                },
                default));

        Assert.Equal(2, attempts);
        Assert.Equal(AiTaskKey.PhotoAnalysis, exception.TaskKey);

        // Both attempts' errors are reported, so the failure record shows whether the model
        // produced the same problem twice or a different one.
        Assert.True(exception.Errors.Count >= 2);
    }

    /// <summary>
    /// A broken schema is our deployment problem, not the model's, and retrying cannot help —
    /// so it throws immediately rather than consuming the retry.
    /// </summary>
    [Fact]
    public void Validate_ThrowsWhenOurOwnSchemaIsMalformed()
    {
        var exception = Assert.Throws<StructuredOutputException>(
            () => StructuredOutputValidator.Validate(ValidResponse, "{ this is not json }"));

        Assert.Contains("prompt-registry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════ Registry resolution and the VIII.4 gate ═════════════════════

    private sealed class FakeRegistryStore : IModelRegistryStore
    {
        private readonly Dictionary<Guid, ModelRegistryEntry> entries = [];

        internal void Add(ModelRegistryEntry entry) => entries[entry.Id] = entry;

        public Task<ModelRegistryEntry?> GetEnabledForTaskAsync(
            string taskKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(entries.Values.SingleOrDefault(entry =>
                entry.Enabled && entry.TaskKey == taskKey));

        public Task<ModelRegistryEntry?> GetByIdAsync(Guid registryId, CancellationToken cancellationToken) =>
            Task.FromResult(entries.TryGetValue(registryId, out var found) ? found : null);
    }

    private static ModelRegistryEntry Entry(
        Guid id,
        bool enabled = true,
        string? license = "apache-2.0",
        Guid? fallback = null,
        string taskKey = AiTaskKey.PhotoAnalysis,
        string provider = "test-provider",
        ModelEvalStatus evalStatus = ModelEvalStatus.Passed) =>
        new(id, taskKey, provider, $"test/model-{id:N}", null, "{}", fallback, license,
            evalStatus, enabled);

    [Fact]
    public async Task Resolve_ThrowsModelUnavailableWhenNothingIsEnabled()
    {
        var store = new FakeRegistryStore();
        store.Add(Entry(Guid.NewGuid(), enabled: false, license: null));

        var registry = new ModelRegistry(store);

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => registry.ResolveAsync(AiTaskKey.PhotoAnalysis, default));

        // The message has to explain that this is the normal initial state, or an operator
        // reads it as a bug. Every registry entry ships disabled under VIII.4.
        Assert.Contains("VIII.4", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The database constraint already forbids <c>enabled</c> without a license, so this
    /// simulates the constraint having been dropped or altered by a future migration — the one
    /// scenario in which the check here is the only thing standing between an unlicensed model
    /// and a commercial API call.
    /// </summary>
    [Fact]
    public async Task Resolve_RefusesAnEnabledEntryWithNoRecordedLicense()
    {
        var store = new FakeRegistryStore();
        store.Add(Entry(Guid.NewGuid(), enabled: true, license: null));

        var registry = new ModelRegistry(store);

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => registry.ResolveAsync(AiTaskKey.PhotoAnalysis, default));

        Assert.Contains("license", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveChain_FollowsTheFallbackChain()
    {
        var store = new FakeRegistryStore();

        // Fallbacks are deliberately NOT enabled: registry_one_enabled_per_task permits only
        // one enabled row per task, so this is the only shape the schema actually allows. The
        // original implementation required fallback.Enabled and therefore never followed a
        // chain at all — this test is what found it.
        var third = Entry(Guid.NewGuid(), enabled: false);
        var second = Entry(Guid.NewGuid(), enabled: false, fallback: third.Id);
        var primary = Entry(Guid.NewGuid(), enabled: true, fallback: second.Id);

        store.Add(primary);
        store.Add(second);
        store.Add(third);

        var chain = await new ModelRegistry(store)
            .ResolveChainAsync(AiTaskKey.PhotoAnalysis, default);

        Assert.Equal(3, chain.Count);
    }

    /// <summary>
    /// A fallback with no recorded license must not be reachable by failover. Nothing in the
    /// schema requires a fallback row to be enabled, so this is the only check standing between
    /// an outage and dispatching to an uncleared model.
    /// </summary>
    [Fact]
    public async Task ResolveChain_StopsAtAnUnlicensedFallback()
    {
        var store = new FakeRegistryStore();

        var unlicensed = Entry(Guid.NewGuid(), enabled: false, license: null);
        var primary = Entry(Guid.NewGuid(), enabled: true, fallback: unlicensed.Id);

        store.Add(primary);
        store.Add(unlicensed);

        var chain = await new ModelRegistry(store)
            .ResolveChainAsync(AiTaskKey.PhotoAnalysis, default);

        Assert.Single(chain);
        Assert.Equal(primary.Id, chain[0].Id);
    }

    /// <summary>
    /// Nor may a fallback that ran the evaluation harness and failed it. Having been evaluated
    /// is not the same as having passed.
    /// </summary>
    [Fact]
    public async Task ResolveChain_StopsAtAFallbackThatFailedEvaluation()
    {
        var store = new FakeRegistryStore();

        var failed = Entry(Guid.NewGuid(), enabled: false, evalStatus: ModelEvalStatus.Failed);
        var primary = Entry(Guid.NewGuid(), enabled: true, fallback: failed.Id);

        store.Add(primary);
        store.Add(failed);

        var chain = await new ModelRegistry(store)
            .ResolveChainAsync(AiTaskKey.PhotoAnalysis, default);

        Assert.Single(chain);
    }

    /// <summary>
    /// An entry flagged enabled but still unverified must be refused. The database constraint
    /// forbids the combination, so this covers the constraint having been dropped.
    /// </summary>
    [Fact]
    public async Task Resolve_RefusesAnEnabledEntryThatHasNotPassedEvaluation()
    {
        var store = new FakeRegistryStore();
        store.Add(Entry(Guid.NewGuid(), enabled: true, evalStatus: ModelEvalStatus.Unverified));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => new ModelRegistry(store).ResolveAsync(AiTaskKey.PhotoAnalysis, default));

        Assert.Contains("VIII.4", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fallback serving a different task would answer a different question in the same shape,
    /// which is worse than having no fallback.
    /// </summary>
    [Fact]
    public async Task ResolveChain_StopsAtAFallbackForADifferentTask()
    {
        var store = new FakeRegistryStore();

        var wrongTask = Entry(Guid.NewGuid(), enabled: false, taskKey: AiTaskKey.ComplianceJudge);
        var primary = Entry(Guid.NewGuid(), enabled: true, fallback: wrongTask.Id);

        store.Add(primary);
        store.Add(wrongTask);

        var chain = await new ModelRegistry(store)
            .ResolveChainAsync(AiTaskKey.PhotoAnalysis, default);

        Assert.Single(chain);
    }

    /// <summary>
    /// The database forbids an entry pointing at itself but not a longer loop, so the walk is
    /// bounded. A cycle must not hang the request.
    /// </summary>
    [Fact]
    public async Task ResolveChain_TerminatesOnACycle()
    {
        var store = new FakeRegistryStore();

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        store.Add(Entry(firstId, enabled: true, fallback: secondId));
        store.Add(Entry(secondId, enabled: false, fallback: firstId));

        var chain = await new ModelRegistry(store)
            .ResolveChainAsync(AiTaskKey.PhotoAnalysis, default);

        Assert.Equal(2, chain.Count);
    }

    [Fact]
    public async Task Resolve_RejectsAnUnknownTaskKey()
    {
        var registry = new ModelRegistry(new FakeRegistryStore());

        await Assert.ThrowsAsync<ArgumentException>(
            () => registry.ResolveAsync("photo.colourise", default));
    }

    // ═════════════════════ Task keys ═════════════════════

    /// <summary>
    /// The four task keys must match the <c>registry_task_key_known</c> check constraint in
    /// 0008_ai_platform.sql exactly, or a registry row the code dispatches to cannot be
    /// inserted.
    /// </summary>
    [Fact]
    public void TaskKeys_MatchTheDatabaseConstraint()
    {
        Assert.Equal(
            ConstraintTaskKeys,
            AiTaskKey.All.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }
}
