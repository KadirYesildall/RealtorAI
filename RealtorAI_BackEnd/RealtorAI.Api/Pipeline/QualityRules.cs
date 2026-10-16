namespace Pipeline;

/// <summary>
/// The thresholds that turn the sidecar's measurements into quality flags (contracts/
/// vision-sidecar.md). API configuration, so tuning one needs no sidecar redeploy.
/// </summary>
/// <param name="NearDuplicateSimilarity">
/// Cosine similarity at or above which two photos are flagged as near-duplicates. Provisional
/// until the embedding model is evaluated on the labelled photo set (T156): the right value
/// depends on the model, and is configuration for exactly that reason.
/// </param>
public sealed record QualityThresholds(
    double BlurScore,
    double DarkLuminance,
    double BrightLuminance,
    double ClippedRatio,
    double TiltDegrees,
    double NearDuplicateSimilarity);

/// <summary>The quality flags of the contract's <c>PhotoAnalysis.qualityFlags</c>.</summary>
public static class QualityFlag
{
    public const string Blurry = "blurry";
    public const string TooDark = "too_dark";
    public const string Overexposed = "overexposed";
    public const string TiltedHorizon = "tilted_horizon";
    public const string BelowMinResolution = "below_min_resolution";
    public const string NearDuplicate = "near_duplicate";
}

/// <summary>
/// T093 (API side) — technical quality as flags. Deterministic and model-free, so it is not
/// subject to the VIII.4 model gates.
/// </summary>
public static class QualityRules
{
    /// <summary>
    /// Flags from the sidecar's measurements of the normalized copy, plus the resolution check,
    /// which is made against the <i>original</i>'s dimensions: the normalized copy is downscaled to
    /// about 1280px, so measuring it would flag every photo against an MLS minimum above that.
    /// </summary>
    /// <param name="minWidth">The rule profile's minimum, or null when it has none verified — no flag then.</param>
    public static IReadOnlyList<string> Flags(
        QualityMeasurements measured,
        int? originalWidth,
        int? originalHeight,
        int? minWidth,
        int? minHeight,
        QualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(thresholds);

        var flags = new List<string>();

        if (measured.BlurScore < thresholds.BlurScore)
        {
            flags.Add(QualityFlag.Blurry);
        }

        if (measured.MeanLuminance < thresholds.DarkLuminance)
        {
            flags.Add(QualityFlag.TooDark);
        }

        if (measured.MeanLuminance > thresholds.BrightLuminance || measured.ClippedHighlightRatio > thresholds.ClippedRatio)
        {
            flags.Add(QualityFlag.Overexposed);
        }

        // Null is "no horizon", which is not a tilt.
        if (measured.HorizonTiltDegrees is { } tilt && Math.Abs(tilt) > thresholds.TiltDegrees)
        {
            flags.Add(QualityFlag.TiltedHorizon);
        }

        if ((minWidth is { } width && originalWidth < width) || (minHeight is { } height && originalHeight < height))
        {
            flags.Add(QualityFlag.BelowMinResolution);
        }

        return flags;
    }
}
