using System.Text.RegularExpressions;

namespace RealtorAI.Tests.Prohibitions;

/// <summary>
/// T053 — Constitution Principle VIII.6, NON-NEGOTIABLE: no generative imagery.
///
/// <para>
/// The principle prohibits virtual staging, object removal and inpainting, sky or landscape
/// replacement, digital renovation, generative upscaling, AI "enhancement" that invents pixels,
/// and AI-generated video or avatars. Only non-generative operations on the agent's real photos
/// are permitted — resize, compress, convert, crop, rotate, reorder — and originals are always
/// preserved unchanged.
/// </para>
/// <para>
/// It also requires that <b>any dependency or endpoint capable of image or video generation is
/// prohibited in the codebase, enforced by an automated test</b>. This is that test.
/// </para>
///
/// <para>
/// <b>This test has already caught a real violation.</b> The solution shipped
/// <c>Microsoft.Extensions.AI.OpenAI</c>, which pulls <c>OpenAI</c> 2.14.0 — an SDK that
/// provides <c>ImageClient.GenerateImageAsync</c>, <c>GenerateImageEditAsync</c> (inpainting,
/// named explicitly in the prohibition), <c>GenerateImageVariationsAsync</c> and a
/// <c>VideoClient</c>. Nothing in the codebase called them, but the capability was one
/// <c>using</c> away from being reachable, which is precisely the state VIII.6 forbids. The
/// package was removed and the OpenAI-compatible protocol is now spoken directly over
/// <c>HttpClient</c>, parameterised entirely from the model registry.
/// </para>
///
/// <para>
/// <b>Why the abstraction packages are allowed.</b> <c>Microsoft.Extensions.AI.Abstractions</c>
/// declares <c>IImageGenerator</c>, and <c>Microsoft.Extensions.AI</c> adds middleware such as
/// <c>ImageGeneratingChatClient</c>. An interface with no implementation cannot generate an
/// image: the capability only becomes real when something implements or invokes it. Removing
/// those packages would also mean abandoning the vendor-neutral <c>IChatClient</c> abstraction
/// that Principle VIII.2 requires. So the line is drawn at capability rather than vocabulary —
/// the abstractions stay, and <see cref="NoSourceFileReferencesAGenerativeImageryApi"/> fails
/// if any of our own code so much as names one of those types.
/// </para>
/// </summary>
public sealed class NoGenerativeImageryTests
{
    /// <summary>
    /// Walks up from the test assembly to the repository root, so the test works from any
    /// working directory a runner might choose.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".specify")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }

    /// <summary>
    /// NuGet packages whose purpose, or whose dependency graph, includes generating or
    /// modifying image or video pixels with a model.
    /// </summary>
    private static readonly string[] ProhibitedPackagesList =
    {
        // Vendor SDKs that ship working image/video generation clients. The first entry is the
        // one that was actually present and removed.
        "Microsoft.Extensions.AI.OpenAI",
        "OpenAI",
        "Azure.AI.OpenAI",
        "Betalgo.OpenAI",
        "OpenAI-DotNet",
        "Azure.AI.Vision.ImageAnalysis",
        "StabilityAI",
        "Replicate",
        "LeonardoAi",
        "Fal.Ai",
        "RunwayML",
        "ElevenLabs",
        "HuggingFace.Diffusers",
    };

    public static TheoryData<string> ProhibitedPackages { get; } = ToTheoryData(ProhibitedPackagesList);

    /// <summary>
    /// Type and member names that only appear when code is generating or editing pixels.
    ///
    /// <para>
    /// Deliberately narrow. Words like "Image" or "Generate" on their own appear throughout
    /// legitimate photo handling — <c>ImageNormalizer</c>, <c>ContentGenerated</c> — and a test
    /// that flagged them would be turned off within a week. These are the specific API names.
    /// </para>
    /// </summary>
    private static readonly string[] ProhibitedApisList =
    {
        "IImageGenerator",
        "ImageGeneratingChatClient",
        "ImageGeneratorBuilder",
        "ImageGenerationRequest",
        "ImageGenerationOptions",
        "HostedImageGenerationTool",
        "GenerateImageAsync",
        "GenerateImagesAsync",
        "GenerateImageEditAsync",
        "GenerateImageEditsAsync",
        "GenerateImageVariationAsync",
        "GenerateImageVariationsAsync",
        "ITextToSpeechClient",
        "VideoClient",
        "GetImageClient",
        "GetVideoClient",
    };

