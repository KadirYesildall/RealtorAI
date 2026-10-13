using Libs.Ai;
using Microsoft.Extensions.AI;

namespace Api.Ai;

/// <summary>
/// T044 — creates a chat client for a resolved registry entry.
///
/// <para>
/// The only place a model id and a provider endpoint meet an HTTP client, and both arrive from
/// the registry row rather than from configuration or code (Principle VIII.1). A registry entry
/// that declares its own <c>endpoint</c> overrides the default base URL, which is what allows
/// one provider-agnostic client to reach a hosted router, a self-hosted vLLM instance, or a
/// local model server without a code change.
/// </para>
/// </summary>
internal sealed class HttpChatClientFactory : IChatClientFactory
{
    private readonly IHttpClientFactory httpClientFactory;

    public HttpChatClientFactory(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        this.httpClientFactory = httpClientFactory;
    }

    public IChatClient Create(ModelRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var client = httpClientFactory.CreateClient(AiTransportOptions.HttpClientName);

        if (!string.IsNullOrWhiteSpace(entry.Endpoint))
        {
            // Trailing slash matters: without it a relative "chat/completions" would replace
            // the last path segment rather than extend it.
            var endpoint = entry.Endpoint.EndsWith('/') ? entry.Endpoint : entry.Endpoint + "/";

            client.BaseAddress = new Uri(endpoint);
        }

        return new OpenAiCompatibleChatClient(client, entry.ModelId, entry.Provider);
    }
}
