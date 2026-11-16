using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace Libs.Ai;

/// <summary>Where the AI HTTP client gets its base URL and credential.</summary>
/// <param name="DefaultBaseUrl">
/// Used when a registry entry declares no endpoint of its own.
/// </param>
/// <param name="ApiKeyEnvironmentVariable">
/// Name of the environment variable holding the token. The <i>name</i> is configuration; the
/// token itself never appears in a settings file, and never in anything prefixed
/// <c>NEXT_PUBLIC_</c>.
/// </param>
public sealed record AiTransportOptions(string DefaultBaseUrl, string ApiKeyEnvironmentVariable)
{
    public const string HttpClientName = "ai-provider";

    /// <summary>Configuration key of <see cref="DefaultBaseUrl"/>.</summary>
    public const string DefaultBaseUrlKey = "Ai:HuggingFace:RouterBaseUrl";

    /// <summary>Configuration key naming the environment variable that holds the token.</summary>
    public const string ApiKeyEnvironmentVariableKey = "Ai:HuggingFace:ApiKeyEnvVar";

    /// <summary>The variable read when <see cref="ApiKeyEnvironmentVariableKey"/> is not set.</summary>
    public const string DefaultApiKeyEnvironmentVariable = "HF_TOKEN";

    /// <summary>
    /// Builds the options from the two configured values, as the API host and the evaluation
    /// harness both read them.
    ///
    /// <para>
    /// One definition of the keys and the default because the harness must reach a model with
    /// the same base URL and the same credential the host would use. Two copies of the key names
    /// could drift, and a harness that silently resolved a different credential would measure a
    /// model through an account production never uses.
    /// </para>
    /// </summary>
    /// <param name="defaultBaseUrl">The value of <see cref="DefaultBaseUrlKey"/>.</param>
    /// <param name="apiKeyEnvironmentVariable">The value of <see cref="ApiKeyEnvironmentVariableKey"/>.</param>
    /// <exception cref="InvalidOperationException">No default base URL is configured.</exception>
    public static AiTransportOptions FromConfiguration(string? defaultBaseUrl, string? apiKeyEnvironmentVariable) =>
        new(
            string.IsNullOrWhiteSpace(defaultBaseUrl)
                ? throw new InvalidOperationException(
                    $"{DefaultBaseUrlKey} is not configured. It is the default base URL for "
                    + "providers whose registry entry declares no endpoint of its own.")
                : defaultBaseUrl,
            string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable)
                ? DefaultApiKeyEnvironmentVariable
                : apiKeyEnvironmentVariable);
}