    public static TheoryData<string> ProhibitedApis { get; } = ToTheoryData(ProhibitedApisList);

    /// <summary>
    /// Python packages that generate or repaint pixels. Checked against the sidecar's
    /// requirement files, which are the only place its dependencies are declared.
    /// </summary>
    private static readonly string[] ProhibitedPythonPackagesList =
    {
        "diffusers",
        "stable-diffusion",
        "stablediffusion",
        "compel",
        "invisible-watermark",
        "controlnet",
        "controlnet-aux",
        "gfpgan",
        "basicsr",
        "realesrgan",
        "codeformer",
        "rembg",
        "lama-cleaner",
        "simple-lama-inpainting",
        "imaginairy",
    };

    public static TheoryData<string> ProhibitedPythonPackages { get; } =
        ToTheoryData(ProhibitedPythonPackagesList);

    private static TheoryData<string> ToTheoryData(string[] values)
    {
        var data = new TheoryData<string>();

        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }

    /// <summary>
    /// The repository walk is cached across cases. Without this, each of the ~47 theory cases
    /// re-enumerated the whole tree, which took half a minute — and a slow build gate is a
    /// gate someone eventually switches off.
    /// </summary>
    private static readonly Lazy<Dictionary<string, FileInfo[]>> FilesByExtension =
        new(BuildFileIndex);

    private static Dictionary<string, FileInfo[]> BuildFileIndex()
    {
        var root = RepositoryRoot();

        var skipped = new[]
        {
            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + ".venv" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + ".next" + Path.DirectorySeparatorChar,
        };

        return root
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !skipped.Any(segment =>
                file.FullName.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            // This file names every prohibited API in order to search for them, so it would
            // otherwise flag itself.
            .Where(file => file.Name != nameof(NoGenerativeImageryTests) + ".cs")
            .GroupBy(file => file.Name.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase)
                ? "packages.lock.json"
                : file.Extension.ToLowerInvariant())
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo root, params string[] extensions)
    {
        return extensions.SelectMany(extension =>
            FilesByExtension.Value.TryGetValue(extension, out var files) ? files : []);
    }

    // ═════════════════════════ Dependencies ═════════════════════════

