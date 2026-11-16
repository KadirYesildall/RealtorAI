using System.Globalization;

namespace AiEval;

// T054 — AI evaluation harness entry point (Principle VIII.4, FR-059, quickstart.md §6).
//
//   aieval --task <task|all>
//       Prints each task's acceptance criteria and whether a suite exists to measure them.
//
//   aieval --registry-id <uuid> [--write --actor <profile-uuid>]
//       Evaluates that registry entry's model with its task's suite. With --write, records a
//       pass or a measured failure in ai_model_registry and the audit trail.
//
//   aieval --fetch-eval-sets [--cache <dir>]
//       Downloads the files evaluation sets reference but do not commit (the photo set's
//       images) into the local cache, verifying each against the manifest's SHA-256. Safe to run
//       again. The suites read the same cache: REALTORAI_EVAL_CACHE, else local application data.
//
// Options: --connection <npgsql> (or REALTORAI_EVAL_POSTGRES), --budget-tokens <n>,
//          --budget-images <n>, --api-dir <path>.
//
// A suite calls the model with the API host's own configuration (EvalEnvironment): its
// appsettings files under --api-dir (found automatically in this repository), then the process
// environment, read under ASPNETCORE_ENVIRONMENT as the host is.
//
// Exit codes: 0 passed (or criteria listed, or every set file fetched and verified), 1 failed,
//             2 incomplete (or some set files could not be fetched), 64 usage error,
//             70 could not record, 78 configuration or evaluation-set error (nothing measured).

