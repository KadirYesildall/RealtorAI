using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T143 — the blind-approval guard: a review screen that is out of date cannot approve (SC-011,
/// FR-066).
///
/// <para>
/// Two sessions: one renders the review screen and the agent reads it; meanwhile the other — a
/// second tab, or a background regeneration — changes what is there. The first session's approve
/// carries the hash of what it showed, which no longer matches, so nothing is approved, and the
/// attempt is on the record.
/// </para>
/// </summary>
public sealed class BlindApprovalTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public BlindApprovalTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        ContentTesting.EnableContentModel(factory);
    }

    [Fact]
    public async Task AnEditInAnotherTab_MakesTheStaleScreensApprovalFail()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "blind-tab");
        var staleScreen = await DetailAsync(factory, agent, listing);

        // The other tab edits, and the edit is re-checked, so nothing but the hash stands in the way.
        var rowVersion = staleScreen["contentDrafts"]![0]!["rowVersion"]!.GetValue<int>();
        using (var client = factory.CreateClientFor(agent))
        {
            using var patch = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["body"] = "Words the first tab never showed." }, rowVersion);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);
        }

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(staleScreen, draft));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["content_changed_since_review"], Codes((await response.Content.ReadFromJsonAsync<JsonObject>())!));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.approvals where listing_id = $1", listing));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where listing_id = $1 and action = 'approval_rejected_hash_mismatch'",
            listing));

        // The fresh screen approves what it shows.
        await ApproveShownAsync(factory, agent, listing, "content_draft", draft);
    }

    /// <summary>A regeneration landing mid-review supersedes the draft being read: it cannot be approved.</summary>
    [Fact]
    public async Task ARegenerationMidReview_SupersedesWhatTheScreenShows()
    {
        var mls = await ComplianceTesting.CompleteMlsAsync();
        var (agent, listing) = await ContentTesting.ListingOnMlsAsync(factory, "blind-regenerate", mls);
        await EnterRequiredFactsAsync(factory, agent, listing);
        var headline = ContentTesting.OfKind(await ContentTesting.GenerateAsync(factory, agent, listing, "headline"), "headline").Single()["id"]!.GetValue<Guid>();
        var staleScreen = await DetailAsync(factory, agent, listing);

        await ContentTesting.GenerateAsync(factory, agent, listing, "headline");

        var response = await ApproveAsync(factory, agent, listing, "content_draft", headline, HashOf(staleScreen, headline));

        Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, response.StatusCode.ToString());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.approvals where listing_id = $1", listing));
    }

    /// <summary>The listing's own hash covers its photos: a caption changed elsewhere stops an approval of the old view.</summary>
    [Fact]
    public async Task APhotoChangeElsewhere_MakesTheStaleListingApprovalFail()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "blind-photo");
        var photo = (await PipelineTesting.UploadAsync(factory, agent, listing, TestImages.Jpeg()))[0];
        await ComplianceTesting.CheckAsync(factory, agent, listing);
        await ApproveShownAsync(factory, agent, listing, "content_draft", draft);
        var staleScreen = await DetailAsync(factory, agent, listing);

        using (var client = factory.CreateClientFor(agent))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(PipelineTesting.Photo(listing, photo), new JsonObject { ["caption"] = "Changed in another tab" })).StatusCode);
        }

        var response = await ApproveAsync(factory, agent, listing, "listing", listing, HashOf(staleScreen, listing));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("content_changed_since_review", Codes((await response.Content.ReadFromJsonAsync<JsonObject>())!));
        Assert.NotEqual("approved", (await DetailAsync(factory, agent, listing))["status"]!.GetValue<string>());
    }
}
