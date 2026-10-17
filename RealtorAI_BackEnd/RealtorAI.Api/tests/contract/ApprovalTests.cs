using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T139 — <c>POST /api/listings/{id}/approvals</c>, the success path (FR-026, SC-008; Story 5
/// scenario 3).
///
/// <para>
/// An approval names one output and one channel, records who and when, and the SHA-256 of the
/// exact content — the hash the agent's screen sent, which the server has just recomputed and
/// found equal. That is what lets the audit trail answer "what exactly did I approve?".
/// </para>
/// </summary>
public sealed class ApprovalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ApprovalTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApproveADraft_RecordsTheApproverTimeAndHash()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "approve-draft");
        var hash = HashOf(await DetailAsync(factory, agent, listing), draft);

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, hash);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var approval = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(draft, approval["targetId"]!.GetValue<Guid>());
        Assert.Equal("content_draft", approval["targetType"]!.GetValue<string>());
        Assert.Equal("mls", approval["channel"]!.GetValue<string>());
        Assert.Equal(hash, approval["contentHash"]!.GetValue<string>());
        Assert.Equal(agent, approval["approvedBy"]!.GetValue<Guid>());
        Assert.True(DateTimeOffset.UtcNow - approval["approvedAt"]!.GetValue<DateTimeOffset>() < TimeSpan.FromMinutes(1));
        Assert.Null(approval["revokedAt"]);

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.approvals where target_id = $1 and approved_by = $2 and content_hash = $3 and revoked_at is null",
            draft,
            agent,
            hash));
        Assert.Equal("approved", await TestDatabase.ScalarAsync<string>("select status::text from public.content_drafts where id = $1", draft));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'approval_granted' and target_id = $1 and actor_id = $2 and after_value ->> 'contentHash' = $3",
            draft,
            agent,
            hash));
    }

    /// <summary>Approving the same words twice records one approval.</summary>
    [Fact]
    public async Task ApproveTheSameContentTwice_IsIdempotent()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "approve-twice");
        var hash = HashOf(await DetailAsync(factory, agent, listing), draft);

        var first = await ApproveAsync(factory, agent, listing, "content_draft", draft, hash);
        var second = await ApproveAsync(factory, agent, listing, "content_draft", draft, hash);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>(),
            (await second.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());
    }

    /// <summary>With its content approved, the listing itself is approved, and its status says so.</summary>
    [Fact]
    public async Task ApproveTheListing_AfterItsContent_MovesItToApproved()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "approve-listing");
        await ApproveShownAsync(factory, agent, listing, "content_draft", draft);

        var detail = await DetailAsync(factory, agent, listing);
        Assert.Empty(Output(detail, listing)["blockers"]!.AsArray());

        var response = await ApproveAsync(factory, agent, listing, "listing", listing, HashOf(detail, listing));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var after = await DetailAsync(factory, agent, listing);
        Assert.Equal("approved", after["status"]!.GetValue<string>());
        Assert.True(Output(after, listing)["approved"]!.GetValue<bool>());
        Assert.True(Output(after, draft)["approved"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("brochure")]
    public async Task Approve_AnUnapprovableTargetType_Is422(string targetType)
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "approve-type");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ApproveAsync(factory, agent, listing, targetType, draft, new string('a', 64))).StatusCode);
    }

    [Fact]
    public async Task Approve_ForAnUnsupportedChannel_Is422()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "approve-channel");
        var hash = HashOf(await DetailAsync(factory, agent, listing), draft);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ApproveAsync(factory, agent, listing, "content_draft", draft, hash, channel: "instagram")).StatusCode);
    }

    /// <summary>Only the version chosen for publication is an output; an alternative is not.</summary>
    [Fact]
    public async Task Approve_ADraftOnAnotherListing_Is422()
    {
        var (agent, listing, _) = await ReadyListingAsync(factory, "approve-other");
        var (_, _, foreign) = await ReadyListingAsync(factory, "approve-foreign");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ApproveAsync(factory, agent, listing, "content_draft", foreign, new string('a', 64))).StatusCode);
    }

    /// <summary>VIII.7: an approval is a human act of the listing's agent, never their broker's.</summary>
    [Fact]
    public async Task Approve_ByTheApprovedBrokerAdmin_Is403_AndByAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("approve-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await ComplianceTesting.CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("approve-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, member));
        var stranger = await TestAgents.CreateAsync("approve-stranger");
        var hash = HashOf(await DetailAsync(factory, member, listing), listing);

        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(factory, admin, listing, "listing", listing, hash)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(factory, stranger, listing, "listing", listing, hash)).StatusCode);
    }
}
