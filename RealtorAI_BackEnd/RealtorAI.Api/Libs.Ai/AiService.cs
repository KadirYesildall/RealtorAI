using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Libs.Ai;

/// <summary>One AI request, expressed in terms of a task rather than a model.</summary>
/// <param name="TaskKey">Which capability is wanted — see <see cref="AiTaskKey"/>.</param>
/// <param name="ListingId">The listing this is for, for budget accounting and the audit trail.</param>
/// <param name="ActorId">
/// The agent whose action caused the call. Recorded on the audit event: an AI call is always
/// attributable to a human decision, never to the system alone.
/// </param>
/// <param name="Input">
/// The task-specific input appended to the prompt. Must already be free of seller PII and
/// agent credentials, and any image must be downscaled with EXIF stripped (VIII.4.c).
/// </param>
/// <param name="ImageCount">Images included, counted against the per-listing image budget.</param>
/// <param name="EstimatedTokens">
/// Pre-dispatch estimate for the budget check. Required rather than optional: a call with no
/// estimate could not be checked before dispatch, which is the only point at which the check
/// prevents rather than records an overspend.
/// </param>
/// <param name="Images">
/// Images sent with the input, for a vision task. Each must be a normalized copy — downscaled,
/// EXIF and GPS stripped (VIII.4.c); an original never leaves the write-once bucket. When
/// present, their count must equal <paramref name="ImageCount"/>, so the budget is charged for
/// exactly what is sent.
/// </param>
public sealed record AiRequest(
    string TaskKey,
    Guid ListingId,
    Guid ActorId,
    string Input,
    int ImageCount,
    int EstimatedTokens,
    IReadOnlyList<AiImage>? Images = null);

/// <summary>One image sent to a vision model.</summary>
/// <param name="Content">The normalized copy's bytes.</param>
/// <param name="MediaType">Its media type, e.g. <c>image/jpeg</c>.</param>
public sealed record AiImage(ReadOnlyMemory<byte> Content, string MediaType);

/// <summary>A validated AI response, with everything needed to audit it.</summary>
/// <param name="Document">The response, already validated against its declared schema.</param>
/// <param name="ModelId">The model that actually served it, including after failover.</param>
/// <param name="PromptVersion">Prompt version used.</param>
/// <param name="PromptHash">Hash of that prompt's text at call time.</param>
/// <param name="FromCache">
/// True when served from the input-hash cache. Recorded so usage figures can distinguish work
/// actually done from work avoided.
/// </param>
public sealed record AiResult(
    JsonNode Document,
    string ModelId,
    int PromptVersion,
    string PromptHash,
    bool FromCache);

/// <summary>
/// Creates a chat client for a resolved registry entry. Implemented in the host, which owns
/// <see cref="IHttpClientFactory"/>.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(ModelRegistryEntry entry);
}

/// <summary>
/// T044 — the single route every AI call takes (Principle VIII.1, VIII.2).
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Runs one AI task end to end: resolve the model, check the budget, dispatch, validate,
    /// retry once on a schema failure, fail over on a provider failure, and record usage.
    /// </summary>
    /// <exception cref="ModelUnavailableException">No model is enabled for the task.</exception>
    /// <exception cref="AiBudgetExceededException">The call would exceed the listing's budget.</exception>
    /// <exception cref="StructuredOutputException">The response failed validation twice.</exception>
    Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The AI service.
