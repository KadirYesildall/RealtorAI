using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Pipeline;

/// <summary>
/// The vision sidecar over HTTP (contracts/vision-sidecar.md). Internal only: the base address
/// is configuration, loopback in development and an internal network in deployment.
/// </summary>
public sealed class HttpVisionSidecar : IVisionSidecar
{
    private readonly HttpClient http;

    public HttpVisionSidecar(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    public async Task<QualityMeasurements> MeasureQualityAsync(
        ReadOnlyMemory<byte> normalizedImage,
        int minWidth,
        int minHeight,
        CancellationToken cancellationToken)
    {
        using var form = Form(normalizedImage);
        form.Add(new StringContent(minWidth.ToString(CultureInfo.InvariantCulture)), "min_width");
        form.Add(new StringContent(minHeight.ToString(CultureInfo.InvariantCulture)), "min_height");

        var body = await PostAsync<QualityResponse>("quality", form, cancellationToken);

        return new QualityMeasurements(
            body.Width,
            body.Height,
            body.BlurScore,
            body.MeanLuminance,
            body.ClippedHighlightRatio,
            body.ClippedShadowRatio,
            body.HorizonTiltDegrees);
    }

    public async Task<ImageEmbedding> EmbedAsync(ReadOnlyMemory<byte> normalizedImage, string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        using var form = Form(normalizedImage);
        form.Add(new StringContent(modelId), "model_id");

        var body = await PostAsync<EmbedResponse>("embed", form, cancellationToken);

        return new ImageEmbedding(body.ModelId, body.Dimension, body.Embedding);
    }

    private static MultipartFormDataContent Form(ReadOnlyMemory<byte> image)
    {
        var form = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");

        return form;
    }

    private async Task<T> PostAsync<T>(string path, MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.PostAsync(path, form, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new VisionSidecarUnavailableException("The vision sidecar could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw new VisionSidecarUnavailableException(
                    "The vision sidecar has no model loaded: " + await response.Content.ReadAsStringAsync(cancellationToken));
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new VisionSidecarUnavailableException($"The vision sidecar returned an empty /{path} response.");
        }
    }

    private sealed class QualityResponse
    {
        [JsonPropertyName("width")]
        public int Width { get; init; }

        [JsonPropertyName("height")]
        public int Height { get; init; }

        [JsonPropertyName("blur_score")]
        public double BlurScore { get; init; }

        [JsonPropertyName("mean_luminance")]
        public double MeanLuminance { get; init; }

        [JsonPropertyName("clipped_highlight_ratio")]
        public double ClippedHighlightRatio { get; init; }

        [JsonPropertyName("clipped_shadow_ratio")]
        public double ClippedShadowRatio { get; init; }

        [JsonPropertyName("horizon_tilt_degrees")]
        public double? HorizonTiltDegrees { get; init; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("model_id")]
        public string ModelId { get; init; } = string.Empty;

        [JsonPropertyName("dimension")]
        public int Dimension { get; init; }

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; init; } = [];
    }
}
