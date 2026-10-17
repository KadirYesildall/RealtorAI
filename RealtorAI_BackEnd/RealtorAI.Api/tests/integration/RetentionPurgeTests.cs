using System.Net;
using Npgsql;
using RealtorAI.Tests.Contract.Infrastructure;
using RetentionPurge;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T155 — the FR-065 retention purge: originals go only once their listing has been deleted for
/// longer than the retention period, only through the purge job's own credential, and on the
/// record; no application role can delete one at any point (FR-001, FR-065).
///
/// <para>
/// <b>Credential.</b> <c>retention_purger</c> is created without a login by 0019; an environment
/// grants it one out of band. The local stack grants it here, as the deployment would.
/// </para>
/// </summary>
[Collection(TableLockingTests.Name)]
public sealed class RetentionPurgeTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string StorageApi = "http://127.0.0.1:54321/storage/v1";
    private const string Originals = "listing-photos-original";
    private const string PurgerPassword = "local-retention-purger";

    private static readonly string ServiceKey =
        Environment.GetEnvironmentVariable("REALTORAI_TEST_SUPABASE_SECRET_KEY")
        ?? "sb_secret_N7UND0UgjKTVK-Uodkm0Hg_xSvEMPvz";

    private readonly ApiFactory factory;
    private static readonly HttpClient Http = new();

    public RetentionPurgeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync() =>
        await TestDatabase.ExecuteAsync($"alter role retention_purger login password '{PurgerPassword}'");

    public Task DisposeAsync() => Task.CompletedTask;

    private static PurgeOptions Options(bool dryRun = false) => new(
        "Host=127.0.0.1;Port=54322;Database=postgres;Username=retention_purger;Password=" + PurgerPassword,
        new Uri(StorageApi),
        ServiceKey,
        Originals,
        BatchSize: 500,
        DryRun: dryRun);

    [Fact]
    public async Task AnOriginalOfAListingDeletedLongerThanTheRetentionPeriod_IsPurged_AndLogged()
    {
        var (listing, key) = await DeletedListingWithOriginalAsync("purge-expired", "4 years");

        var report = await PurgeJob.RunAsync(Options(), Http, default);

        Assert.Contains(key, report.Purged);
        Assert.Equal(HttpStatusCode.NotFound, await OriginalStatusAsync(key));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'originals_purged' and listing_id = $1 and actor_id is null
              and after_value -> 'objects' ? $2 and payload_hash ~ '^[0-9a-f]{64}$'
            """,
            listing,
            key));
    }

    [Fact]
    public async Task AnOriginalWithinTheRetentionPeriod_OrOfALiveListing_IsKept()
    {
        var (_, recent) = await DeletedListingWithOriginalAsync("purge-recent", "2 years");
        var (_, _, photos) = await ListingWithPhotosAsync(factory, "purge-live", 1);
        var live = await KeyOfAsync(photos[0]);

        var report = await PurgeJob.RunAsync(Options(), Http, default);

        Assert.DoesNotContain(recent, report.Purged);
        Assert.DoesNotContain(live, report.Purged);
        Assert.Equal(HttpStatusCode.OK, await OriginalStatusAsync(recent));
        Assert.Equal(HttpStatusCode.OK, await OriginalStatusAsync(live));
    }

    /// <summary>FR-065: the retention period is configuration — its legal value is confirmed in the AB 723 review.</summary>
    [Fact]
    public async Task TheRetentionPeriod_IsConfigurable()
    {
        var (_, key) = await DeletedListingWithOriginalAsync("purge-configured", "2 years");

        await TestDatabase.ExecuteAsync("update ops.retention_settings set value = interval '1 year' where key = 'originals_after_listing_deleted'");

        try
        {
            var dryRun = await PurgeJob.RunAsync(Options(dryRun: true), Http, default);

            Assert.Contains(key, dryRun.Eligible);
            Assert.Empty(dryRun.Purged);
            Assert.Equal(HttpStatusCode.OK, await OriginalStatusAsync(key));
        }
        finally
        {
            await TestDatabase.ExecuteAsync("update ops.retention_settings set value = interval '3 years' where key = 'originals_after_listing_deleted'");
        }
    }

    /// <summary>
    /// FR-001: the application's own credential — the storage service key the API holds — cannot
    /// delete an original, even an expired one, and neither can the database roles the API's
    /// tokens map to. Only an authorization the purge job wrote opens the guard.
    /// </summary>
    [Fact]
    public async Task NoApplicationRole_CanDeleteAnOriginal_EvenAnExpiredOne()
    {
        var (_, key) = await DeletedListingWithOriginalAsync("purge-application", "4 years");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{StorageApi}/object/{Originals}/{key}");
        request.Headers.Add("apikey", ServiceKey);
        request.Headers.Add("Authorization", $"Bearer {ServiceKey}");
        var response = await Http.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, await OriginalStatusAsync(key));

        foreach (var role in new[] { "service_role", "authenticated" })
        {
            await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var setRole = new NpgsqlCommand($"set local role {role}", connection, transaction))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            await using var insert = new NpgsqlCommand("insert into ops.purge_authorizations (object_name, listing_id) values ($1, gen_random_uuid())", connection, transaction)
            {
                Parameters = { new() { Value = key } },
            };

            var refused = await Assert.ThrowsAnyAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", refused.SqlState);
        }
    }

    /// <summary>The purge credential itself cannot open the guard for an original still under retention.</summary>
    [Fact]
    public async Task ThePurgeCredential_CannotDeleteAnOriginalStillUnderRetention()
    {
        var (listing, key) = await DeletedListingWithOriginalAsync("purge-forced", "2 years");

        await using (var purger = new NpgsqlConnection(Options().PurgerConnectionString))
        {
            await purger.OpenAsync();
            await using var insert = new NpgsqlCommand("insert into ops.purge_authorizations (object_name, listing_id) values ($1, $2)", purger)
            {
                Parameters = { new() { Value = key }, new() { Value = listing } },
            };
            await insert.ExecuteNonQueryAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{StorageApi}/object/{Originals}/{key}");
        request.Headers.Add("apikey", ServiceKey);
        request.Headers.Add("Authorization", $"Bearer {ServiceKey}");
        var response = await Http.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, await OriginalStatusAsync(key));
    }

    private async Task<(Guid Listing, string Key)> DeletedListingWithOriginalAsync(string label, string deletedAgo)
    {
        var (_, listing, photos) = await ListingWithPhotosAsync(factory, label, 1);
        var key = await KeyOfAsync(photos[0]);

        await TestDatabase.ExecuteAsync(
            $"update public.listings set status = 'archived', deleted_at = timezone('utc', now()) - interval '{deletedAgo}' where id = $1",
            listing);

        return (listing, key);
    }

    private static async Task<string> KeyOfAsync(Guid photo) =>
        (await TestDatabase.ScalarAsync<string>("select original_storage_key from public.photos where id = $1", photo))!;

    private static async Task<HttpStatusCode> OriginalStatusAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{StorageApi}/object/authenticated/{Originals}/{key}");
        request.Headers.Add("apikey", ServiceKey);
        request.Headers.Add("Authorization", $"Bearer {ServiceKey}");
        var response = await Http.SendAsync(request);

        // Storage answers a missing object with 400 and a not_found body.
        return response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync()).Contains("not_found", StringComparison.OrdinalIgnoreCase)
            ? HttpStatusCode.NotFound
            : response.StatusCode;
    }
}
