using System.Diagnostics;
using System.Text.Json.Nodes;
using Libs.Ai;
using Modules.Media;

namespace AiEval;

/// <summary>
/// An image ready to send to a model under evaluation: always a normalized copy.
///
/// <para>
/// VIII.4.c lets only a downscaled, EXIF- and GPS-stripped copy leave the boundary. The private
/// constructor makes that structural for the harness: the only way to obtain an
/// <see cref="EvalImage"/> is <see cref="Normalize"/>, which runs the production
/// <see cref="ImageNormalizer"/> — so no suite can hand a model an original, however its
/// evaluation photos were collected.
/// </para>
/// </summary>
internal sealed class EvalImage
{
    private EvalImage(AiImage image) => Image = image;

    internal AiImage Image { get; }

    /// <summary>Normalizes <paramref name="original"/> exactly as an upload is normalized.</summary>
    /// <exception cref="UnsupportedImageException">Not an image the product would accept.</exception>
    internal static EvalImage Normalize(ImageNormalizer normalizer, ReadOnlySpan<byte> original)
    {
        ArgumentNullException.ThrowIfNull(normalizer);

        var normalized = normalizer.Normalize(original);

        return new EvalImage(new AiImage(normalized.Content, normalized.ContentType));
    }
}

/// <summary>How one evaluated call ended.</summary>
internal enum EvalCallOutcome
{
    /// <summary>The first response validated against the schema (FR-015's first attempt).</summary>
    ValidFirstAttempt,

    /// <summary>The first response failed validation; the one retry produced a valid response.</summary>
    ValidAfterRetry,

    /// <summary>Both responses failed validation — production would record a failure here.</summary>
    InvalidAfterRetry,

    /// <summary>
    /// The provider failed (transport, timeout, non-success status): there is no response to
    /// judge. Kept apart from <see cref="InvalidAfterRetry"/> because it says nothing about the
    /// model's output, only about its availability at the time.
    /// </summary>
    ProviderFailed,
}

/// <summary>What one evaluated call measured.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="WallTime">
/// From dispatch to a validated answer or a final failure, including the schema retry when one
/// happened — production waits for the retry too, so it is part of the latency a listing sees.
/// </param>
/// <param name="InputTokens">Prompt tokens across every attempt, from the responses' usage.</param>
/// <param name="OutputTokens">Completion tokens across every attempt.</param>
/// <param name="UsageReported">
/// False when any response arrived without token usage. Its tokens then read as zero, so a
/// token figure built on it would understate consumption; <see cref="CallMetrics"/> refuses to.
/// </param>
/// <param name="ImagesSent">
/// Images that crossed the boundary: the call's images once per attempt, because the retry
/// re-sends them. This is at least what production's budget charges (which counts a call's
/// images once), so a model that fits the image budget here fits it in production.
/// </param>
/// <param name="Attempts">Requests made: 1, or 2 with the schema retry.</param>
/// <param name="ProviderError">The provider's error, when <paramref name="Outcome"/> is <see cref="EvalCallOutcome.ProviderFailed"/>.</param>
internal sealed record EvalCallRecord(
    EvalCallOutcome Outcome,
    TimeSpan WallTime,
    int InputTokens,
    int OutputTokens,
    bool UsageReported,
    int ImagesSent,
    int Attempts,
    string? ProviderError)
{
    internal bool FirstAttemptValid => Outcome == EvalCallOutcome.ValidFirstAttempt;

    internal int Tokens => InputTokens + OutputTokens;
}

/// <summary>One evaluated call: the validated document, if any, and what the call measured.</summary>
/// <param name="Document">The schema-valid response, or null when the call did not produce one.</param>
internal sealed record EvalCall(JsonNode? Document, EvalCallRecord Record);

/// <summary>
/// T158 — calls the model of one registry row the way production would, before that row may
/// be enabled.
///
/// <para>
/// <b>Why not <see cref="AiService"/>.</b> AiService dispatches only to the enabled entry for a
/// task, and VIII.4 forbids enabling a model until it has passed this harness, so the model under
/// evaluation is by definition one AiService will not call. Everything else is production's own
/// code, not a copy of it:
/// </para>
/// <list type="bullet">
///   <item><description>the active prompt and schema from the <see cref="IPromptRegistry"/> the host
///   loads (the current version — the one production would send);</description></item>
///   <item><description>AiService's own message building and request options
///   (<see cref="AiService.BuildMessages"/>, <see cref="AiService.ChatOptionsFor"/>), so the system
///   prompt, user input, inlined images, JSON response format and the row's temperature are
///   identical;</description></item>
///   <item><description><see cref="StructuredOutputValidator.ValidateWithSingleRetryAsync"/>, so the
///   one retry and its feedback are FR-015's, not a reimplementation;</description></item>
///   <item><description>the host's <see cref="IChatClientFactory"/>, so base URL, credential and
///   resilience policy are the host's (see <see cref="EvalEnvironment"/>).</description></item>
/// </list>
/// <para>
/// <b>What is deliberately left out:</b> the budget check, the response cache, usage recording
/// and the per-provider circuit breaker. The first three would charge or serve an evaluation as
/// if it were a listing — a cached answer in particular would measure the cache, not the model.
/// The breaker exists to fail over, and an evaluation must never fail over: a verdict is about
/// this row's model only. Provider failures are recorded as such instead.
/// </para>
/// <para>
/// <b>Bound to one target.</b> The invoker is constructed for a <see cref="RegistryTarget"/> and
/// exposes no way to name another, so a suite can only ever call the model its verdict will be
/// recorded against.
/// </para>
/// </summary>
internal sealed class EvalModelInvoker
{
    private readonly IChatClientFactory clients;
    private readonly ImageNormalizer normalizer;
    private readonly ModelRegistryEntry entry;

