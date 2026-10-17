using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T144 — two sessions editing one draft at once: one wins, the other is told what is there now,
/// and no edit is lost (SC-012, FR-066).
/// </summary>
public sealed class ConcurrentEditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ConcurrentEditTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task TwoEditsFromTheSameVersion_OneWins_TheOtherSeesItAndReappliesWithoutLoss()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "concurrent");
        var version = (await DetailAsync(factory, agent, listing))["contentDrafts"]![0]!["rowVersion"]!.GetValue<int>();

        using var first = factory.CreateClientFor(agent);
        using var second = factory.CreateClientFor(agent);

        using var a = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["body"] = "Session A's words." }, version);
        using var b = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["body"] = "Session B's words." }, version);

        var responses = await Task.WhenAll(first.SendAsync(a), second.SendAsync(b));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        // The loser is shown what the winner saved, at the version it now holds.
        var problem = (await loser.Content.ReadFromJsonAsync<JsonObject>())!;
        var current = problem["current"]!.AsObject();
        var winnerBody = current["body"]!.GetValue<string>();
        Assert.True(winnerBody is "Session A's words." or "Session B's words.", winnerBody);
        Assert.Equal(version + 1, problem["currentRowVersion"]!.GetValue<int>());

        // Reapplied from the current version, the loser's edit is kept as well: nothing is lost.
        var loserBody = winnerBody == "Session A's words." ? "Session B's words." : "Session A's words.";
        using var retry = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["body"] = winnerBody + " " + loserBody }, version + 1);
        Assert.Equal(HttpStatusCode.OK, (await first.SendAsync(retry)).StatusCode);

        Assert.Equal(winnerBody + " " + loserBody, await TestDatabase.ScalarAsync<string>("select body from public.content_drafts where id = $1", draft));
        Assert.Equal(2L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'content_edited' and target_id = $1 and before_value is not null",
            draft));
    }

    /// <summary>The same guard holds against the background: an approval racing an edit never records the old words.</summary>
    [Fact]
    public async Task AnApprovalRacingAnEdit_NeverRecordsWordsThatAreNoLongerThere()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "concurrent-approve");
        var detail = await DetailAsync(factory, agent, listing);
        var version = detail["contentDrafts"]![0]!["rowVersion"]!.GetValue<int>();

        using var client = factory.CreateClientFor(agent);
        using var edit = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["body"] = "Edited while being approved." }, version);

        await Task.WhenAll(
            client.SendAsync(edit),
            ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(detail, draft)));

        var body = await TestDatabase.ScalarAsync<string>("select body from public.content_drafts where id = $1", draft);
        var live = await TestDatabase.ScalarAsync<string>("select content_hash from public.approvals where target_id = $1 and revoked_at is null", draft);

        // Either nothing is approved, or what is approved is exactly what is there.
        if (live is not null)
        {
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body!))), live);
        }
    }
}
