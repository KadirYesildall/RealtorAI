using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T138 — <c>GET /api/listings/{id}</c> as the review screen's aggregate (FR-027; Story 5
/// scenario 1).
///
/// <para>
/// <b>What is at stake.</b> One read holds everything the agent reviews — the facts with their
/// provenance, the photos, the content, the findings — and, for each output the agent can
/// approve, the hash of exactly what is shown and why approval is not yet possible. The hash is
/// what the approve call sends back, so the screen can only ever approve what it rendered.
/// </para>
/// </summary>
public sealed class ListingDetailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ListingDetailTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Detail_HoldsContentFindingsAndOneOutputPerApprovableItem()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "detail-aggregate");

        var detail = await DetailAsync(factory, agent, listing);

        Assert.Single(detail["contentDrafts"]!.AsArray());
        Assert.NotEmpty(detail["findings"]!.AsArray());
        Assert.Equal(4, detail["fields"]!.AsArray().Count);

        var outputs = detail["outputs"]!.AsArray().Select(output => output!.AsObject()).ToList();
        Assert.Equal(2, outputs.Count);

        var description = Output(detail, draft);
        Assert.Equal("content_draft", description["targetType"]!.GetValue<string>());
        Assert.Equal("public_remarks", description["kind"]!.GetValue<string>());
        Assert.Equal(detail["contentDrafts"]![0]!["contentHash"]!.GetValue<string>(), description["contentHash"]!.GetValue<string>());
        Assert.False(description["approved"]!.GetValue<bool>());
        Assert.Empty(description["blockers"]!.AsArray());

        var whole = Output(detail, listing);
        Assert.Equal("listing", whole["targetType"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", whole["contentHash"]!.GetValue<string>());
        Assert.Equal(["content_not_approved"], Codes(whole));
        Assert.Equal(whole["blockers"]!.ToJsonString(), detail["approvalBlockers"]!.ToJsonString());
    }

    /// <summary>A listing nobody has checked or completed shows every reason, each machine-readable.</summary>
    [Fact]
    public async Task Detail_OfAFreshListing_NamesEveryBlocker()
    {
        var (agent, listing, _) = await ComplianceTesting.ListingWithRemarksAsync(factory, "detail-fresh", Remarks);

        var codes = Codes(Output(await DetailAsync(factory, agent, listing), listing));

        Assert.Contains("compliance_not_run", codes);
        Assert.Contains("required_field_missing", codes);
        Assert.Contains("content_not_approved", codes);
    }

    /// <summary>Research R2: against unverified CRMLS, nothing is approvable, and the screen says why.</summary>
    [Fact]
    public async Task Detail_AgainstUnverifiedCrmls_ShowsTheProfileBlocker()
    {
        var (agent, listing, draft) = await ComplianceTesting.ListingWithRemarksAsync(factory, "detail-crmls", Remarks, "CRMLS");
        await ComplianceTesting.CheckAsync(factory, agent, listing);

        var detail = await DetailAsync(factory, agent, listing);

        Assert.Contains("rule_profile_incomplete", Codes(Output(detail, listing)));
        Assert.Contains("rule_profile_incomplete", Codes(Output(detail, draft)));
    }

    /// <summary>The hash of the listing output changes with what the agent reviews — here, a caption.</summary>
    [Fact]
    public async Task Detail_ListingHash_ChangesWhenWhatIsShownChanges()
    {
        var (agent, listing, _) = await ReadyListingAsync(factory, "detail-hash");
        var photo = (await PipelineTesting.UploadAsync(factory, agent, listing, TestImages.Jpeg()))[0];
        var before = HashOf(await DetailAsync(factory, agent, listing), listing);

        using (var client = factory.CreateClientFor(agent))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(PipelineTesting.Photo(listing, photo), new JsonObject { ["caption"] = "The front garden" })).StatusCode);
        }

        Assert.NotEqual(before, HashOf(await DetailAsync(factory, agent, listing), listing));
    }

    [Fact]
    public async Task Detail_ForTheApprovedBrokerAdmin_ShowsTheSameOutputs()
    {
        var admin = await TestAgents.CreateAsync("detail-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await ComplianceTesting.CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("detail-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, member));

        var asOwner = await DetailAsync(factory, member, listing);
        var asAdmin = await DetailAsync(factory, admin, listing);

        Assert.Equal(asOwner["outputs"]!.ToJsonString(), asAdmin["outputs"]!.ToJsonString());
    }
}