///
/// <para>
/// <b>Everything model-related happens here and only here.</b> Principle VIII.1 requires that
/// no vendor name, model id or endpoint appears outside the registry, so callers name a task
/// and this resolves the rest. That is what makes swapping a model a gated configuration change
/// rather than a code change — and what makes the prohibition on generative imagery
/// enforceable, since there is exactly one place where a model is ever invoked.
/// </para>
///
/// <para>The order of operations is deliberate:</para>
/// <list type="number">
///   <item><description>
///     <b>Prompt first.</b> Resolving the prompt and its schema is local and cheap, and its
///     hash is part of the cache key — so a cache hit can be served without touching the
///     registry or the budget.
///   </description></item>
///   <item><description>
///     <b>Registry next.</b> Needed for the model id, which is also part of the cache key: a
///     cached response from a different model would answer a question the current
///     configuration never asked.
///   </description></item>
///   <item><description>
///     <b>Cache before budget.</b> A cache hit costs no tokens, so charging it against the
///     budget — or refusing it because the budget is spent — would be wrong. Hits are still
///     recorded, flagged as hits.
///   </description></item>
///   <item><description>
///     <b>Budget before dispatch.</b> The only point where the check prevents an overspend
///     instead of reporting one (FR-055).
///   </description></item>
///   <item><description>
///     <b>Validate, then record.</b> Usage is recorded from the response's own token counts,
///     including when validation ultimately fails — the tokens were spent either way, and a
///     budget that ignored failed calls could be exceeded indefinitely by a failing model.
///   </description></item>
/// </list>
/// </summary>
public sealed partial class AiService : IAiService
{
    private readonly IModelRegistry registry;
    private readonly IPromptRegistry prompts;
    private readonly IChatClientFactory clientFactory;
    private readonly ICostTracker costTracker;
    private readonly ProviderCircuitBreakers breakers;
    private readonly ILogger<AiService> logger;

    public AiService(
        IModelRegistry registry,
        IPromptRegistry prompts,
        IChatClientFactory clientFactory,
        ICostTracker costTracker,
        ProviderCircuitBreakers breakers,
        ILogger<AiService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(costTracker);
        ArgumentNullException.ThrowIfNull(breakers);
        ArgumentNullException.ThrowIfNull(logger);

        this.registry = registry;
        this.prompts = prompts;
        this.clientFactory = clientFactory;
        this.costTracker = costTracker;
        this.breakers = breakers;
        this.logger = logger;
    }

    public async Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiTaskKey.EnsureKnown(request.TaskKey);

        var prompt = prompts.GetActive(request.TaskKey);
        var chain = await registry.ResolveChainAsync(request.TaskKey, cancellationToken);
        var primary = chain[0];

        var inputHash = costTracker.ComputeInputHash(
            request.TaskKey,
            primary.ModelId,
            prompt.PromptHash,
            request.Input);

        if (costTracker.TryGetCachedResponse(inputHash) is { } cached)
        {
            var validated = StructuredOutputValidator.Validate(cached, prompt.OutputSchema);

            if (validated.IsValid)
            {
                // Recorded with zero tokens and cacheHit = true, so usage reporting shows what
                // the cache saved rather than hiding the call entirely.
                await costTracker.RecordAsync(
                    request.ListingId,
                    request.TaskKey,
                    primary.ModelId,
                    tokensIn: 0,
                    tokensOut: 0,
                    request.ImageCount,
                    cacheHit: true,
                    cancellationToken);

                return new AiResult(
                    validated.Document!,
                    primary.ModelId,
                    prompt.Version,
                    prompt.PromptHash,
                    FromCache: true);
            }

            // A cached response that no longer validates means the schema changed under it.
            // Fall through to a live call rather than failing: the cache is an optimisation and
            // must never be the reason a request cannot be served.
            LogStaleCacheEntry(logger, request.TaskKey);
        }

        await costTracker.EnsureWithinBudgetAsync(
            request.ListingId,
            request.EstimatedTokens,
            request.ImageCount,
            cancellationToken);

        Exception? lastFailure = null;

        foreach (var entry in chain)
        {
            try
            {
                return await DispatchAsync(request, entry, prompt, inputHash, cancellationToken);
            }
            catch (Exception exception) when (IsFailoverWorthy(exception))
            {
                lastFailure = exception;

                // Provider identity is logged, never the prompt or the input: those can carry
                // property details that do not belong in logs.
                LogProviderFailure(logger, entry.Provider, request.TaskKey, exception);
            }
        }