    [Theory]
    [MemberData(nameof(ProhibitedPackages))]
    public void NoProjectReferencesAGenerativeImageryPackage(string packageId)
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var project in SourceFiles(root, ".csproj"))
        {
            var text = File.ReadAllText(project.FullName);

            // Matches Include="<id>" exactly, so a package whose name merely contains a
            // prohibited one as a prefix is not flagged by accident.
            if (Regex.IsMatch(
                    text,
                    "Include\\s*=\\s*\"" + Regex.Escape(packageId) + "\"",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
            {
                offenders.Add(Path.GetRelativePath(root.FullName, project.FullName));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"'{packageId}' provides image or video generation and is prohibited by "
            + $"Constitution VIII.6 (NON-NEGOTIABLE). Referenced by: {string.Join(", ", offenders)}. "
            + "If a model-served capability is genuinely needed, it belongs in the AI model "
            + "registry behind the VIII.4 enablement gate — not as a direct dependency.");
    }

    /// <summary>
    /// The lock files are checked as well as the project files, because a prohibited package
    /// can arrive transitively without anyone writing its name in a csproj — which is exactly
    /// how <c>OpenAI</c> 2.14.0 was present.
    /// </summary>
    [Fact]
    public void NoTransitiveDependencyProvidesImageOrVideoGeneration()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var assets in SourceFiles(root, "packages.lock.json"))
        {
            var text = File.ReadAllText(assets.FullName);

            foreach (var packageId in ProhibitedPackagesList)
            {
                if (text.Contains("\"" + packageId + "\"", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(
                        $"{packageId} in {Path.GetRelativePath(root.FullName, assets.FullName)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Prohibited image/video generation packages reached the dependency graph "
            + $"transitively: {string.Join("; ", offenders)}");
    }

    // ═════════════════════════ Our own code ═════════════════════════

    [Theory]
    [MemberData(nameof(ProhibitedApis))]
    public void NoSourceFileReferencesAGenerativeImageryApi(string api)
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root, ".cs", ".ts", ".tsx", ".py"))
        {
            var text = File.ReadAllText(file.FullName);

            if (Regex.IsMatch(
                    text,
                    "\\b" + Regex.Escape(api) + "\\b",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)))
            {
                offenders.Add(Path.GetRelativePath(root.FullName, file.FullName));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"'{api}' is an image, video or speech generation API, prohibited by Constitution "
            + $"VIII.6 (NON-NEGOTIABLE). Found in: {string.Join(", ", offenders)}. Permitted "
            + "operations on an agent's photos are non-generative only: resize, compress, "
            + "convert, crop, rotate, and ordering.");
    }

    // ═════════════════════════ The sidecar ═════════════════════════

    [Theory]
    [MemberData(nameof(ProhibitedPythonPackages))]
    public void SidecarDoesNotDependOnAGenerativeImageryPackage(string packageName)
    {
        var root = RepositoryRoot();
        var sidecar = new DirectoryInfo(
            Path.Combine(root.FullName, "RealtorAI_BackEnd", "vision-sidecar"));

        Assert.True(sidecar.Exists, $"The vision sidecar was not found at {sidecar.FullName}.");

        var offenders = new List<string>();

        // Both the .in files (what we asked for) and the .txt files (what pip resolved), since
        // a generative package can arrive as a transitive dependency of an innocent one.
        foreach (var file in sidecar.EnumerateFiles("requirements*", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(file.FullName))
            {
                var declaration = line.Split('#')[0].Trim();

                if (declaration.Length == 0)
                {
                    continue;
                }

                // Compare the distribution name only, ignoring any version specifier.
                var name = declaration.Split(['=', '<', '>', '!', '~', '[', ';', ' '])[0].Trim();

                if (string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{file.Name}: {declaration}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"The vision sidecar declares '{packageName}', which generates or repaints image "
            + $"pixels — prohibited by Constitution VIII.6. Found: {string.Join("; ", offenders)}. "
            + "The sidecar measures and embeds photos; it never produces them.");
    }

    /// <summary>
    /// The sidecar exposes no generation route. Its contract is <c>/health</c>, <c>/quality</c>
    /// and <c>/embed</c> — measure and embed, never produce.
    /// </summary>
    [Fact]
    public void SidecarExposesNoGenerationEndpoint()
    {
        var root = RepositoryRoot();
        var sidecarApp = new DirectoryInfo(
            Path.Combine(root.FullName, "RealtorAI_BackEnd", "vision-sidecar", "app"));

        Assert.True(sidecarApp.Exists, $"Sidecar app directory not found at {sidecarApp.FullName}.");

        var routes = new List<string>();

        foreach (var file in sidecarApp.EnumerateFiles("*.py", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file.FullName);

            foreach (var match in Regex.Matches(
                    text,
                    "@app\\.(?:get|post|put|patch|delete)\\(\\s*[\"']([^\"']+)[\"']",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5))
                .Cast<Match>())
            {
                routes.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(routes);

        var allowed = new[] { "/health", "/quality", "/embed" };

        var unexpected = routes.Where(route => !allowed.Contains(route, StringComparer.Ordinal)).ToList();

        Assert.True(
            unexpected.Count == 0,
            "The sidecar exposes routes outside its measure-and-embed contract: "
            + string.Join(", ", unexpected)
            + ". Adding a generation route would violate Constitution VIII.6; adding any other "
            + "route needs contracts/vision-sidecar.md updated first.");
    }

    // ═════════════════════════ The test itself ═════════════════════════

    /// <summary>
    /// Guards against the scan silently covering nothing — if the file walk broke, every
    /// assertion above would pass vacuously and the suite would report a non-negotiable
    /// guarantee it had stopped checking.
    /// </summary>
    [Fact]
    public void TheScanActuallyReachesTheCodebase()
    {
        var root = RepositoryRoot();

        var csharp = SourceFiles(root, ".cs").Count();
        var projects = SourceFiles(root, ".csproj").Count();
        var python = SourceFiles(root, ".py").Count();

        Assert.True(csharp > 20, $"Only {csharp} C# files were scanned; the walk is not working.");
        Assert.True(projects >= 10, $"Only {projects} project files were scanned.");
        Assert.True(python > 2, $"Only {python} Python files were scanned.");
    }
}
