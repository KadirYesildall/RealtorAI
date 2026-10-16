namespace Pipeline;

/// <summary>
/// Raw technical-quality measurements of one photo (contracts/vision-sidecar.md <c>/quality</c>).
/// The API owns the thresholds and turns these into flags; the sidecar's own flag field is not
/// read, so tuning a threshold needs no sidecar redeploy.
/// </summary>
/// <param name="HorizonTiltDegrees">Null when the photo has no usable horizon — not "level".</param>
public sealed record QualityMeasurements(
    int Width,
    int Height,
    double BlurScore,
    double MeanLuminance,
    double ClippedHighlightRatio,
    double ClippedShadowRatio,
    double? HorizonTiltDegrees);

/// <summary>
/// An image embedding (contracts/vision-sidecar.md <c>/embed</c>).
/// </summary>
/// <param name="ModelId">The model the sidecar actually loaded, echoed for the audit trail.</param>
/// <param name="Dimension">The vector length the sidecar reports.</param>
public sealed record ImageEmbedding(string ModelId, int Dimension, IReadOnlyList<float> Vector);

/// <summary>
/// The sidecar is unreachable, or reports that it cannot serve the request (503: weights not
/// loaded). The step that needed it is recorded as failed or skipped with this reason; the
/// listing stays completable by hand (SC-010).
/// </summary>
public sealed class VisionSidecarUnavailableException : Exception
{
    public VisionSidecarUnavailableException(string message)
        : base(message)
    {
    }

    public VisionSidecarUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VisionSidecarUnavailableException()
    {
    }
}

/// <summary>
/// The vision sidecar, as the pipeline uses it. Only normalized copies are ever sent
/// (VIII.4.c); the original never leaves the write-once bucket.
/// </summary>
public interface IVisionSidecar
{
    /// <exception cref="VisionSidecarUnavailableException">The sidecar cannot be reached.</exception>
    Task<QualityMeasurements> MeasureQualityAsync(
        ReadOnlyMemory<byte> normalizedImage,
        int minWidth,
        int minHeight,
        CancellationToken cancellationToken);

    /// <param name="modelId">From the <c>photo.embedding</c> registry entry; the sidecar never chooses.</param>
    /// <exception cref="VisionSidecarUnavailableException">Unreachable, or the model is not loaded.</exception>
    Task<ImageEmbedding> EmbedAsync(
        ReadOnlyMemory<byte> normalizedImage,
        string modelId,
        CancellationToken cancellationToken);
}
