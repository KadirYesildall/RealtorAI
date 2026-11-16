using System.Globalization;
using Libs.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Media;

namespace AiEval;

/// <summary>
/// The production pieces a model is evaluated with, built from the API host's own configuration:
/// prompts, chat-client construction (base URL, credential, resilience) and image normalization.
///
/// <para>
/// <b>Same configuration, not a parallel one.</b> The harness reads the host's
/// <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c> and then the process
/// environment — the providers <c>WebApplication.CreateBuilder</c> layers for the host, in the
/// same order, so an environment variable such as <c>Ai__HuggingFace__RouterBaseUrl</c>
/// overrides a file value here exactly as it does there. Every key is read through the constant
/// the host itself uses (<see cref="AiTransportOptions"/>, <see cref="PromptRegistry"/>,
/// <see cref="NormalizationOptions"/>), and the chat clients come from
/// <see cref="AiHttpClientSetup.AddAiHttpClient"/> and <see cref="HttpChatClientFactory"/>,
/// which read the credential from the environment variable those keys name, exactly as the
/// host's clients do. No key, variable, vendor, model or endpoint is introduced here.
/// </para>
/// <para>
/// The environment name follows the host's rule: <c>ASPNETCORE_ENVIRONMENT</c>, then
/// <c>DOTNET_ENVIRONMENT</c>, then <c>Production</c>. Locally the host runs as
/// <c>Development</c> through its launch profile, so a harness run against the local stack sets
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> to read the same file.
/// </para>
/// </summary>
internal sealed class EvalEnvironment : IDisposable
{
    private readonly ServiceProvider services;

    private EvalEnvironment(
        ServiceProvider services,
        IPromptRegistry prompts,
        IChatClientFactory clients,
        ImageNormalizer normalizer,
        AiTransportOptions transport,
        string promptsDirectory)
    {
        this.services = services;
        Prompts = prompts;
        Clients = clients;
        Normalizer = normalizer;
        Transport = transport;
        PromptsDirectory = promptsDirectory;
    }

    internal IPromptRegistry Prompts { get; }

    internal IChatClientFactory Clients { get; }

    internal ImageNormalizer Normalizer { get; }

    /// <summary>The default base URL and the name of the credential's environment variable.</summary>
    internal AiTransportOptions Transport { get; }

    internal string PromptsDirectory { get; }

    /// <summary>
    /// Whether the credential's environment variable is set. Reported, never required: a
    /// self-hosted endpoint may need no token, and the host does not require one either.
    /// </summary>
    internal bool CredentialPresent =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Transport.ApiKeyEnvironmentVariable));

    /// <summary>
    /// Reads the host's configuration from <paramref name="apiDirectory"/> (the API host project
    /// directory, or its published output) and builds the environment from it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required setting is missing or malformed.</exception>
    internal static EvalEnvironment FromHost(string apiDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiDirectory);

        if (!File.Exists(Path.Combine(apiDirectory, "appsettings.json")))
        {
            throw new InvalidOperationException(
                $"'{apiDirectory}' has no appsettings.json, so it is not the API host directory. "
                + "Pass the host's directory with --api-dir.");
        }

        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is { Length: > 0 } aspnet
            ? aspnet
            : Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is { Length: > 0 } dotnet
                ? dotnet
                : "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(apiDirectory))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return Create(configuration, apiDirectory);
    }

    /// <summary>Builds the environment from an already-assembled configuration.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="contentRoot">The host's content root, against which the prompts directory is resolved.</param>
    /// <exception cref="InvalidOperationException">A required setting is missing or malformed.</exception>
    internal static EvalEnvironment Create(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var transport = AiTransportOptions.FromConfiguration(
            configuration[AiTransportOptions.DefaultBaseUrlKey],
            configuration[AiTransportOptions.ApiKeyEnvironmentVariableKey]);

        var normalization = new NormalizationOptions(
            Required(configuration, NormalizationOptions.LongEdgePxKey, int.Parse),
            Required(configuration, NormalizationOptions.JpegQualityKey, int.Parse),
            Required(configuration, NormalizationOptions.MaxSourcePixelsKey, long.Parse));

        var promptsDirectory = PromptRegistry.ResolveDirectory(
            configuration[PromptRegistry.PathConfigurationKey],
            Path.GetFullPath(contentRoot));

        var prompts = PromptRegistry.Load(promptsDirectory);

        var collection = new ServiceCollection();
        collection.AddAiHttpClient(transport);
        var services = collection.BuildServiceProvider();

        try
        {
            return new EvalEnvironment(
                services,
                prompts,
                new HttpChatClientFactory(services.GetRequiredService<IHttpClientFactory>()),
                new ImageNormalizer(normalization),
                transport,
                promptsDirectory);
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds the API host directory from <paramref name="startDirectory"/> upwards: the first
    /// ancestor with an <c>Api/appsettings.json</c>. That is where <c>dotnet run --project
    /// tools/AiEval</c> sits relative to the host in this repository.
    /// </summary>
    /// <returns>The host directory, or null when none is found.</returns>
    internal static string? FindApiDirectory(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Api");

            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>An invoker bound to <paramref name="target"/>, using this environment's pieces.</summary>
    internal EvalModelInvoker CreateInvoker(RegistryTarget target) =>
        new(target, Prompts, Clients, Normalizer);

    public void Dispose() => services.Dispose();

    private static T Required<T>(
        IConfiguration configuration,
        string key,
        Func<string, IFormatProvider, T> parse)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"{key} is not configured.");
        }

        try
        {
            return parse(raw, CultureInfo.InvariantCulture);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{key} is '{raw}', which is not a number.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException($"{key} is '{raw}', which is out of range.", exception);
        }
    }
}