/// <summary>
/// Entry point. An explicit class in the <c>AiEval</c> namespace rather than top-level
/// statements: those compile to a global <c>Program</c>, which collides with the API host's
/// <c>Program</c> in any test assembly that references both.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        const int ExitPassed = 0;
        const int ExitFailed = 1;
        const int ExitIncomplete = 2;
        const int ExitUsage = 64;
        const int ExitRecordFailed = 70;
        const int ExitConfiguration = 78;

        HarnessOptions options;

        try
        {
            options = HarnessOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return ExitUsage;
        }

        if (options.FetchEvalSets)
        {
            try
            {
                return await FetchEvalSetsAsync(options.CacheDirectory) ? ExitPassed : ExitIncomplete;
            }
            catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException)
            {
                Console.Error.WriteLine(exception.Message);
                return ExitConfiguration;
            }
        }

        var catalog = new MetricCatalog(options.Budget);

        if (options.RegistryId is not { } registryId)
        {
            var tasks = options.Task == "all" ? catalog.Tasks.ToList() : [options.Task!];

            foreach (var task in tasks)
            {
                Report.PrintCriteria(task, catalog.For(task), EvalSuites.Registered.ContainsKey(task));
            }

            return ExitPassed;
        }

        var writer = new RegistryResultWriter(options.ConnectionString!);
        var target = await writer.LoadAsync(registryId, CancellationToken.None);

        if (target is null)
        {
            Console.Error.WriteLine($"No registry entry has id {registryId}.");
            return ExitUsage;
        }

        if (options.Task is { } requested && requested != "all" && requested != target.TaskKey)
        {
            Console.Error.WriteLine(
                $"Registry entry {registryId} serves '{target.TaskKey}', not '{requested}'.");
            return ExitUsage;
        }

        SuiteRun? run = null;

        if (EvalSuites.Registered.TryGetValue(target.TaskKey, out var suite))
        {
            try
            {
                run = await RunSuiteAsync(suite, target, options.ApiDirectory);
            }
            catch (Exception exception) when (IsSetupFailure(exception))
            {
                // Nothing was measured, so there is nothing to judge and nothing to record.
                Console.Error.WriteLine(exception.Message);
                return ExitConfiguration;
            }
        }

        var evaluatedAt = DateTimeOffset.UtcNow;
        var outcome = EvalVerdict.Evaluate(catalog.For(target.TaskKey), run, target.TaskKey);
        var (verdict, results) = (outcome.Verdict, outcome.Results);

        Report.PrintResult(target, outcome);

        if (options.Write)
        {
            if (verdict == Verdict.Incomplete)
            {
                Console.WriteLine("Nothing recorded: an incomplete evaluation is neither a pass nor a failure.");
            }
            else
            {
                try
                {
                    await writer.WriteAsync(
                        target,
                        verdict,
                        results,
                        options.ActorId!.Value,
                        evaluatedAt,
                        CancellationToken.None);

                    Console.WriteLine(
                        $"Recorded eval_status='{verdict.ToString().ToLowerInvariant()}' on {target.Id} "
                        + "and audited it as registry_changed.");
                }
                catch (InvalidOperationException exception)
                {
                    Console.Error.WriteLine(exception.Message);
                    return ExitRecordFailed;
                }
            }
        }

        return verdict switch
        {
            Verdict.Passed => ExitPassed,
            Verdict.Failed => ExitFailed,
            _ => ExitIncomplete,
        };
    }

    /// <summary>
    /// Builds the host-configured environment and runs <paramref name="suite"/> against
    /// <paramref name="target"/>. Built only when a suite exists, so listing criteria or judging
    /// a task with no suite never depends on model configuration being present.
    /// </summary>
    private static async Task<SuiteRun> RunSuiteAsync(IEvalSuite suite, RegistryTarget target, string? apiDirectory)
    {
        var hostDirectory = apiDirectory
            ?? EvalEnvironment.FindApiDirectory(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Could not find the API host directory (Api/appsettings.json) above "
                + $"{AppContext.BaseDirectory}. Pass it with --api-dir.");

        using var environment = EvalEnvironment.FromHost(hostDirectory);
        var model = environment.CreateInvoker(target);

        Report.PrintSetup(environment, model);

        return await suite.RunAsync(model, CancellationToken.None);
    }

    /// <summary>
    /// Fetches every shipped set's uncommitted files into the cache. The photo set (T156) is the
    /// only one today; the Fair Housing set's passages are in its manifest and need nothing.
    /// </summary>
    /// <returns>True when every file is in the cache and verified.</returns>
    private static async Task<bool> FetchEvalSetsAsync(string? cacheDirectory)
    {
        var cache = new EvalSetCache(cacheDirectory ?? EvalSetCache.DefaultRoot());
        var set = EvalSet.LoadShipped(Libs.Ai.AiTaskKey.PhotoAnalysis, PhotoEval.SetFileName);
        var files = PhotoEval.RemoteFiles(set);

        Console.WriteLine(
            $"Fetching {set.Name} ({files.Count.ToString(CultureInfo.InvariantCulture)} files) into "
            + Path.Combine(cache.Root, set.TaskKey));

        using var http = EvalSetFetcher.CreateClient();
        var result = await EvalSetFetcher.FetchAsync(http, cache, set.TaskKey, files, Console.Out);

        Console.WriteLine(
            $"{set.Name}: {result.Verified.ToString(CultureInfo.InvariantCulture)} of {files.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"verified ({result.AlreadyPresent.Count.ToString(CultureInfo.InvariantCulture)} already cached, "
            + $"{result.Downloaded.Count.ToString(CultureInfo.InvariantCulture)} downloaded), "
            + $"{result.Failed.Count.ToString(CultureInfo.InvariantCulture)} not available.");

        if (result.Failed.Count > 0)
        {
            Console.WriteLine("The photo suite reports Incomplete until every file is present and verified.");
        }

        if (cacheDirectory is not null
            && !string.Equals(cache.Root, new EvalSetCache(EvalSetCache.DefaultRoot()).Root, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"The suites read {EvalSetCache.DirectoryEnvironmentVariable} or the default cache; set "
                + $"{EvalSetCache.DirectoryEnvironmentVariable}={cache.Root} before evaluating to use this one.");
        }

        return result.Failed.Count == 0;
    }

    /// <summary>
    /// Failures that mean the evaluation could not be set up or its data could not be read -
    /// configuration, prompts, an evaluation-set manifest, an evaluation photo - as opposed to a
    /// bug, which should surface with its stack trace.
    /// </summary>
    private static bool IsSetupFailure(Exception exception) =>
        exception is InvalidOperationException
            or InvalidDataException
            or FileNotFoundException
            or DirectoryNotFoundException
            or Libs.Ai.ModelUnavailableException
            or Libs.Ai.PromptDriftException
            or Libs.Ai.StructuredOutputException
            or Modules.Media.UnsupportedImageException;
}

