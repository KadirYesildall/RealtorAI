using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>Approvals arranged and driven for the US5 suites.</summary>
internal static class ApprovalTesting
{
    internal const string Remarks = "A bright home on a quiet street, with a garden and a two-car garage.";

    internal static string Approvals(Guid listingId) => $"/api/listings/{listingId}/approvals";

    internal static string Audit(Guid listingId) => $"/api/listings/{listingId}/audit";

    internal static string BrokerageAudit(Guid brokerageId) => $"/api/brokerages/{brokerageId}/audit";

    /// <summary>The four public-record facts FR-008 requires, entered by the agent.</summary>
    internal static async Task EnterRequiredFactsAsync(ApiFactory factory, Guid agent, Guid listing)
    {
        using var client = factory.CreateClientFor(agent);

        foreach (var (key, value) in new (string, int)[] { ("LivingArea", 2840), ("LotSizeSquareFeet", 7200), ("YearBuilt", 1998), ("TaxAnnualAmount", 14250) })
        {
            using var put = TestListings.Conditional(HttpMethod.Put, TestListings.Fields(listing), new JsonObject
            {
                ["resoFieldKey"] = key,
                ["value"] = value,
                ["provenance"] = "agent_entered",
            }, ifMatch: 0);

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);
        }
    }

    /// <summary>
    /// A listing ready to approve: a complete verified profile, the required facts, a description
    /// written by hand, and a compliance check of all of it. Returns the description's id.
    /// </summary>
    internal static async Task<(Guid Agent, Guid Listing, Guid Draft)> ReadyListingAsync(ApiFactory factory, string label, string remarks = Remarks)
    {
        var (agent, listing, draft) = await ComplianceTesting.ListingWithRemarksAsync(factory, label, remarks);
        await EnterRequiredFactsAsync(factory, agent, listing);
        await ComplianceTesting.CheckAsync(factory, agent, listing);

        return (agent, listing, draft);
    }

    internal static async Task<JsonObject> DetailAsync(ApiFactory factory, Guid caller, Guid listing)
    {
        using var client = factory.CreateClientFor(caller);

        return (await client.GetFromJsonAsync<JsonObject>(TestListings.Listing(listing)))!;
    }

    /// <summary>The output of the detail that is <paramref name="targetId"/>.</summary>
    internal static JsonObject Output(JsonObject detail, Guid targetId) =>
        detail["outputs"]!.AsArray().Single(output => output!["targetId"]!.GetValue<Guid>() == targetId)!.AsObject();

    internal static string HashOf(JsonObject detail, Guid targetId) => Output(detail, targetId)["contentHash"]!.GetValue<string>();

    internal static async Task<HttpResponseMessage> ApproveAsync(
        ApiFactory factory,
        Guid caller,
        Guid listing,
        string targetType,
        Guid targetId,
        string reviewedHash,
        string channel = "mls")
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PostAsJsonAsync(Approvals(listing), new JsonObject
        {
            ["targetType"] = targetType,
            ["targetId"] = targetId,
            ["channel"] = channel,
            ["reviewedContentHash"] = reviewedHash,
        });
    }

    /// <summary>Approves what the screen currently shows for <paramref name="targetId"/>, and asserts it was recorded.</summary>
    internal static async Task<JsonObject> ApproveShownAsync(ApiFactory factory, Guid agent, Guid listing, string targetType, Guid targetId)
    {
        var detail = await DetailAsync(factory, agent, listing);
        var response = await ApproveAsync(factory, agent, listing, targetType, targetId, HashOf(detail, targetId));

        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    internal static List<string> Codes(JsonObject problemOrOutput) =>
        problemOrOutput["blockers"]!.AsArray().Select(blocker => blocker!["code"]!.GetValue<string>()).ToList();
}
