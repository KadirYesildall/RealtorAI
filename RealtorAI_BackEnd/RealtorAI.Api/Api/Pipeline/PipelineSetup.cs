using Pipeline;
using Pipeline.Steps;

namespace Api.Pipeline;

/// <summary>
/// Registers the analysis pipeline: its steps, their persistence, the vision sidecar, and the
/// thresholds and estimates they run with (US2, and content generation for US3).
/// </summary>
internal static class PipelineSetup
{
    internal static IServiceCollection AddAnalysisPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(new QualityThresholds(
            Required<double>(configuration, "Pipeline:Quality:BlurScore"),
            Required<double>(configuration, "Pipeline:Quality:DarkLuminance"),
            Required<double>(configuration, "Pipeline:Quality:BrightLuminance"),
            Required<double>(configuration, "Pipeline:Quality:ClippedRatio"),
            Required<double>(configuration, "Pipeline:Quality:TiltDegrees"),
            Required<double>(configuration, "Pipeline:Quality:NearDuplicateSimilarity")));

        services.AddSingleton(new PhotoAnalysisOptions(Required<int>(configuration, "Pipeline:Analysis:EstimatedTokensPerPhoto")));
        services.AddSingleton(new ContentGenerationOptions(Required<int>(configuration, "Pipeline:Content:EstimatedTokensPerCall")));

        services.AddHttpClient<IVisionSidecar, HttpVisionSidecar>(client =>
        {
            var baseUrl = configuration["VisionSidecar:BaseUrl"]
                ?? throw new InvalidOperationException("VisionSidecar:BaseUrl is not configured.");

            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

            // Quality and embedding of one ~1280px image; a sidecar slower than this is treated
            // as unavailable, which fails only the step that needed it.
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddScoped<IPipelineStore, EfPipelineStore>();
        services.AddScoped<IContentStore, EfContentStore>();
        services.AddScoped<PipelineReporter>();

        // Registered in execution order; the runner orders them by step regardless.
        services.AddScoped<IPipelineStep, NormalizeStep>();
        services.AddScoped<IPipelineStep, TechnicalQualityStep>();
        services.AddScoped<IPipelineStep, EmbeddingsStep>();
        services.AddScoped<IPipelineStep, PhotoAnalysisStep>();
        services.AddScoped<IPipelineStep, CoverAndOrderStep>();
        services.AddScoped<IPipelineStep, ContentGenerationStep>();

        services.AddScoped<PipelineRunner>();

        return services;
    }

    private static T Required<T>(IConfiguration configuration, string key) =>
        configuration.GetValue<T?>(key) is { } value
            ? value
            : throw new InvalidOperationException($"{key} is not configured.");
}