    /// <exception cref="ModelUnavailableException">The task has no prompt on disk.</exception>
    /// <exception cref="StructuredOutputException">The task's declared schema is itself not valid JSON.</exception>
    internal EvalModelInvoker(
        RegistryTarget target,
        IPromptRegistry prompts,
        IChatClientFactory clients,
        ImageNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(normalizer);

        Target = target;
        this.clients = clients;
        this.normalizer = normalizer;

        Prompt = prompts.GetActive(target.TaskKey);

        // Surfaces a broken schema before any model is called. Validate throws only for our own
        // schema; for "{}" against a valid one it merely reports the document invalid. Once this
        // passes, a StructuredOutputException during a call can only mean the model's answer
        // failed validation — which is what InvalidAfterRetry records.
        StructuredOutputValidator.Validate("{}", Prompt.OutputSchema);

        entry = ToRegistryEntry(target);
    }

    /// <summary>The registry row every call goes to.</summary>
    internal RegistryTarget Target { get; }

    /// <summary>The prompt every call sends, for the report: its version and hash.</summary>
    internal PromptDefinition Prompt { get; }

    /// <summary>Normalizes an evaluation photo with the host's settings (VIII.4.c).</summary>
    /// <exception cref="UnsupportedImageException">Not an image the product would accept.</exception>
    internal EvalImage PrepareImage(ReadOnlySpan<byte> original) => EvalImage.Normalize(normalizer, original);

    /// <summary>
    /// Sends one request to the target model and reports what it measured.
    /// </summary>
    /// <param name="input">
    /// The task input, built as production builds it — for example
    /// <see cref="Libs.Ai.Tasks.PhotoAnalysisTask.BuildInput"/>.
    /// </param>
    /// <param name="images">Normalized images to send with it; empty for a text task.</param>
    /// <param name="cancellationToken">Cancels the evaluation; never recorded as a provider failure.</param>
    internal async Task<EvalCall> InvokeAsync(
        string input,
        IReadOnlyList<EvalImage> images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(images);

        var aiImages = images.Select(image => image.Image).ToList();

        var attempts = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        var usageReported = true;

        var stopwatch = Stopwatch.StartNew();

        EvalCallRecord Record(EvalCallOutcome outcome, string? providerError = null) => new(
            outcome,
            stopwatch.Elapsed,
            inputTokens,
            outputTokens,
            usageReported,
            aiImages.Count * attempts,
            attempts,
            providerError);

        try
        {
            using var client = clients.Create(entry);

            var document = await StructuredOutputValidator.ValidateWithSingleRetryAsync(
                Target.TaskKey,
                Prompt.OutputSchema,
                async (retryFeedback, token) =>
                {
                    attempts++;

                    var response = await client.GetResponseAsync(
                        AiService.BuildMessages(Prompt, input, aiImages, retryFeedback),
                        AiService.ChatOptionsFor(entry),
                        token);

                    if (response.Usage is { InputTokenCount: { } prompt, OutputTokenCount: { } completion })
                    {
                        inputTokens += (int)prompt;
                        outputTokens += (int)completion;
                    }
                    else
                    {
                        usageReported = false;
                    }

                    return response.Text;
                },
                cancellationToken);

            stopwatch.Stop();

            // ValidateWithSingleRetryAsync makes the second request only when the first answer
            // failed validation, so one attempt and a document means the first answer was valid.
            return new EvalCall(
                document,
                Record(attempts == 1 ? EvalCallOutcome.ValidFirstAttempt : EvalCallOutcome.ValidAfterRetry));
        }
        catch (StructuredOutputException)
        {
            stopwatch.Stop();
            return new EvalCall(null, Record(EvalCallOutcome.InvalidAfterRetry));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && AiService.IsFailoverWorthy(exception))
        {
            stopwatch.Stop();
            return new EvalCall(null, Record(EvalCallOutcome.ProviderFailed, exception.Message));
        }
    }

    /// <summary>
    /// The registry row as the chat-client factory and the request options read it: identity,
    /// endpoint and parameters.
    ///
    /// <para>
    /// No fallback, because an evaluation is about this model only. License and evaluation
    /// status are set to their not-cleared values rather than loaded: nothing on this path reads
    /// them, and if this entry ever reached code that checks the VIII.4 gate, it should be
    /// refused rather than read as cleared.
    /// </para>
    /// </summary>
    internal static ModelRegistryEntry ToRegistryEntry(RegistryTarget target) => new(
        target.Id,
        target.TaskKey,
        target.Provider,
        target.ModelId,
        target.Endpoint,
        target.Parameters,
        FallbackRegistryId: null,
        License: null,
        EvalStatus: ModelEvalStatus.Unverified,
        target.Enabled);
}
