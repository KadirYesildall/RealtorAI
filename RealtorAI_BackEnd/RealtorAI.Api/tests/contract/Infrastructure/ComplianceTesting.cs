using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Libs.Ai;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>Compliance runs arranged and driven for the US4 suites.</summary>
internal static class ComplianceTesting
{
    /// <summary>Copy with every kind of finding the checks raise, at known places.</summary>
    internal const string ViolatingRemarks =
        "Perfect for young families. Walking distance to church. Call (949) 555-0123 for a tour.";

    internal static string Compliance(Guid listingId) => $"/api/listings/{listingId}/compliance";

    internal static string ApplyFix(Guid listingId, Guid findingId) => $"/api/listings/{listingId}/compliance/{findingId}/apply-fix";

    internal static string Acknowledge(Guid listingId, Guid findingId) => $"/api/listings/{listingId}/compliance/{findingId}/acknowledge";

    /// <summary>
    /// A verified MLS profile with every value set: limits, photo bounds, every remarks detector
    /// blocking, no required fields, virtual staging allowed with one approved label.
    /// </summary>
    internal static async Task<string> CompleteMlsAsync(int publicLimit = 1000, int photoMinCount = 0, string requiredFields = "[]")
    {
        var id = "TEST-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        await TestDatabase.ExecuteAsync(
            """
            insert into public.mls_rule_profiles
                (id, display_name, public_remarks_max_chars, private_remarks_max_chars,
                 photo_min_width, photo_min_height, photo_min_count, photo_max_count,
                 forbidden_in_public_remarks, required_fields, allowed_alteration_types, disclosure_labels,
                 rulebook_version, verified_at)
            values ($1, 'Compliance test MLS', $2, 500, 640, 480, $3, 100,
                    '[{"detector":"phone_number","severity":"blocking"},{"detector":"email_address","severity":"blocking"},
                      {"detector":"url","severity":"blocking"},{"detector":"agent_name","severity":"blocking"},
                      {"detector":"brokerage_name","severity":"blocking"}]'::jsonb,
                    $4::jsonb, '["virtual_staging"]'::jsonb, '["Virtually Staged"]'::jsonb,
                    'contract-test', timezone('utc', now()))
            """,
            id,
            publicLimit,
            photoMinCount,
            requiredFields);

        return id;
    }

    internal static void EnableJudge(AiEnabledApiFactory factory) =>
        factory.Registry.Enable(AiEnabledApiFactory.Entry(AiTaskKey.ComplianceJudge, "test/judge-model", """{"temperature": 0}"""));

    /// <summary>A listing on a complete profile, with public remarks written by hand.</summary>
    internal static async Task<(Guid Agent, Guid Listing, Guid Draft)> ListingWithRemarksAsync(ApiFactory factory, string label, string remarks, string? mlsId = null)
    {
        var (agent, listing) = await ContentTesting.ListingOnMlsAsync(factory, label, mlsId ?? await CompleteMlsAsync());

        using var client = factory.CreateClientFor(agent);
        var created = await client.PostAsJsonAsync(ContentTesting.Content(listing), new JsonObject { ["kind"] = "public_remarks", ["body"] = remarks });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (agent, listing, (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());
    }

    internal static async Task<HttpResponseMessage> RunAsync(ApiFactory factory, Guid caller, Guid listing, JsonObject? body = null)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PostAsJsonAsync(Compliance(listing), body ?? new JsonObject());
    }

    /// <summary>Runs compliance on the whole listing, waits for it, and returns the findings.</summary>
    internal static async Task<JsonArray> CheckAsync(ApiFactory factory, Guid agent, Guid listing, JsonObject? body = null)
    {
        var response = await RunAsync(factory, agent, listing, body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await PipelineTesting.WaitForCompletionAsync(factory, agent, listing);

        return await FindingsAsync(factory, agent, listing);
    }

    internal static async Task<JsonArray> FindingsAsync(ApiFactory factory, Guid caller, Guid listing, string query = "")
    {
        using var client = factory.CreateClientFor(caller);

        return (await client.GetFromJsonAsync<JsonArray>(Compliance(listing) + query))!;
    }

    internal static IEnumerable<JsonObject> Open(JsonArray findings) =>
        findings.Select(finding => finding!.AsObject()).Where(finding => finding["status"]!.GetValue<string>() == "open");

    internal static JsonObject Rule(JsonArray findings, string ruleId) =>
        Open(findings).Single(finding => finding["ruleId"]!.GetValue<string>() == ruleId);

    internal static Guid IdOf(JsonObject finding) => finding["id"]!.GetValue<Guid>();
}
