using Libs.Ai;
using Modules.Media;

namespace Pipeline.Steps;

/// <summary>
/// T094/T095/T098 — image embeddings, then near-duplicate flags (FR-009).
///
/// <para>
/// <b>The model comes from the registry</b> (<c>photo.embedding</c>, VIII.1); the sidecar is told
/// which to use and reports what it loaded. With no entry enabled — the state of every entry
/// until it is evaluated — the step is skipped with that reason, and duplicate detection falls
/// back to the exact-checksum match upload already makes (SC-010).
/// </para>
/// <para>
/// <b>The dimension is checked, never adjusted.</b> <c>photo_embeddings.embedding</c> is
/// <c>vector(768)</c>, a provisional width (0006). A vector of any other length is refused —
/// truncating or padding it would make every similarity score meaningless while still looking
/// plausible (contracts/vision-sidecar.md).
/// </para>
/// </summary>
public sealed class EmbeddingsStep : IPipelineStep
{
    /// <summary>The width of <c>photo_embeddings.embedding</c> (0006_media.sql).</summary>
    public const int ColumnDimension = 768;

    private readonly IPipelineStore store;
    private readonly IPhotoStorage storage;
    private readonly IVisionSidecar sidecar;
    private readonly IModelRegistry registry;
    private readonly QualityThresholds thresholds;

    public EmbeddingsStep(
        IPipelineStore store,
        IPhotoStorage storage,
        IVisionSidecar sidecar,
        IModelRegistry registry,
        QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(thresholds);

        this.store = store;
        this.storage = storage;
        this.sidecar = sidecar;
        this.registry = registry;
        this.thresholds = thresholds;
    }

    public PipelineStep Kind => PipelineStep.Embeddings;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        ModelRegistryEntry entry;

        try
        {
            entry = await registry.ResolveAsync(AiTaskKey.PhotoEmbedding, cancellationToken);
        }
        catch (ModelUnavailableException)
        {
            return StepOutcome.Skipped(
                "Near-duplicate detection is unavailable: no embedding model is enabled. Exact duplicates "
                + "are still caught at upload.");
        }

        var comparedModel = entry.ModelId;

        foreach (var photo in run.Photos.Where(photo => photo.NormalizedStorageKey is not null))
        {
            ImageEmbedding embedding;

            try
            {
                var normalized = await storage.GetNormalizedAsync(photo.NormalizedStorageKey!, cancellationToken);
                embedding = await sidecar.EmbedAsync(normalized, entry.ModelId, cancellationToken);
            }
            catch (VisionSidecarUnavailableException)
            {
                return StepOutcome.Failed(
                    "The image-embedding service is unavailable, so near-duplicates were not checked. Run analysis again later.");
            }
            catch (Exception exception) when (exception is StorageRequestException or StorageObjectNotFoundException)
            {
                return StepOutcome.Failed("A photo's processing copy could not be read, so near-duplicates were not checked.");
            }

            if (embedding.Dimension != ColumnDimension || embedding.Vector.Count != ColumnDimension)
            {
                return StepOutcome.Failed(
                    $"The embedding model returned {embedding.Vector.Count} values where {ColumnDimension} are stored. "
                    + "Its vectors were not saved; the registry entry needs correcting before near-duplicates can be checked.");
            }

            // Recorded under the model the sidecar reports it loaded, not the one it was asked
            // for: vectors from different models are not comparable.
            await store.SaveEmbeddingAsync(photo.Id, embedding.Vector, embedding.ModelId, cancellationToken);
            comparedModel = embedding.ModelId;
        }

        var pairs = await store.FindSimilarPairsAsync(run.ListingId, comparedModel, thresholds.NearDuplicateSimilarity, cancellationToken);
        var flagged = NearDuplicateDetector.PhotosToFlag(
            pairs,
            run.Photos.ToDictionary(photo => photo.Id, photo => photo.OrderIndex),
            thresholds.NearDuplicateSimilarity);

        foreach (var photoId in flagged)
        {
            await store.AddQualityFlagAsync(photoId, QualityFlag.NearDuplicate, cancellationToken);
        }

        return StepOutcome.Done;
    }
}
