using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T118 — <c>POST /compliance/{findingId}/apply-fix</c>: the one-click rewrite (FR-017, FR-025).
///
/// <para>
/// The rewrite replaces exactly the span the finding names, as an edit by the agent: audited,
/// revoking any approval of the old words, and followed at once by a re-check of that draft.
/// </para>
/// </summary>
public sealed class ApplyFixTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ApplyFixTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApplyFix_ReplacesTheSpan_ResolvesTheFinding_AndReChecks()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(factory, "fix-apply", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families");
        var fix = finding["suggestedFix"]!.GetValue<string>();

        var response = await PostAsync(agent, listing, IdOf(finding));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(fix + ViolatingRemarks["Perfect for young families".Length..], updated["body"]!.GetValue<string>());
        Assert.Equal(draft, updated["id"]!.GetValue<Guid>());

        var after = await FindingsAsync(factory, agent, listing);
        Assert.DoesNotContain(Open(after), candidate => candidate["ruleId"]!.GetValue<string>() == "fair_housing.familial_status.perfect_for_families");
        Assert.Equal("resolved", await TestDatabase.ScalarAsync<string>("select status::text from public.compliance_findings where id = $1", IdOf(finding)));

        // The rest of the draft was re-checked against its new text: the phone number is still
        // found, at its shifted position.
        var phone = Rule(after, "mls.forbidden.phone_number");
        var body = updated["body"]!.GetValue<string>();
        Assert.Equal("(949) 555-0123", body[phone["spanStart"]!.GetValue<int>()..phone["spanEnd"]!.GetValue<int>()]);

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'content_edited' and target_id = $1 and after_value ->> 'appliedFinding' = $2",
            draft,
            IdOf(finding).ToString()));
    }

    /// <summary>FR-025: a fix to approved words is an edit, so the approval goes.</summary>
    [Fact]
    public async Task ApplyFix_ToAnApprovedDraft_RevokesTheApproval()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(factory, "fix-approved", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.religion.distance_to_worship");
        await TestDatabase.ExecuteAsync("update public.content_drafts set status = 'approved' where id = $1", draft);
        await TestDatabase.ExecuteAsync(
            """
            insert into public.approvals (listing_id, target_type, target_id, channel, content_hash, approved_by)
            values ($1, 'content_draft', $2, 'mls', 'x', $3)
            """,
            listing,
            draft,
            agent);

        var response = await PostAsync(agent, listing, IdOf(finding));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("draft", (await response.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.approvals where target_id = $1 and revoked_at is null", draft));
    }

    /// <summary>A finding about the listing as a whole has no words to rewrite.</summary>
    [Fact]
    public async Task ApplyFix_ForAFindingWithNoRewrite_Is422()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "fix-none", "A bright home.", "CRMLS");
        var finding = Rule(await CheckAsync(factory, agent, listing), "mls.profile_incomplete");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(agent, listing, IdOf(finding))).StatusCode);
    }

    /// <summary>The words the finding pointed at are no longer there: nothing is replaced blindly.</summary>
    [Fact]
    public async Task ApplyFix_WhenTheTextNoLongerMatches_Is409()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(factory, "fix-moved", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families");
        await TestDatabase.ExecuteAsync("update public.content_drafts set body = 'Rewritten elsewhere entirely.' where id = $1", draft);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(agent, listing, IdOf(finding))).StatusCode);
    }

    [Fact]
    public async Task ApplyFix_ForAFindingThatIsNotOpen_Is409()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "fix-twice", ViolatingRemarks);
        var finding = Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(agent, listing, IdOf(finding))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(agent, listing, IdOf(finding))).StatusCode);
    }

    [Fact]
    public async Task ApplyFix_ForAnUnknownFinding_Is404()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "fix-unknown", "A bright home.");

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(agent, listing, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task ApplyFix_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("fix-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("fix-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, member));

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(admin, listing, Guid.NewGuid())).StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid caller, Guid listing, Guid finding)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PostAsync(ApplyFix(listing, finding), content: null);
    }
}
