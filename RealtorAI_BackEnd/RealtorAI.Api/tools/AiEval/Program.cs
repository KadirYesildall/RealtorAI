using System.Globalization;
using AiEval;

// T054 — AI evaluation harness entry point (Principle VIII.4, FR-059, quickstart.md §6).
//
//   aieval --task <task|all>
//       Prints each task's acceptance criteria and whether a suite exists to measure them.
//
//   aieval --registry-id <uuid> [--write --actor <profile-uuid>]
//       Evaluates that registry entry's model with its task's suite. With --write, records a
//       pass or a measured failure in ai_model_registry and the audit trail.
//
// Options: --connection <npgsql> (or REALTORAI_EVAL_POSTGRES), --budget-tokens <n>,
//          --budget-images <n>.
//
// Exit codes: 0 passed (or criteria listed), 1 failed, 2 incomplete, 64 usage error,
//             70 could not record.

const int ExitPassed = 0;
const int ExitFailed = 1;
const int ExitIncomplete = 2;
const int ExitUsage = 64;
const int ExitRecordFailed = 70;

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

IReadOnlyDictionary<string, double> measurements = EvalSuites.Registered.TryGetValue(
    target.TaskKey,
    out var suite)
    ? await suite.RunAsync(target, CancellationToken.None)
    : new Dictionary<string, double>();

var evaluatedAt = DateTimeOffset.UtcNow;
var (verdict, results) = EvalVerdict.Evaluate(catalog.For(target.TaskKey), measurements);

Report.PrintResult(target, verdict, results, suite is not null);

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

/// <summary>Parsed command line.</summary>
internal sealed record HarnessOptions(
    string? Task,
    Guid? RegistryId,
    bool Write,
    Guid? ActorId,
    string? ConnectionString,
    EvalBudget Budget)
{
    internal const string Usage =
        """
        Usage:
          aieval --task <photo.analysis|photo.embedding|listing.content|compliance.judge|all>
          aieval --registry-id <uuid> [--task <task>] [--write --actor <profile-uuid>]
        Options:
          --connection <npgsql connection string>   (or REALTORAI_EVAL_POSTGRES)
          --budget-tokens <n>   --budget-images <n>   (default 200000 / 120)
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
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        var budget = new EvalBudget(tokens, images);

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

        return new HarnessOptions(task, registryId, write, actorId, connection, budget);
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

    internal static void PrintResult(
        RegistryTarget target,
        Verdict verdict,
        IReadOnlyList<MetricResult> results,
        bool hasSuite)
    {
        Console.WriteLine($"{target.TaskKey}  {target.Provider}/{target.ModelId}  [{target.Id}]");

        if (!hasSuite)
        {
            Console.WriteLine("  No evaluation suite exists for this task, so nothing was measured.");
        }

        foreach (var result in results)
        {
            var value = result.Value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
            Console.WriteLine(
                $"  {result.Metric.Key,-42} {value,10}  {result.Metric.FormatRule(),-20} {result.Outcome}");
        }

        Console.WriteLine($"Verdict: {verdict}");
    }
}
