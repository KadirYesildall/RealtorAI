using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T140 — <c>POST /approvals</c> refused, with the machine-readable reasons (FR-014, FR-022,
/// FR-066, SC-011; Story 5 scenario 5).
///
/// <para>
/// Each refusal records nothing but, for a hash mismatch, the attempt itself: the screen that sent
/// it was showing content that is no longer there, and the audit trail keeps that near-miss as
/// evidence.
/// </para>
/// </summary>
public sealed class ApprovalBlockedTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public ApprovalBlockedTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        ContentTesting.EnableContentModel(factory);
    }

    [Fact]
    public async Task Approve_ContentThatChangedSinceReview_Is409_AndTheAttemptIsAudited()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "blocked-changed");
        var shown = HashOf(await DetailAsync(factory, agent, listing), draft);
        await TestDatabase.ExecuteAsync("update public.content_drafts set body = body || ' Changed elsewhere.' where id = $1", draft);

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, shown);

        await AssertBlockedAsync(response, draft, "content_changed_since_review");
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'approval_rejected_hash_mismatch' and target_id = $1 and actor_id = $2 and after_value ->> 'reviewedContentHash' = $3",
            draft,
            agent,
            shown));
    }

    [Fact]
    public async Task Approve_WithAnOpenBlockingFinding_Is409_NamingTheFinding()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "blocked-finding", "Perfect for young families, near the park.");
        var finding = ComplianceTesting.Rule(await ComplianceTesting.FindingsAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families");

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(await DetailAsync(factory, agent, listing), draft));

        var problem = await AssertBlockedAsync(response, draft, "blocking_finding_open");
        Assert.Contains(problem["blockers"]!.AsArray(), blocker => blocker!["targetId"]?.GetValue<Guid>() == ComplianceTesting.IdOf(finding));
    }

    /// <summary>FR-014: content resting on an unconfirmed AI fact cannot be approved until the fact is confirmed.</summary>
    [Fact]
    public async Task Approve_ContentRestingOnAnUnconfirmedAiFact_Is409_UntilItIsConfirmed()
    {
        var (agent, listing) = await ContentTesting.ListingOnMlsAsync(factory, "blocked-fact", await ComplianceTesting.CompleteMlsAsync());
        await EnterRequiredFactsAsync(factory, agent, listing);
        var field = await ContentTesting.AiFeatureFieldAsync(factory, agent, listing);
        var draft = ContentTesting.OfKind(await ContentTesting.GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").First()["id"]!.GetValue<Guid>();
        await ContentTesting.DraftsAsync(factory, agent, listing);
        using (var client = factory.CreateClientFor(agent))
        {
            var drafts = await ContentTesting.DraftsAsync(factory, agent, listing);
            var rowVersion = drafts.Single(candidate => candidate!["id"]!.GetValue<Guid>() == draft)!["rowVersion"]!.GetValue<int>();
            using var select = TestListings.Conditional(HttpMethod.Patch, ContentTesting.Draft(listing, draft), new JsonObject { ["selected"] = true }, rowVersion);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(select)).StatusCode);
        }

        var blocked = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(await DetailAsync(factory, agent, listing), draft));

        var problem = await AssertBlockedAsync(blocked, draft, "unconfirmed_ai_fact");
        Assert.Contains(problem["blockers"]!.AsArray(), blocker => blocker!["targetId"]?.GetValue<Guid>() == field);

        using (var client = factory.CreateClientFor(agent))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(TestListings.Confirm(listing, field), content: null)).StatusCode);
        }

        var allowed = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(await DetailAsync(factory, agent, listing), draft));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    /// <summary>SC-011: content no check has read cannot be approved.</summary>
    [Fact]
    public async Task Approve_ContentNoCheckHasRead_Is409()
    {
        var (agent, listing, draft) = await ComplianceTesting.ListingWithRemarksAsync(factory, "blocked-unchecked", Remarks);
        await EnterRequiredFactsAsync(factory, agent, listing);

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(await DetailAsync(factory, agent, listing), draft));

        await AssertBlockedAsync(response, draft, "compliance_not_run");
    }

    [Fact]
    public async Task Approve_TheListing_BeforeItsContent_Is409()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "blocked-content");

        var response = await ApproveAsync(factory, agent, listing, "listing", listing, HashOf(await DetailAsync(factory, agent, listing), listing));

        var problem = await AssertBlockedAsync(response, listing, "content_not_approved");
        Assert.Contains(problem["blockers"]!.AsArray(), blocker => blocker!["targetId"]?.GetValue<Guid>() == draft);
    }

    [Fact]
    public async Task Approve_TheListing_WithARequiredFactMissing_Is409()
    {
        var (agent, listing, draft) = await ComplianceTesting.ListingWithRemarksAsync(factory, "blocked-required", Remarks);
        await ComplianceTesting.CheckAsync(factory, agent, listing);
        await ApproveShownAsync(factory, agent, listing, "content_draft", draft);

        var response = await ApproveAsync(factory, agent, listing, "listing", listing, HashOf(await DetailAsync(factory, agent, listing), listing));

        await AssertBlockedAsync(response, listing, "required_field_missing");
    }

    /// <summary>Research R2: against unverified CRMLS, nothing can be approved.</summary>
    [Fact]
    public async Task Approve_AgainstUnverifiedCrmls_Is409()
    {
        var (agent, listing, draft) = await ComplianceTesting.ListingWithRemarksAsync(factory, "blocked-crmls", Remarks, "CRMLS");
        await EnterRequiredFactsAsync(factory, agent, listing);
        await ComplianceTesting.CheckAsync(factory, agent, listing);

        var response = await ApproveAsync(factory, agent, listing, "content_draft", draft, HashOf(await DetailAsync(factory, agent, listing), draft));

        await AssertBlockedAsync(response, draft, "rule_profile_incomplete");
    }

    private static async Task<JsonObject> AssertBlockedAsync(HttpResponseMessage response, Guid target, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(code, Codes(problem));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.approvals where target_id = $1 and revoked_at is null",
            target));

        return problem;
    }
}