/// <summary>Parsed command line.</summary>
internal sealed record HarnessOptions(
    string? Task,
    Guid? RegistryId,
    bool Write,
    Guid? ActorId,
    string? ConnectionString,
    EvalBudget Budget,
    string? ApiDirectory,
    bool FetchEvalSets = false,
    string? CacheDirectory = null)
{
    internal const string Usage =
        """
        Usage:
          aieval --task <photo.analysis|photo.embedding|listing.content|compliance.judge|all>
          aieval --registry-id <uuid> [--task <task>] [--write --actor <profile-uuid>]
          aieval --fetch-eval-sets [--cache <dir>]
        Options:
          --connection <npgsql connection string>   (or REALTORAI_EVAL_POSTGRES)
          --budget-tokens <n>   --budget-images <n>   (default 200000 / 120)
          --api-dir <path>   API host directory whose configuration a suite uses
                             (default: found above the harness binary)
          --cache <dir>      where --fetch-eval-sets stores set files (default:
                             REALTORAI_EVAL_CACHE, else local application data)
        """;

    internal static HarnessOptions Parse(IReadOnlyList<string> args)
    {
        string? task = null;
        Guid? registryId = null;
        var write = false;
        Guid? actorId = null;
        var connection = Environment.GetEnvironmentVariable("REALTORAI_EVAL_POSTGRES");
        var tokens = EvalBudget.Default.TokensPerListing;
        var images = EvalBudget.Default.ImagesPerListing;
        string? apiDirectory = null;
        var fetchEvalSets = false;
        string? cacheDirectory = null;

        for (var index = 0; index < args.Count; index++)
        {
            string Value() => index + 1 < args.Count
                ? args[++index]
                : throw new ArgumentException($"{args[index]} needs a value.");

            switch (args[index])
            {
                case "--task":
                    task = Value();
                    break;
                case "--registry-id":
                    registryId = ParseGuid(Value(), "--registry-id");
                    break;
                case "--write":
                    write = true;
                    break;
                case "--actor":
                    actorId = ParseGuid(Value(), "--actor");
                    break;
                case "--connection":
                    connection = Value();
                    break;
                case "--budget-tokens":
                    tokens = ParsePositive(Value(), "--budget-tokens");
                    break;
                case "--budget-images":
                    images = ParsePositive(Value(), "--budget-images");
                    break;
                case "--api-dir":
                    apiDirectory = Value();
                    break;
                case "--fetch-eval-sets":
                    fetchEvalSets = true;
                    break;
                case "--cache":
                    cacheDirectory = Value();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        var budget = new EvalBudget(tokens, images);

        if (fetchEvalSets)
        {
            // Fetching measures nothing and records nothing, so it takes no model, task or
            // database: mixing it with an evaluation would hide which of the two an exit code
            // reports.
            if (task is not null || registryId is not null || write)
            {
                throw new ArgumentException("--fetch-eval-sets runs on its own; evaluate in a separate run.");
            }

            return new HarnessOptions(null, null, false, null, connection, budget, apiDirectory, FetchEvalSets: true, cacheDirectory);
        }

        if (cacheDirectory is not null)
        {
            throw new ArgumentException(
                $"--cache applies to --fetch-eval-sets. A suite reads {EvalSetCache.DirectoryEnvironmentVariable} or the default cache.");
        }

        if (task is not null && task != "all" && !new MetricCatalog(budget).IsKnownTask(task))
        {
            throw new ArgumentException($"Unknown task '{task}'.");
        }

        if (registryId is null)
        {
            if (task is null)
            {
                throw new ArgumentException("Give --task to list criteria, or --registry-id to evaluate a model.");
            }

            if (write)
            {
                throw new ArgumentException("--write records a verdict against a model, so it needs --registry-id.");
            }
        }
        else if (string.IsNullOrWhiteSpace(connection))
        {
            // No default: a harness that silently picked a database could record a verdict in
            // the wrong one.
            throw new ArgumentException(
                "Evaluating a registry entry needs --connection or REALTORAI_EVAL_POSTGRES.");
        }

        if (write && actorId is null)
        {
            throw new ArgumentException(
                "--write needs --actor: every registry change is attributed to a platform administrator (FR-064).");
        }

        return new HarnessOptions(task, registryId, write, actorId, connection, budget, apiDirectory);
    }

    private static Guid ParseGuid(string value, string option) =>
        Guid.TryParse(value, out var guid)
            ? guid
            : throw new ArgumentException($"{option} must be a UUID, not '{value}'.");

    private static double ParsePositive(string value, string option) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException($"{option} must be a positive number, not '{value}'.");
}

/// <summary>Console output.</summary>
internal static class Report
{
    internal static void PrintCriteria(string task, IReadOnlyList<EvalMetric> metrics, bool hasSuite)
    {
        Console.WriteLine($"{task}  (suite: {(hasSuite ? "present" : "none — cannot be measured")})");

        if (metrics.Count == 0)
        {
            Console.WriteLine("  No acceptance metrics are specified for this task, so no model can pass it.");
        }

        foreach (var metric in metrics)
        {
            Console.WriteLine($"  {metric.Key,-42} {metric.FormatRule(),-20} {metric.Source}");
        }

        Console.WriteLine();
    }

    /// <summary>What the model will be called with, so the run can be traced afterwards.</summary>
    internal static void PrintSetup(EvalEnvironment environment, EvalModelInvoker model)
    {
        Console.WriteLine(
            $"Prompt: {model.Target.TaskKey} v{model.Prompt.Version.ToString(CultureInfo.InvariantCulture)}  "
            + $"sha256 {model.Prompt.PromptHash}  ({environment.PromptsDirectory})");

        // The variable's name and whether it is set, never its value.
        Console.WriteLine(
            $"Credential: {environment.Transport.ApiKeyEnvironmentVariable} "
            + (environment.CredentialPresent ? "is set" : "is NOT set"));
    }

    internal static void PrintResult(RegistryTarget target, EvalOutcome outcome)
    {
        Console.WriteLine($"{target.TaskKey}  {target.Provider}/{target.ModelId}  [{target.Id}]");

        foreach (var set in outcome.EvalSets)
        {
            var review = set.IsReviewed
                ? $"reviewed by {set.Review.Reviewer} at {set.Review.ReviewedAt?.ToString("O", CultureInfo.InvariantCulture)}"
                : set.ReviewMismatch is not null
                    ? "REVIEW DOES NOT COVER THESE ITEMS"
                    : "NOT REVIEWED";

            // The items hash is what a reviewer records as review.itemsSha256 when signing off.
            Console.WriteLine(
                $"  Evaluation set {set.Name}: {set.Items.Count.ToString(CultureInfo.InvariantCulture)} items, "
                + $"{review}, sha256 {set.ContentHash}, items sha256 {set.ItemsHash}");
        }

        foreach (var result in outcome.Results)
        {
            var value = result.Value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
            Console.WriteLine(
                $"  {result.Metric.Key,-42} {value,10}  {result.Metric.FormatRule(),-20} {result.Outcome}");
        }

        Console.WriteLine($"Verdict: {outcome.Verdict}");

        foreach (var reason in outcome.Reasons)
        {
            Console.WriteLine($"  - {reason}");
        }
    }
}
