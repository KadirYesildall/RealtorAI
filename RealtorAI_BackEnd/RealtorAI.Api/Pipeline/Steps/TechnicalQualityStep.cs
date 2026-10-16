using Modules.Media;

namespace Pipeline.Steps;

/// <summary>
/// T095 — technical quality flags for every photo (FR-009): blur, exposure, tilt, resolution.
///
/// <para>
/// Measured by the sidecar from the normalized copy, judged here against the API's thresholds
/// (<see cref="QualityRules"/>). Deterministic and model-free, so it runs whatever the registry
/// holds. A sidecar outage fails this step only: the flags are unavailable, and analysis goes on
/// without them (contracts/vision-sidecar.md).
/// </para>
/// </summary>
public sealed class TechnicalQualityStep : IPipelineStep
{
    private readonly IPipelineStore store;
    private readonly IPhotoStorage storage;
    private readonly IVisionSidecar sidecar;
    private readonly QualityThresholds thresholds;

    public TechnicalQualityStep(IPipelineStore store, IPhotoStorage storage, IVisionSidecar sidecar, QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(thresholds);

        this.store = store;
        this.storage = storage;
        this.sidecar = sidecar;
        this.thresholds = thresholds;
    }

    public PipelineStep Kind => PipelineStep.TechnicalQuality;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        foreach (var photo in run.Photos.Where(photo => photo.NormalizedStorageKey is not null))
        {
            try
            {
                var normalized = await storage.GetNormalizedAsync(photo.NormalizedStorageKey!, cancellationToken);

                // The sidecar is given no minimum: resolution is judged against the original's
                // dimensions, which the sidecar never sees.
                var measured = await sidecar.MeasureQualityAsync(normalized, minWidth: 1, minHeight: 1, cancellationToken);
                var flags = QualityRules.Flags(measured, photo.Width, photo.Height, run.MinWidth, run.MinHeight, thresholds);

                await store.SaveQualityFlagsAsync(photo.Id, flags, cancellationToken);
            }
            catch (VisionSidecarUnavailableException)
            {
                return StepOutcome.Failed(
                    "The photo quality service is unavailable, so blur, exposure and tilt were not checked. "
                    + "Everything else continues; run analysis again later to add these checks.");
            }
            catch (Exception exception) when (exception is StorageRequestException or StorageObjectNotFoundException)
            {
                return StepOutcome.Failed("A photo's processing copy could not be read, so quality was not checked. Run analysis again.");
            }
        }

        return StepOutcome.Done;
    }
}
