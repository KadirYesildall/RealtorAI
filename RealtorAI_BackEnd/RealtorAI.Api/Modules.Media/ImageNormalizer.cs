using ImageMagick;

namespace Modules.Media;

/// <summary>Normalization settings.</summary>
/// <param name="LongEdgePx">
/// Longest side of a normalized copy (~1280px, Constitution Section D). Images already smaller
/// are left at their size: enlarging adds no information, only pixels the camera never took.
/// </param>
/// <param name="JpegQuality">Compression quality of the normalized JPEG, 1–100.</param>
/// <param name="MaxSourcePixels">
/// Largest source image, in pixels, that will be decoded. A small compressed file can declare
/// enormous dimensions; checking the header first stops one upload from exhausting memory.
/// </param>
public sealed record NormalizationOptions(int LongEdgePx, int JpegQuality, long MaxSourcePixels);

/// <summary>A normalized copy, ready to store and send to a vision model.</summary>
/// <param name="SourceWidth">Width of the original as stored (before orientation is applied).</param>
/// <param name="SourceHeight">Height of the original as stored.</param>
public sealed record NormalizedImage(
    byte[] Content,
    string ContentType,
    string Extension,
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight,
    string SourceMimeType);

/// <summary>
/// The upload is not an image in an accepted format (FR-003). The message is written for the
/// agent, because it is shown per file.
/// </summary>
public sealed class UnsupportedImageException : Exception
{
    public UnsupportedImageException(string message)
        : base(message)
    {
    }

    public UnsupportedImageException()
    {
    }

    public UnsupportedImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// T058 — produces the normalized copy of a photo (FR-002, Constitution VIII.4.c and VIII.6).
///
/// <para>
/// <b>Only these operations, all non-generative:</b> apply the camera's orientation (a
/// rotation), downscale to the long-edge limit (a resize), convert to sRGB JPEG (a format
/// conversion), compress, and strip metadata. FR-002 permits crop as well; no Slice A flow asks
/// for one, so none exists. Nothing here synthesises, fills, retouches or enlarges a pixel —
/// <c>NoGenerativeImageryTests</c> fails the build if a generation capability appears anywhere.
/// </para>
/// <para>
/// <b>Metadata.</b> The normalized copy is what leaves the boundary, to a vision model, so it
/// carries no EXIF, GPS, XMP, IPTC, comment or embedded profile: a photo's GPS block is the
/// seller's home address in coordinates. Orientation is applied <i>before</i> stripping, so the
/// copy still displays the right way up once the EXIF orientation tag is gone.
/// </para>
/// <para>
/// <b>The original is never touched.</b> The input is a read-only span; this class has no way to
/// write to it, and the stored original lives in a write-once bucket regardless.
/// <c>ImageNormalizerTests</c> asserts the input bytes are unchanged after normalizing.
/// </para>
/// </summary>
public sealed class ImageNormalizer
{
    /// <summary>
    /// Accepted upload formats (FR-003), by what the bytes are — never by file name or the
    /// client's declared content type.
    /// </summary>
    private static readonly Dictionary<MagickFormat, string> AcceptedFormats = new()
    {
        [MagickFormat.Jpeg] = "image/jpeg",
        [MagickFormat.Jpg] = "image/jpeg",
        [MagickFormat.Png] = "image/png",
        [MagickFormat.Heic] = "image/heic",
        [MagickFormat.Heif] = "image/heif",
        [MagickFormat.WebP] = "image/webp",
    };

    private readonly NormalizationOptions options;

    public ImageNormalizer(NormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.LongEdgePx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.JpegQuality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.JpegQuality, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSourcePixels, 1);

        this.options = options;
    }

    /// <summary>
    /// Identifies the upload's format from its bytes and returns its MIME type.
    /// </summary>
    /// <exception cref="UnsupportedImageException">Not an accepted image.</exception>
    public string DetectMimeType(ReadOnlySpan<byte> original) => Inspect(original).MimeType;

    /// <summary>Builds the normalized copy of <paramref name="original"/>.</summary>
    /// <exception cref="UnsupportedImageException">
    /// The bytes are not an accepted image, cannot be decoded, or declare dimensions beyond the
    /// decode limit.
    /// </exception>
    public NormalizedImage Normalize(ReadOnlySpan<byte> original)
    {
        var (mimeType, sourceWidth, sourceHeight) = Inspect(original);

        try
        {
            // Decoded from a copy. Magick reads from managed memory it is handed, and the
            // caller's span must stay exactly as it was — the original is evidence.
            using var image = new MagickImage(original.ToArray());

            // Rotation, from the camera's own orientation tag, before that tag is stripped.
            image.AutoOrient();

            if (Math.Max(image.Width, image.Height) > (uint)options.LongEdgePx)
            {
                // Fits within a square box, so the aspect ratio is kept and only the longer
                // side reaches the limit.
                image.Resize(new MagickGeometry((uint)options.LongEdgePx, (uint)options.LongEdgePx));
            }

            image.ColorSpace = ColorSpace.sRGB;

            // EXIF (including GPS), XMP, IPTC, comments and embedded profiles.
            image.Strip();

            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)options.JpegQuality;

            return new NormalizedImage(
                image.ToByteArray(),
                "image/jpeg",
                "jpg",
                (int)image.Width,
                (int)image.Height,
                sourceWidth,
                sourceHeight,
                mimeType);
        }
        catch (MagickException exception)
        {
            throw new UnsupportedImageException(
                "This file looks like an image but could not be read. It may be damaged or "
                + "only partly uploaded. Try exporting it again.",
                exception);
        }
    }

    private (string MimeType, int Width, int Height) Inspect(ReadOnlySpan<byte> original)
    {
        if (original.IsEmpty)
        {
            throw new UnsupportedImageException("This file is empty.");
        }

        MagickImageInfo info;

        try
        {
            // Header only: format and declared dimensions, without decoding pixels.
            info = new MagickImageInfo(original.ToArray());
        }
        catch (MagickException exception)
        {
            throw new UnsupportedImageException(
                "This file is not a photo in a supported format. Use JPEG, PNG, HEIC or WebP.",
                exception);
        }

        if (!AcceptedFormats.TryGetValue(info.Format, out var mimeType))
        {
            throw new UnsupportedImageException(
                $"{info.Format} files are not accepted. Use JPEG, PNG, HEIC or WebP.");
        }

        if ((long)info.Width * info.Height > options.MaxSourcePixels)
        {
            throw new UnsupportedImageException(
                $"This photo is {info.Width}×{info.Height} pixels, larger than can be processed. "
                + "Export it at a smaller size and upload it again.");
        }

        return (mimeType, (int)info.Width, (int)info.Height);
    }
}
