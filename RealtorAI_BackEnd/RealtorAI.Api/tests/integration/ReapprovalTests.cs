using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T142 — editing an approved item revokes its approval and re-runs compliance on that item only
/// (FR-025; Story 5 scenario 4, the Independent Test).
/// </summary>
public sealed class ReapprovalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ReapprovalTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task EditingAnApprovedDescription_ReturnsEverythingToReview_AndReChecksOnlyIt()
    {
        var (agent, listing, description) = await ReadyListingAsync(factory, "reapprove");
        var headline = await WriteAsync(agent, listing, "headline", "A bright home on a quiet street");
        await ComplianceTesting.CheckAsync(factory, agent, listing);
        await ApproveShownAsync(factory, agent, listing, "content_draft", description);
        await ApproveShownAsync(factory, agent, listing, "content_draft", headline);
        await ApproveShownAsync(factory, agent, listing, "listing", listing);
        Assert.Equal("approved", (await DetailAsync(factory, agent, listing))["status"]!.GetValue<string>());

        var headlineChecked = await CheckedAtAsync(headline);
        var listingChecked = await CheckedAtAsync(listing);
        var rowVersion = (await DetailAsync(factory, agent, listing))["contentDrafts"]!.AsArray()
            .Single(draft => draft!["id"]!.GetValue<Guid>() == description)!["rowVersion"]!.GetValue<int>();

        using (var client = factory.CreateClientFor(agent))
        {
            using var patch = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, description), new JsonObject { ["body"] = Remarks + " Freshly painted." }, rowVersion);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);
        }

        var after = await DetailAsync(factory, agent, listing);

        // The edited item: back to draft, approval revoked, re-checked.
        Assert.Equal("draft", after["contentDrafts"]!.AsArray().Single(draft => draft!["id"]!.GetValue<Guid>() == description)!["status"]!.GetValue<string>());
        Assert.False(Output(after, description)["approved"]!.GetValue<bool>());
        Assert.Equal(Output(after, description)["contentHash"]!.GetValue<string>(), await CheckedHashAsync(description));

        // The listing: back to review, its approval revoked by the change.
        Assert.Equal("ready_for_review", after["status"]!.GetValue<string>());
        Assert.False(Output(after, listing)["approved"]!.GetValue<bool>());
        Assert.Equal("listing_changed", await TestDatabase.ScalarAsync<string>(
            "select revoked_reason from public.approvals where target_id = $1 and target_type = 'listing'",
            listing));

        // Only the edited item was re-checked; the untouched headline keeps its approval.
        Assert.Equal(headlineChecked, await CheckedAtAsync(headline));
        Assert.Equal(listingChecked, await CheckedAtAsync(listing));
        Assert.True(Output(after, headline)["approved"]!.GetValue<bool>());
    }

    /// <summary>A change to the listing's facts revokes the listing's approval too, whatever path made it.</summary>
    [Fact]
    public async Task EditingAFact_OfAnApprovedListing_RevokesItsApproval()
    {
        var (agent, listing, description) = await ReadyListingAsync(factory, "reapprove-fact");
        await ApproveShownAsync(factory, agent, listing, "content_draft", description);
        await ApproveShownAsync(factory, agent, listing, "listing", listing);
        var field = (await DetailAsync(factory, agent, listing))["fields"]!.AsArray().Single(candidate => candidate!["resoFieldKey"]!.GetValue<string>() == "YearBuilt")!;

        using (var client = factory.CreateClientFor(agent))
        {
            using var put = TestListings.Conditional(HttpMethod.Put, TestListings.Fields(listing), new JsonObject
            {
                ["resoFieldKey"] = "YearBuilt",
                ["value"] = 1999,
                ["provenance"] = "agent_entered",
            }, field["rowVersion"]!.GetValue<int>());
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);
        }

        var after = await DetailAsync(factory, agent, listing);
        Assert.Equal("ready_for_review", after["status"]!.GetValue<string>());
        Assert.False(Output(after, listing)["approved"]!.GetValue<bool>());
        Assert.Contains("compliance_not_run", Codes(Output(after, listing)));
    }

    private async Task<Guid> WriteAsync(Guid agent, Guid listing, string kind, string body)
    {
        using var client = factory.CreateClientFor(agent);
        var response = await client.PostAsJsonAsync(ContentTesting.Content(listing), new JsonObject { ["kind"] = kind, ["body"] = body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    private static Task<DateTime?> CheckedAtAsync(Guid target) =>
        TestDatabase.ScalarAsync<DateTime?>("select checked_at from public.compliance_checks where target_id = $1", target);

    private static Task<string?> CheckedHashAsync(Guid target) =>
        TestDatabase.ScalarAsync<string>("select content_hash from public.compliance_checks where target_id = $1", target);
}
