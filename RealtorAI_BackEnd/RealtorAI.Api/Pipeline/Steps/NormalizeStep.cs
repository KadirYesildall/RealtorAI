using Modules.Media;

namespace Pipeline.Steps;

/// <summary>
/// T095 — every photo has its normalized copy (FR-002, VIII.4.c).
///
/// <para>
/// Upload already produces the copy, so on a healthy listing this does nothing. It exists for the
/// photo whose copy is missing — an upload interrupted between the original and the copy — and
/// rebuilds it from the original with the same non-generative operations upload uses. The
/// original is read, never written: it lives in the write-once bucket.
/// </para>
/// </summary>
public sealed class NormalizeStep : IPipelineStep
{
    private readonly IPipelineStore store;
    private readonly IPhotoStorage storage;
    private readonly ImageNormalizer normalizer;

    public NormalizeStep(IPipelineStore store, IPhotoStorage storage, ImageNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(normalizer);

        this.store = store;
        this.storage = storage;
        this.normalizer = normalizer;
    }

    public PipelineStep Kind => PipelineStep.Normalize;

    public async Task<StepOutcome> ExecuteAsync(PipelineRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var failures = 0;

        foreach (var photo in run.Photos.Where(photo => photo.NormalizedStorageKey is null))
        {
            try
            {
                var original = await storage.GetOriginalAsync(photo.OriginalStorageKey, cancellationToken);
                var normalized = normalizer.Normalize(original);
                var key = PhotoStorageKeys.Normalized(run.ListingId, photo.Id, normalized.Extension);

                await storage.PutNormalizedAsync(key, normalized.Content, normalized.ContentType, cancellationToken);
                await store.SetNormalizedKeyAsync(photo.Id, key, cancellationToken);
            }
            catch (Exception exception) when (exception is StorageRequestException or StorageObjectNotFoundException or UnsupportedImageException or HttpRequestException)
            {
                failures++;
            }
        }

        return failures == 0
            ? StepOutcome.Done
            : StepOutcome.Failed($"{failures} photo(s) have no processing copy and could not be prepared. Upload them again.");
    }
}
