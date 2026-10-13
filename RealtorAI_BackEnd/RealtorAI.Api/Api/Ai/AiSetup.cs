using Libs.Ai;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Ai;

/// <summary>
/// Owns the AI response cache instance.
///
/// <para>
/// A dedicated cache rather than the shared one, for the same reasons as the membership cache:
/// AI responses are large relative to other cached values, so they would dominate a shared size
/// budget, and a size limit set here does not force every other caller in the application to
/// start declaring entry sizes.
/// </para>
/// </summary>
internal sealed class AiResponseCacheStore : IDisposable
{
    /// <summary>
    /// Sized in kilobytes (see <c>CostTracker.CacheResponse</c>), so roughly 64 MB of model
    /// responses. Eviction costs a repeat call, never a wrong answer.
    /// </summary>
    private const long MaxSizeKilobytes = 65_536;

    internal IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions
    {
        SizeLimit = MaxSizeKilobytes,
    });

    public void Dispose() => (Cache as IDisposable)?.Dispose();
}

internal static class AiSetup
{
    /// <summary>
    /// Registers the AI platform (T044–T049).
    /// </summary>
    internal static IServiceCollection AddAiPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // ───────────── Transport ─────────────
        //
        // The base URL and the name of the environment variable holding the token come from
        // configuration. The token itself does not: only its variable name is written down, so
        // a credential never lands in a settings file or a container image layer.
        var transport = new AiTransportOptions(
            configuration["Ai:HuggingFace:RouterBaseUrl"]
                ?? throw new InvalidOperationException(
                    "Ai:HuggingFace:RouterBaseUrl is not configured. It is the default base URL "
                    + "for providers whose registry entry declares no endpoint of its own."),
            configuration["Ai:HuggingFace:ApiKeyEnvVar"] ?? "HF_TOKEN");

        services.AddAiHttpClient(transport);

        // ───────────── Prompts ─────────────
        //
        // Loaded once at startup. Prompt text is immutable for the life of the process, which
        // is what makes a recorded prompt hash meaningful (VIII.5) — a prompt that could be
        // reloaded mid-process could serve two different texts under one version number.
        services.AddSingleton<IPromptRegistry>(
            PromptRegistry.Load(ResolvePromptsDirectory(configuration, environment)));

        // ───────────── Stores ─────────────
        services.AddScoped<IModelRegistryStore, EfModelRegistryStore>();
        services.AddScoped<IPromptVersionStore, EfPromptVersionStore>();
        services.AddScoped<IAiUsageStore, EfAiUsageStore>();

        // ───────────── Platform ─────────────
        services.AddSingleton<AiResponseCacheStore>();

        // Singleton so each provider's failure window accumulates across requests. A scoped
        // breaker would reset every request and never open.
        services.AddSingleton<ProviderCircuitBreakers>();

        services.AddScoped<IModelRegistry, ModelRegistry>();
        services.AddSingleton<IChatClientFactory, HttpChatClientFactory>();

        var defaultBudget = new AiBudget(
            configuration.GetValue("Ai:Budget:DefaultTokensPerListing", 200_000),
            configuration.GetValue("Ai:Budget:DefaultImagesPerListing", 120));

        services.AddScoped<ICostTracker>(provider => new CostTracker(
            provider.GetRequiredService<IAiUsageStore>(),
            provider.GetRequiredService<AiResponseCacheStore>().Cache,
            defaultBudget));

        services.AddScoped<IAiService, AiService>();

        return services;
    }

    /// <summary>
    /// Finds the prompts directory.
    ///
    /// <para>
    /// Probing is necessary because the directory sits beside the host project rather than
    /// inside it (plan.md puts <c>prompts/</c> as a sibling of <c>Api/</c>, shared with the
    /// evaluation harness), while <see cref="IHostEnvironment.ContentRootPath"/> is the host
    /// project directory under <c>dotnet run</c> and the output directory once published.
    /// Hard-coding either one works in exactly one of those situations.
    /// </para>
    /// <para>
    /// This was not theoretical: the first wiring resolved only
    /// <c>{ContentRoot}/prompts</c> and the host refused to start, reporting the recorded
    /// <c>photo.analysis v1</c> as missing. The drift check was right — from where it looked,
    /// the file genuinely was not there.
    /// </para>
    /// </summary>
    private static string ResolvePromptsDirectory(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // An explicit setting wins, so a deployment with an unusual layout needs no code change.
        if (configuration["Ai:PromptsPath"] is { Length: > 0 } configured)
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured);
        }

        string[] candidates =
        [
            // Published output, where the csproj copies them.
            Path.Combine(environment.ContentRootPath, "prompts"),

            // Development: beside the host project, as the plan lays it out.
            Path.Combine(environment.ContentRootPath, "..", "prompts"),
        ];

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // Returning the first candidate rather than throwing: an absent directory is a valid
        // state when no prompt version has been recorded yet, and the startup verification
        // reports it precisely when it actually matters — when a recorded version has no file.
        return Path.GetFullPath(candidates[0]);
    }

    /// <summary>
    /// Verifies prompt hashes against the database before serving traffic (VIII.5).
    ///
    /// <para>
    /// Startup, not per call. Drift invalidates records that are <i>already written</i>, so it
    /// needs to be a deployment failure someone sees immediately rather than something
    /// discovered when the first request happens to hit the affected task.
    /// </para>
    /// <para>
    /// A database that is unreachable at startup is not treated as drift — that would turn a
    /// transient connectivity problem into a refusal to boot. Only an actual hash disagreement
    /// stops the host.
    /// </para>
    /// </summary>
    internal static async Task VerifyPromptIntegrityAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();

        var prompts = scope.ServiceProvider.GetRequiredService<IPromptRegistry>();
        var store = scope.ServiceProvider.GetRequiredService<IPromptVersionStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AiSetup));

        try
        {
            await prompts.VerifyAgainstRecordedHashesAsync(store, CancellationToken.None);
        }
        catch (Npgsql.NpgsqlException exception)
        {
            AiSetupLog.PromptVerificationUnavailable(logger, exception);
        }
    }
}

internal static partial class AiSetupLog
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Could not verify prompt hashes against the database at startup because it "
            + "was unreachable. Prompt integrity (Principle VIII.5) is therefore UNVERIFIED for "
            + "this process. A hash mismatch would still be a defect; it has simply not been "
            + "checked.")]
    internal static partial void PromptVerificationUnavailable(ILogger logger, Exception exception);
}