/// <summary>
/// T048 — timeouts, backoff, and a per-provider circuit breaker for model calls.
///
/// <para>
/// <b>Why AI calls need their own policy.</b> They are slow by nature — a vision model on a
/// dozen photos takes tens of seconds — so the default short timeouts would cancel healthy
/// work, while the absence of a breaker would let one unavailable provider stall every
/// background job at once.
/// </para>
/// <para>
/// <b>The breaker lives outside this handler, in <see cref="ProviderCircuitBreakers"/>.</b> The
/// registry can name several providers, and failover (<c>fallback_registry_id</c>) is only
/// useful if the fallback is reachable while the primary is not. A breaker attached to the
/// shared client would open on the primary's failures and reject the fallback as well, so it
/// has to be keyed by provider — which is only known once the registry entry is resolved.
/// </para>
/// </summary>
public static class AiHttpClientSetup
{
    /// <summary>
    /// Generous because model inference is genuinely slow. The pipeline runs these on Hangfire
    /// background jobs rather than inside a request, so a long wait costs a worker slot, not a
    /// user-facing response.
    /// </summary>
    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Bounds the whole operation including retries, so a pathological provider cannot hold a
    /// worker indefinitely.
    /// </summary>
    internal static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers the named <see cref="HttpClient"/> that model calls go through.
    /// </summary>
    public static IServiceCollection AddAiHttpClient(
        this IServiceCollection services,
        AiTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services
            .AddHttpClient(AiTransportOptions.HttpClientName, client =>
            {
                // A trailing slash matters: without it, a relative "chat/completions" would
                // replace the last path segment of the base address rather than extend it, so
                // ".../v1" would silently become ".../chat/completions".
                var baseUrl = options.DefaultBaseUrl.EndsWith('/')
                    ? options.DefaultBaseUrl
                    : options.DefaultBaseUrl + "/";

                client.BaseAddress = new Uri(baseUrl);

                // Timeout is handled by the resilience pipeline below; leaving the client's own
                // timeout in place would cut attempts short before the policy could act.
                client.Timeout = Timeout.InfiniteTimeSpan;

                var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKey);
                }

                // Deliberately not throwing when the key is absent. Every registry entry ships
                // disabled under VIII.4, so a deployment with no model enabled and no token is
                // a valid state; the call itself fails with the provider's 401 if one is ever
                // attempted without a credential, which is a clearer signal than a startup
                // crash for a feature nobody enabled.
            })
            .AddResilienceHandler("ai-provider-resilience", (pipeline, context) =>
            {
                pipeline.AddTimeout(TotalTimeout);

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,

                    // Jitter so a batch of photo-analysis jobs that all fail together do not
                    // retry in lockstep and reproduce the spike that caused the failure.
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2),

                    ShouldHandle = args => ValueTask.FromResult(
                        IsTransient(args.Outcome.Result, args.Outcome.Exception)),
                });

                pipeline.AddTimeout(PerAttemptTimeout);

                // No circuit breaker here. A breaker on the shared HttpClient handler would be
                // shared across every provider, so the primary's failures would open it for
                // the fallback too — turning one provider's outage into a total outage and
                // making fallback pointless. Providers are configured at runtime in the
                // registry, so the breaker has to be keyed by provider where the provider is
                // actually known: see ProviderCircuitBreakers, used by AiService.
                _ = context;
            });

        return services;
    }

    /// <summary>
    /// Which failures are worth retrying.
    ///
    /// <para>
    /// 429 and 5xx are. A 400 or 422 is not: the request was malformed or the model rejected
    /// it, and sending it again produces the same answer while spending the listing's token
    /// budget (FR-055). 401 and 403 are not either — a missing or wrong credential is a
    /// configuration problem, and retrying it looks like a brute-force attempt to the provider.
    /// </para>
    /// <para>
    /// A per-attempt timeout is: that strategy sits inside the retry precisely so one hung
    /// attempt can be abandoned and another made, within <see cref="TotalTimeout"/>. It throws
    /// <see cref="TimeoutRejectedException"/>, which is not a <see cref="TimeoutException"/>, so
    /// it has to be named.
    /// </para>
    /// </summary>
    internal static bool IsTransient(HttpResponseMessage? response, Exception? exception)
    {
        if (exception is HttpRequestException or TaskCanceledException or TimeoutException or TimeoutRejectedException)
        {
            return true;
        }

        if (response is null)
        {
            return false;
        }

        return response.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }
}

/// <summary>
/// A circuit breaker per provider, keyed by the registry entry's provider name.
///
/// <para>
/// Separate from the HTTP handler because provider identity comes from the database at runtime,
/// not from startup configuration: one named <see cref="HttpClient"/> serves every provider, so
/// a breaker registered against it could not distinguish them. Keying here is what makes
/// <c>fallback_registry_id</c> meaningful — the primary's breaker can be open while the
/// fallback's is closed.
/// </para>
/// </summary>
public sealed class ProviderCircuitBreakers : IDisposable
{
    private readonly ResiliencePipelineRegistry<string> registry = new();

    /// <summary>
    /// The breaker for one provider, created on first use and reused thereafter so its failure
    /// window actually accumulates across calls.
    /// </summary>
    public ResiliencePipeline Get(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        return registry.GetOrAddPipeline(providerName, builder =>
            builder.AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions
            {
                SamplingDuration = TimeSpan.FromSeconds(60),
                FailureRatio = 0.5,

                // Eight calls before the ratio is trusted. Lower, and a single early failure
                // on a quiet provider would trip the breaker on a 100% failure rate of one.
                MinimumThroughput = 8,
                BreakDuration = TimeSpan.FromSeconds(30),

                // TimeoutRejectedException is what the HTTP pipeline's timeouts throw; a
                // provider that keeps timing out is as unavailable as one refusing connections.
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException
                        or TaskCanceledException
                        or TimeoutException
                        or TimeoutRejectedException),
            }));
    }

    public void Dispose() => registry.Dispose();
}
