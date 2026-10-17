using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Libs.Ai;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>Content generation arranged and driven for the US3 suites.</summary>
internal static class ContentTesting
{
    internal static string Content(Guid listingId) => $"/api/listings/{listingId}/content";

    internal static string Regenerate(Guid listingId) => $"/api/listings/{listingId}/content/regenerate";

    internal static string Draft(Guid listingId, Guid draftId) => $"/api/listings/{listingId}/content/{draftId}";

    /// <summary>
    /// An MLS rule profile with verified limits — what generation needs (research R2). CRMLS's
    /// own profile is unverified until T160, so content can never be generated against it.
    /// </summary>
    internal static async Task<string> VerifiedMlsAsync(int publicLimit = 600, int privateLimit = 400)
    {
        var id = "TEST-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        await TestDatabase.ExecuteAsync(
            """
            insert into public.mls_rule_profiles
                (id, display_name, public_remarks_max_chars, private_remarks_max_chars, rulebook_version, verified_at)
            values ($1, 'Contract test MLS', $2, $3, 'contract-test', timezone('utc', now()))
            """,
            id,
            publicLimit,
            privateLimit);

        return id;
    }

    /// <summary>A listing owned by a fresh agent whose brokerage targets <paramref name="mlsId"/>.</summary>
    internal static async Task<(Guid Agent, Guid Listing)> ListingOnMlsAsync(ApiFactory factory, string label, string? mlsId)
    {
        var agent = await TestAgents.CreateAsync(label);
        await TestBrokerages.CreateAsync(agent, targetMlsId: mlsId);

        return (agent, TestListings.IdOf(await TestListings.CreateAsync(factory, agent)));
    }

    internal static void EnableContentModel(AiEnabledApiFactory factory) =>
        factory.Registry.Enable(AiEnabledApiFactory.Entry(AiTaskKey.ListingContent, "test/writer-model"));

    internal static async Task<HttpResponseMessage> RegenerateAsync(ApiFactory factory, Guid agent, Guid listing, string kind, string? tone = null)
    {
        using var client = factory.CreateClientFor(agent);
        var body = new JsonObject { ["kind"] = kind };

        if (tone is not null)
        {
            body["tone"] = tone;
        }

        return await client.PostAsJsonAsync(Regenerate(listing), body);
    }

    /// <summary>Regenerates <paramref name="kind"/>, waits for the run, and returns the current drafts.</summary>
    internal static async Task<JsonArray> GenerateAsync(ApiFactory factory, Guid agent, Guid listing, string kind, string? tone = null)
    {
        var response = await RegenerateAsync(factory, agent, listing, kind, tone);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await PipelineTesting.WaitForCompletionAsync(factory, agent, listing);

        return await DraftsAsync(factory, agent, listing);
    }

    internal static async Task<JsonArray> DraftsAsync(ApiFactory factory, Guid caller, Guid listing)
    {
        using var client = factory.CreateClientFor(caller);

        return (await client.GetFromJsonAsync<JsonArray>(Content(listing)))!;
    }

    internal static IEnumerable<JsonObject> OfKind(JsonArray drafts, string kind) =>
        drafts.Select(draft => draft!.AsObject()).Where(draft => draft["kind"]!.GetValue<string>() == kind);

    /// <summary>An unconfirmed AI-detected InteriorFeatures field, as photo-derived facts arrive.</summary>
    internal static async Task<Guid> AiFeatureFieldAsync(ApiFactory factory, Guid agent, Guid listing)
    {
        using var client = factory.CreateClientFor(agent);
        using var put = TestListings.Conditional(HttpMethod.Put, TestListings.Fields(listing), new JsonObject
        {
            ["resoFieldKey"] = "InteriorFeatures",
            ["value"] = new JsonArray("VaultedCeilings"),
            ["provenance"] = "ai_detected_from_photo",
        }, ifMatch: 0);

        var response = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }
}
