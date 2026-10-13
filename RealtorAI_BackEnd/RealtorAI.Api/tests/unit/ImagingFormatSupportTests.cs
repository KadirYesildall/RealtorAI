using ImageMagick;

namespace Tests.Unit;

/// <summary>
/// FR-003 requires accepting JPEG, PNG, HEIC, and WebP uploads. The imaging backend must
/// actually be able to decode all four — a missing delegate would surface as a runtime
/// upload failure for iPhone photos, which are HEIC by default and therefore the single most
/// likely real-world input.
///
/// This exists because the first imaging library chosen (SixLabors.ImageSharp) was swapped
/// out for two reasons: its Six Labors Split License fails Release builds without a purchased
/// key, and it has no native HEIC decoder. Magick.NET (Apache-2.0, bundling ImageMagick) was
/// selected instead. This test pins the capability so a future package change cannot silently
/// drop HEIC support.
/// </summary>
public sealed class ImagingFormatSupportTests
{
    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    [InlineData(MagickFormat.Heic)]
    [InlineData(MagickFormat.Heif)]
    public void SupportedUploadFormats_CanBeRead(MagickFormat format)
    {
        var info = MagickFormatInfo.Create(format);

        Assert.NotNull(info);
        Assert.True(
            info!.SupportsReading,
            $"FR-003 requires decoding {format}, but the imaging backend reports no read "
            + "support. Check that the native ImageMagick build includes the delegate.");
    }

    /// <summary>
    /// VIII.6 prohibits generative imagery. Normalization is limited to non-generative
    /// operations, all of which must be available: resize, compress/quality, format
    /// conversion, crop, rotate, and EXIF stripping. This asserts the write side of the
    /// formats we emit for normalized variants.
    /// </summary>
    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    public void NormalizedVariantFormats_CanBeWritten(MagickFormat format)
    {
        var info = MagickFormatInfo.Create(format);

        Assert.NotNull(info);
        Assert.True(
            info!.SupportsWriting,
            $"Normalized variants are emitted as {format}, which the backend cannot write.");
    }
}
