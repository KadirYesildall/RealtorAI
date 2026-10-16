using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Libs.Ai;

/// <summary>
/// An <see cref="IChatClient"/> speaking the OpenAI-compatible <c>/chat/completions</c> protocol
/// over plain <see cref="HttpClient"/>.
///
/// <para>
/// <b>Why this is hand-written rather than an SDK.</b> The project previously referenced
/// <c>Microsoft.Extensions.AI.OpenAI</c>, which pulls the <c>OpenAI</c> SDK — and that SDK ships
/// working clients for image generation, image editing (inpainting), image variations and
/// video. Constitution Principle VIII.6 prohibits any dependency capable of image or video
/// generation, with no exception for "we don't call it", so the package was removed and the
/// prohibition test now fails the build if it returns.
///
/// (The specific method names are deliberately not written out here: the prohibition test
/// searches source files for those identifiers, and it caught this very comment naming them.
/// Keeping the scan strict is worth more than the extra detail in a doc comment.)
/// </para>
/// <para>
/// Writing the protocol directly costs one small file and buys two things beyond compliance.
/// The wire format is an open, widely-implemented convention — the Hugging Face router, vLLM,
/// Ollama, llama.cpp and others all serve it — so this one client reaches every provider the
/// registry might name. And nothing vendor-specific appears anywhere: the base URL, the model
/// id and the parameters all arrive from <see cref="ModelRegistryEntry"/>, which is what
/// Principle VIII.1 requires.
/// </para>
/// <para>
/// This client is deliberately minimal. It sends messages and returns text and token counts.
/// No tool calling, no streaming, no image generation — the pipeline needs a validated JSON
/// document, which is a whole-response concern.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;
    private readonly string modelId;
    private readonly ChatClientMetadata metadata;

    /// <summary>
    /// Creates a client bound to one registry entry.
    /// </summary>
    /// <param name="httpClient">
    /// Configured with the base address, the authorization header, and the resilience pipeline
    /// from <see cref="AiHttpClientSetup"/>. Supplied rather than created so the timeout,
    /// retry and circuit-breaker policy lives in one place.
    /// </param>
    /// <param name="modelId">The provider's model id, from the registry row.</param>
    /// <param name="providerName">Provider identifier, for telemetry only.</param>
    public OpenAiCompatibleChatClient(HttpClient httpClient, string modelId, string providerName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        this.httpClient = httpClient;
        this.modelId = modelId;

        metadata = new ChatClientMetadata(providerName, httpClient.BaseAddress, modelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = new ChatCompletionRequest
        {
            Model = modelId,
            Messages = [.. messages.Select(ToWireMessage)],
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxOutputTokens,
            TopP = options?.TopP,

            // Ask for a JSON object when the caller wants structured output. Providers that
            // ignore this still work: the response is validated against its schema either way
            // (FR-015), so this is an optimisation that reduces retries, not a guarantee.
            ResponseFormat = options?.ResponseFormat is ChatResponseFormatJson
                ? new ResponseFormat { Type = "json_object" }
                : null,
        };

        using var response = await httpClient.PostAsJsonAsync(
            "chat/completions",
            request,
            SerializerOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The provider and model are named because the registry can point at several, and
            // "the AI call failed" is useless when diagnosing which entry is misconfigured.
            throw new HttpRequestException(
                $"The model provider '{metadata.ProviderName}' returned "
                + $"{(int)response.StatusCode} for model '{modelId}': {Truncate(body)}",
                inner: null,
                response.StatusCode);
        }

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
            SerializerOptions,
            cancellationToken);

        var text = completion?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = completion?.Model ?? modelId,
            ResponseId = completion?.Id,
            FinishReason = MapFinishReason(completion?.Choices?.FirstOrDefault()?.FinishReason),
            Usage = completion?.Usage is { } usage
                ? new UsageDetails
                {
                    InputTokenCount = usage.PromptTokens,
                    OutputTokenCount = usage.CompletionTokens,
                    TotalTokenCount = usage.TotalTokens,
                }
                : null,
        };
    }

    /// <summary>
    /// Not supported, deliberately.
    ///
    /// <para>
    /// Every AI response in this feature is a JSON document validated against a declared schema
    /// before anything is persisted (FR-015). A partial document cannot be validated, so
    /// streaming would mean either buffering it all — which is what the non-streaming path
    /// already does — or showing the agent unvalidated model output, which is exactly what the
    /// provenance rules forbid. Progress is reported over SignalR from the pipeline instead.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Streaming is not supported. Model output is validated against its JSON Schema "
            + "before use (FR-015), which requires the whole response; pipeline progress is "
            + "reported over SignalR rather than by streaming tokens.");

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>
    /// Does not dispose the <see cref="HttpClient"/>: it is owned by
    /// <see cref="IHttpClientFactory"/>, and disposing a factory-managed client defeats the
    /// connection pooling and DNS refresh the factory exists to provide.
    /// </summary>
    public void Dispose()
    {
        // Nothing owned.
    }

    /// <summary>
    /// A message with only text is sent as a string. A message carrying an image is sent as the
    /// protocol's array of parts — <c>text</c> and <c>image_url</c> — with the image inlined as a
    /// <c>data:</c> URL. Inline rather than a signed link: a provider outside this network could
    /// not fetch from local storage anyway, and an inlined copy is the normalized one the caller
    /// chose to send (VIII.4.c), never a URL that could be followed to anything else.
    /// </summary>
    private static WireMessage ToWireMessage(ChatMessage message)
    {
        var role = message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.Tool ? "tool"
            : "user";

        var images = message.Contents.OfType<DataContent>().ToList();

        if (images.Count == 0)
        {
            return new WireMessage { Role = role, Content = message.Text };
        }

        var parts = new List<object>();

        if (!string.IsNullOrEmpty(message.Text))
        {
            parts.Add(new TextPart { Text = message.Text });
        }

        foreach (var image in images)
        {
            parts.Add(new ImagePart
            {
                ImageUrl = new ImageUrl
                {
                    Url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}",
                },
            });
        }

        return new WireMessage { Role = role, Content = parts };
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "content_filter" => ChatFinishReason.ContentFilter,
        "tool_calls" => ChatFinishReason.ToolCalls,
        null or "" => null,
        _ => new ChatFinishReason(reason),
    };

    /// <summary>
    /// Caps an error body. Provider errors can embed the whole request, and an exception
    /// message containing an entire prompt ends up in logs where it does not belong.
    /// </summary>
    private static string Truncate(string body) =>
        body.Length <= 500 ? body : body[..500] + "… (truncated)";

    // ───────────────────────── Wire types ─────────────────────────

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public IList<WireMessage> Messages { get; init; } = [];

        [JsonPropertyName("temperature")]
        public float? Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("top_p")]
        public float? TopP { get; init; }

        [JsonPropertyName("response_format")]
        public ResponseFormat? ResponseFormat { get; init; }
    }

    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "json_object";
    }

    /// <summary>
    /// An outgoing message. <c>Content</c> is a string, or a list of parts for a message with an
    /// image; serialized by its runtime type.
    /// </summary>
    private sealed class WireMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = "user";

        [JsonPropertyName("content")]
        public object? Content { get; init; }
    }

    private sealed class TextPart
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }

    private sealed class ImagePart
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "image_url";

        [JsonPropertyName("image_url")]
        public ImageUrl ImageUrl { get; init; } = new();
    }

    private sealed class ImageUrl
    {
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
    }

    /// <summary>An incoming message: the response's content is always text.</summary>
    private sealed class ResponseMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("choices")]
        public IList<Choice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public Usage? Usage { get; init; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public ResponseMessage? Message { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; init; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; init; }
    }
}