        throw new ModelUnavailableException(
            request.TaskKey,
            $"all {chain.Count} registry entries in the fallback chain failed. The listing "
            + "remains completable by hand (SC-010).")
        {
            // Preserved so the operator sees the provider's actual error rather than only that
            // everything failed.
            Data = { ["lastFailure"] = lastFailure?.Message },
        };
    }

    private async Task<AiResult> DispatchAsync(
        AiRequest request,
        ModelRegistryEntry entry,
        PromptDefinition prompt,
        string inputHash,
        CancellationToken cancellationToken)
    {
        using var client = clientFactory.Create(entry);

        var breaker = breakers.Get(entry.Provider);

        var usageIn = 0;
        var usageOut = 0;
        string? lastRawResponse = null;

        try
        {
            var document = await StructuredOutputValidator.ValidateWithSingleRetryAsync(
                request.TaskKey,
                prompt.OutputSchema,
                async (retryFeedback, token) =>
                {
                    var messages = BuildMessages(prompt, request.Input, retryFeedback);

                    var response = await breaker.ExecuteAsync(
                        async innerToken => await client.GetResponseAsync(
                            messages,
                            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                            innerToken),
                        token);

                    // Accumulated across the retry, because both attempts cost tokens.
                    usageIn += (int)(response.Usage?.InputTokenCount ?? 0);
                    usageOut += (int)(response.Usage?.OutputTokenCount ?? 0);

                    lastRawResponse = response.Text;

                    return response.Text;
                },
                cancellationToken);

            costTracker.CacheResponse(inputHash, lastRawResponse ?? string.Empty);

            await RecordUsageAsync(request, entry, usageIn, usageOut, cancellationToken);

            return new AiResult(document, entry.ModelId, prompt.Version, prompt.PromptHash, false);
        }
        catch (StructuredOutputException)
        {
            // The tokens were spent even though the output was unusable. Recording them is what
            // stops a model that always fails validation from consuming an unbounded budget.
            await RecordUsageAsync(request, entry, usageIn, usageOut, cancellationToken);
            throw;
        }
    }

    private Task RecordUsageAsync(
        AiRequest request,
        ModelRegistryEntry entry,
        int tokensIn,
        int tokensOut,
        CancellationToken cancellationToken) =>
        costTracker.RecordAsync(
            request.ListingId,
            request.TaskKey,
            entry.ModelId,
            tokensIn,
            tokensOut,
            request.ImageCount,
            cacheHit: false,
            cancellationToken);

    /// <summary>
    /// The prompt becomes the system message and the input the user message, so the
    /// instructions and the data are never concatenated into one string. That separation is
    /// what keeps agent-supplied notes from being read as instructions.
    /// </summary>
    private static List<ChatMessage> BuildMessages(
        PromptDefinition prompt,
        string input,
        string? retryFeedback)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompt.Text),
            new(ChatRole.User, input),
        };

        if (retryFeedback is not null)
        {
            messages.Add(new ChatMessage(ChatRole.User, retryFeedback));
        }

        return messages;
    }

    /// <summary>
    /// Which failures should move on to the next entry in the fallback chain.
    ///
    /// <para>
    /// Transport and availability failures should: a different provider may well succeed. A
    /// schema validation failure should <b>not</b> — the response arrived, the model simply
    /// could not produce the required shape, and FR-015 caps that at one retry. Failing over
    /// would multiply the retry budget by the chain length and spend the listing's tokens
    /// discovering the same thing repeatedly.
    /// </para>
    /// <para>
    /// A budget failure must not fail over either: the ceiling is per listing, not per
    /// provider.
    /// </para>
    /// </summary>
    private static bool IsFailoverWorthy(Exception exception) =>
        exception is HttpRequestException
            or BrokenCircuitException
            or TimeoutException
            or TaskCanceledException;

    // Source-generated logging. The analyzer requires this over the LogWarning extension
    // methods: those box every argument and allocate a params array on each call, including
    // when the level is disabled. These delegates are built once.
    //
    // Note what is NOT logged: the prompt, the model input and the response. Any of them can
    // carry property details or agent notes, and the data-handling boundary in VIII.4.c keeps
    // that out of telemetry. Provider, task and model id are enough to diagnose a failure.

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Discarding a cached AI response for task {TaskKey}: it no longer validates "
            + "against the current schema, so a live call will be made instead.")]
    private static partial void LogStaleCacheEntry(ILogger logger, string taskKey);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "AI provider {Provider} failed for task {TaskKey}; falling back to the next "
            + "registry entry if one remains.")]
    private static partial void LogProviderFailure(
        ILogger logger,
        string provider,
        string taskKey,
        Exception exception);
}
