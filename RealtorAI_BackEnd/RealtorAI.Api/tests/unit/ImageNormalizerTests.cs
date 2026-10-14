using ImageMagick;
using ImageMagick.Drawing;
using Modules.Media;

namespace Tests.Unit;

/// <summary>
/// T059 — normalization strips location and camera metadata, applies only non-generative
/// operations, and never changes the original (FR-001, FR-002, VIII.4.c, VIII.6).
///
/// <para>
/// Test images are generated here rather than checked in, so each one carries exactly the
/// metadata a test is about — a real iPhone photo's GPS block, orientation tag and comment —
/// and the assertion cannot pass because a fixture happened to lack the thing being stripped.
/// </para>
/// </summary>
public sealed class ImageNormalizerTests
{
    private static readonly NormalizationOptions Options = new(LongEdgePx: 1280, JpegQuality: 85, MaxSourcePixels: 100_000_000);

    private static readonly ImageNormalizer Normalizer = new(Options);

    /// <summary>
    /// A photo as a phone would produce it: GPS coordinates, a camera model, an orientation tag
    /// and a comment, all of which the normalized copy must lose.
    /// </summary>
    private static byte[] PhotoWithMetadata(
        uint width,
        uint height,
        MagickFormat format = MagickFormat.Jpeg,
        ushort orientation = 1)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);

        // A corner marker, so orientation can be checked as pixels and not only as dimensions.
        image.Draw(new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, width / 4.0, height / 4.0));

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(34, 1), new Rational(3, 1), new Rational(1524, 100)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "W");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(118, 1), new Rational(14, 1), new Rational(3672, 100)]);
        exif.SetValue(ExifTag.Model, "iPhone 15 Pro");
        exif.SetValue(ExifTag.Orientation, orientation);
        image.SetProfile(exif);

        // Magick writes the orientation tag from this property, overwriting the profile's
        // value — setting only the EXIF entry silently produced an upright fixture.
        image.Orientation = (Orientation)orientation;

        image.Comment = "1234 Seller Street, private";
        image.Format = format;

        return image.ToByteArray();
    }

    // ───────────────────────────── The original is never changed ─────────────────────────────

    [Fact]
    public void Normalize_LeavesTheOriginalBytesUnchanged()
    {
        var original = PhotoWithMetadata(3000, 2000);
        var before = original.ToArray();

        _ = Normalizer.Normalize(original);

        Assert.Equal(before, original);
    }

    // ───────────────────────────── Metadata ─────────────────────────────

    [Fact]
    public void Normalize_StripsExifIncludingGps()
    {
        var normalized = Normalizer.Normalize(PhotoWithMetadata(3000, 2000));

        using var output = new MagickImage(normalized.Content);

        Assert.Null(output.GetExifProfile());
        Assert.Null(output.GetXmpProfile());
        Assert.Null(output.GetIptcProfile());
        Assert.Null(output.GetColorProfile());
        Assert.True(string.IsNullOrEmpty(output.Comment), "The comment survived normalization.");
    }

    /// <summary>
    /// The precondition for the test above meaning anything: the source really did carry GPS.
    /// </summary>
    [Fact]
    public void TheSourcePhoto_CarriesGps()
    {
        using var source = new MagickImage(PhotoWithMetadata(3000, 2000));

        var exif = source.GetExifProfile();

        Assert.NotNull(exif);
        Assert.NotNull(exif!.GetValue(ExifTag.GPSLatitude));
    }

    [Theory]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    public void Normalize_StripsMetadataFromOtherAcceptedFormats(MagickFormat format)
    {
        var normalized = Normalizer.Normalize(PhotoWithMetadata(1600, 1200, format));

        using var output = new MagickImage(normalized.Content);

        Assert.Null(output.GetExifProfile());
        Assert.Equal("image/jpeg", normalized.ContentType);
    }

    // ───────────────────────────── Resize ─────────────────────────────

    [Fact]
    public void Normalize_DownscalesTheLongEdgeToTheLimit_KeepingAspectRatio()
    {
        var normalized = Normalizer.Normalize(PhotoWithMetadata(4000, 3000));

        Assert.Equal(1280, normalized.Width);
        Assert.Equal(960, normalized.Height);
        Assert.Equal(4000, normalized.SourceWidth);
        Assert.Equal(3000, normalized.SourceHeight);
    }

    /// <summary>
    /// Enlarging a small photo would add pixels the camera never captured. Not generative in the
    /// AI sense, but nothing is gained by it and VIII.6 is clearest when nothing is invented.
    /// </summary>
    [Fact]
    public void Normalize_NeverUpscalesASmallerPhoto()
    {
        var normalized = Normalizer.Normalize(PhotoWithMetadata(800, 600));

        Assert.Equal(800, normalized.Width);
        Assert.Equal(600, normalized.Height);
    }

    [Fact]
    public void Normalize_OutputsAJpegThatDecodes()
    {
        var normalized = Normalizer.Normalize(PhotoWithMetadata(2000, 1500, MagickFormat.Png));

        using var output = new MagickImage(normalized.Content);

        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.Equal("jpg", normalized.Extension);
        Assert.Equal("image/png", normalized.SourceMimeType);
    }

    // ───────────────────────────── Orientation ─────────────────────────────

    /// <summary>
    /// A portrait phone photo is stored landscape with orientation 6 ("rotate 90° clockwise").
    /// Once the tag is stripped, a copy that had not been rotated first would display sideways.
    /// </summary>
    [Fact]
    public void Normalize_AppliesOrientationBeforeStrippingIt()
    {
        var source = PhotoWithMetadata(1600, 1200, orientation: 6);

        // Precondition: the fixture really is a sideways-stored portrait.
        using (var decoded = new MagickImage(source))
        {
            Assert.Equal(Orientation.RightTop, decoded.Orientation);
        }

        var normalized = Normalizer.Normalize(source);

        Assert.True(
            normalized.Height > normalized.Width,
            $"Expected portrait after applying orientation 6, got {normalized.Width}×{normalized.Height}.");

        using var output = new MagickImage(normalized.Content);
        Assert.Null(output.GetExifProfile());
    }

    // ───────────────────────────── Rejection (FR-003) ─────────────────────────────

    [Fact]
    public void Normalize_RejectsAnUnacceptedImageFormat()
    {
        using var gif = new MagickImage(MagickColors.White, 50, 50) { Format = MagickFormat.Gif };

        var exception = Assert.Throws<UnsupportedImageException>(() => Normalizer.Normalize(gif.ToByteArray()));

        Assert.Contains("JPEG, PNG, HEIC or WebP", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_RejectsBytesThatAreNotAnImage()
    {
        Assert.Throws<UnsupportedImageException>(
            () => Normalizer.Normalize("definitely not a photo"u8.ToArray()));
    }

    [Fact]
    public void Normalize_RejectsAnEmptyFile()
    {
        Assert.Throws<UnsupportedImageException>(() => Normalizer.Normalize([]));
    }

    /// <summary>
    /// The decode limit is checked from the header, before pixels are decoded, so an oversized
    /// declaration cannot exhaust memory.
    /// </summary>
    [Fact]
    public void Normalize_RejectsAnImageBeyondTheDecodeLimit()
    {
        var tight = new ImageNormalizer(Options with { MaxSourcePixels = 10_000 });

        Assert.Throws<UnsupportedImageException>(() => tight.Normalize(PhotoWithMetadata(200, 200)));
    }

    /// <summary>
    /// Format is decided by the bytes, so a JPEG's MIME type is reported correctly whatever the
    /// client claimed.
    /// </summary>
    [Fact]
    public void DetectMimeType_ReadsTheFormatFromTheBytes()
    {
        Assert.Equal("image/png", Normalizer.DetectMimeType(PhotoWithMetadata(100, 100, MagickFormat.Png)));
        Assert.Equal("image/jpeg", Normalizer.DetectMimeType(PhotoWithMetadata(100, 100)));
    }
}
