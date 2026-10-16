using Api.Endpoints;
using Modules.Listings;
using Modules.Listings.PropertyData;
using Modules.Media;

namespace Api.Media;

/// <summary>
/// Registers photo storage, normalization, upload bounds and public-record pre-fill (T057, T058,
/// T060, T077, T078).
/// </summary>
internal static class MediaSetup
{
    internal static IServiceCollection AddMedia(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ───────────── Normalization ─────────────
        services.AddSingleton(new ImageNormalizer(new NormalizationOptions(
            Required<int>(configuration, "Storage:NormalizedLongEdgePx"),
            Required<int>(configuration, "Storage:NormalizedJpegQuality"),
            Required<long>(configuration, "Storage:MaxSourcePixels"))));

        // ───────────── Storage ─────────────
        //
        // The options are built on first use rather than at startup. The secret key comes from
        // an environment variable, and failing the whole host when it is absent would take down
        // every endpoint that never touches storage; failing the first storage call does not,
        // and says exactly what is missing.
        var storageApiBase = new Uri(
            (configuration["Supabase:Url"]
                ?? throw new InvalidOperationException("Supabase:Url is not configured."))
            .TrimEnd('/') + "/storage/v1/");

        var keyVariable = configuration["Storage:ServiceKeyEnvVar"] ?? "SUPABASE_SECRET_KEY";

        services.AddSingleton(_ => new StorageOptions(
            storageApiBase,
            Environment.GetEnvironmentVariable(keyVariable) is { Length: > 0 } key
                ? key
                : throw new InvalidOperationException(
                    $"Photo storage needs the Supabase secret key in the {keyVariable} "
                    + "environment variable. Locally it is printed by `npx supabase status`. "
                    + "It is a server-only secret and must never be given a NEXT_PUBLIC_ name."),
            Required<string>(configuration, "Storage:OriginalsBucket"),
            Required<string>(configuration, "Storage:NormalizedBucket"),
            TimeSpan.FromSeconds(Required<int>(configuration, "Storage:SignedUrlSeconds"))));

        services.AddHttpClient<IPhotoStorage, StorageService>();

        // ───────────── Public-record pre-fill ─────────────
        //
        // No source is configured in Slice A (research R5). FR-008's unconfigured behaviour is
        // the whole behaviour until one is chosen.
        services.AddSingleton<IPropertyDataProvider, NullPropertyDataProvider>();
        services.AddSingleton<PrefillService>();

        // ───────────── Upload bounds ─────────────
        services.AddSingleton(new PhotoUploadOptions(Required<long>(configuration, "Storage:MaxUploadBytes")));

        return services;
    }

    private static T Required<T>(IConfiguration configuration, string key) =>
        configuration.GetValue<T?>(key) is { } value
            ? value
            : throw new InvalidOperationException($"{key} is not configured.");
}
