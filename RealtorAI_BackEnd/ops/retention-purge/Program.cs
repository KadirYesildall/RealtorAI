using RetentionPurge;

// FR-065 retention purge (T154). Run on a schedule by the operations platform, never by the API.
//
//   RETENTION_PURGER_CONNECTION  retention_purger's own Postgres login (granted out of band)
//   STORAGE_API_URL              e.g. http://127.0.0.1:54321/storage/v1
//   STORAGE_KEY                  a key the Storage API accepts
//   ORIGINALS_BUCKET             optional; default listing-photos-original
//
//   --dry-run                    list what is eligible; authorize and delete nothing

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set.");

var options = new PurgeOptions(
    Required("RETENTION_PURGER_CONNECTION"),
    new Uri(Required("STORAGE_API_URL")),
    Required("STORAGE_KEY"),
    Environment.GetEnvironmentVariable("ORIGINALS_BUCKET") ?? "listing-photos-original",
    DryRun: args.Contains("--dry-run", StringComparer.Ordinal));

using var http = new HttpClient();
var report = await PurgeJob.RunAsync(options, http, CancellationToken.None);

Console.WriteLine($"Eligible: {report.Eligible.Count}. Purged: {report.Purged.Count}. Failed: {report.Failed.Count}.");

foreach (var (key, error) in report.Failed)
{
    Console.Error.WriteLine($"Failed {key}: {error}");
}

return report.Failed.Count == 0 ? 0 : 1;
