using System.Text.Json;
using Libs.Ai;
using Modules.Media;

namespace Pipeline.Steps;

/// <summary>Analysis settings.</summary>
/// <param name="EstimatedTokensPerPhoto">
/// The pre-dispatch estimate the budget is checked against for one photo (FR-055). An estimate,
/// not a charge: usage is recorded from the provider's own token counts.
/// </param>
public sealed record PhotoAnalysisOptions(int EstimatedTokensPerPhoto);

/// <summary>
/// T096 — the per-photo vision analysis (FR-009, FR-010; Story 2 scenarios 1–5, 8).
///
/// <para>
/// <b>One call per photo, each on its own.</b> A photo is sent as its normalized copy (VIII.4.c)
/// with only its id beside it — no address, no agent notes — through <see cref="IAiService"/>,
/// which resolves the model from the registry, checks the budget before dispatch, validates the
/// answer against <c>photo.analysis</c>'s schema with one retry, and fails over. A photo that
/// fails is recorded as failed with its reason, and the others go on (scenario 8).
/// </para>
/// <para>
/// <b>Resumable.</b> Photos that already hold a successful analysis are not sent again, so
/// starting the pipeline again is how the agent retries the photos that failed.
/// </para>
/// <para>
/// <b>Budget.</b> When the listing's budget runs out mid-run, the photos not yet analysed are
/// marked failed with that reason and the run stops spending (FR-055). The agent finishes by
/// hand.
/// </para>
/// </summary>
public sealed class PhotoAnalysisStep : IPipelineStep
{
    private readonly IPipelineStore store;
    private readonly PipelineReporter reporter;
    private readonly IPhotoStorage storage;
    private readonly IAiService ai;
    private readonly PhotoAnalysisOptions options;

    public PhotoAnalysisStep(
        IPipelineStore store,
        PipelineReporter reporter,
        IPhotoStorage storage,
        IAiService ai,
        PhotoAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(options);

        this.store = store;
        this.reporter = reporter;
        this.storage = storage;
        this.ai = ai;
        this.options = options;
    }

    public PipelineStep Kind => PipelineStep.PhotoAnalysis;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var analysed = await store.AnalysedPhotosAsync(run.ListingId, cancellationToken);
        var succeeded = 0;
        var failed = 0;
        string? stopReason = null;

        foreach (var photo in run.Photos)
        {
            if (analysed.Contains(photo.Id))
            {
                succeeded++;
                await reporter.PhotoAsync(run, photo.Id, PhotoState.Done, null, cancellationToken);
                continue;
            }

            if (stopReason is not null)
            {
                failed++;
                await FailAsync(run, photo.Id, stopReason, cancellationToken);
                continue;
            }

            await reporter.PhotoAsync(run, photo.Id, PhotoState.Analyzing, null, cancellationToken);

            if (photo.NormalizedStorageKey is null)
            {
                failed++;
                await FailAsync(run, photo.Id, "This photo has no processing copy. Upload it again.", cancellationToken);
                continue;
            }

            try
            {
                var normalized = await storage.GetNormalizedAsync(photo.NormalizedStorageKey, cancellationToken);

                var result = await ai.ExecuteAsync(
                    new AiRequest(
                        AiTaskKey.PhotoAnalysis,
                        run.ListingId,
                        run.StartedBy,
                        Input(photo.Id),
                        ImageCount: 1,
                        options.EstimatedTokensPerPhoto,
                        [new AiImage(normalized, "image/jpeg")]),
                    cancellationToken);

                await store.SaveAnalysisAsync(
                    run,
                    new PhotoAnalysisOutcome(photo.Id, result.Document, result.ModelId, result.PromptVersion, result.PromptHash),
                    cancellationToken);

                succeeded++;
                await reporter.PhotoAsync(run, photo.Id, PhotoState.Done, null, cancellationToken);
            }
            catch (AiBudgetExceededException)
            {
                stopReason = "This listing's AI budget is used up, so this photo was not analysed. Classify and caption it by hand.";
                failed++;
                await FailAsync(run, photo.Id, stopReason, cancellationToken);
            }
            catch (StructuredOutputException)
            {
                failed++;
                await FailAsync(run, photo.Id, "The model's answer for this photo was not usable, even after a retry. Classify it by hand, or run analysis again.", cancellationToken);
            }
            catch (ModelUnavailableException)
            {
                failed++;
                await FailAsync(run, photo.Id, "Photo analysis is unavailable right now. Classify this photo by hand, or run analysis again later.", cancellationToken);
            }
            catch (Exception exception) when (exception is StorageRequestException or StorageObjectNotFoundException)
            {
                failed++;
                await FailAsync(run, photo.Id, "This photo's processing copy could not be read. Run analysis again.", cancellationToken);
            }
        }

        if (succeeded > 0 || failed == 0)
        {
            return StepOutcome.Done;
        }

        return StepOutcome.Failed(
            stopReason ?? "No photo could be analysed. Every photo can still be classified, captioned and ordered by hand.");
    }

    /// <summary>
    /// The text beside the image: which photo this is, and nothing else. The prompt states that
    /// this text is a reference, not an instruction.
    /// </summary>
    private static string Input(Guid photoId) =>
        JsonSerializer.Serialize(new { photoId, request = "Analyse the attached photograph." });

    private async Task FailAsync(PipelineRunContext run, Guid photoId, string reason, CancellationToken cancellationToken)
    {
        await store.SaveAnalysisFailureAsync(photoId, reason, cancellationToken);
        await reporter.PhotoAsync(run, photoId, PhotoState.Failed, reason, cancellationToken);
    }
}
