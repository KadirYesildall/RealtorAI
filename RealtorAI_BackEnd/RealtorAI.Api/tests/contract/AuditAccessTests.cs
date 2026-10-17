using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ApprovalTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T141 — who may read an audit trail (FR-049, FR-050; SC-008).
///
/// <para>
/// The listing's agent always. A broker admin only when they are an approved admin of the
/// brokerage <i>and</i> the listing's agent is an approved member of it: a pending applicant's
/// work is not yet the brokerage's to inspect. Anyone else learns nothing, not even that the
/// listing exists.
/// </para>
/// </summary>
public sealed class AuditAccessTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public AuditAccessTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task TheOwner_ReadsTheTrail_NewestFirst_WithTheApprovalOnIt()
    {
        var (agent, listing, draft) = await ReadyListingAsync(factory, "audit-owner");
        await ApproveShownAsync(factory, agent, listing, "content_draft", draft);

        var events = await EventsAsync(agent, listing);

        Assert.NotEmpty(events);
        var times = events.Select(entry => entry!["createdAt"]!.GetValue<DateTimeOffset>()).ToList();
        Assert.Equal(times.OrderByDescending(time => time).ToList(), times);
        var approval = events.First(entry => entry!["action"]!.GetValue<string>() == "approval_granted")!;
        Assert.Equal(draft, approval["targetId"]!.GetValue<Guid>());
        Assert.Matches("^[0-9a-f]{64}$", approval["afterValue"]!["contentHash"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", approval["payloadHash"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheTrail_FiltersByAction()
    {
        var (agent, listing, _) = await ReadyListingAsync(factory, "audit-filter");

        var events = await EventsAsync(agent, listing, "?action=field_edit");

        Assert.Equal(4, events.Count);
        Assert.All(events, entry => Assert.Equal("field_edit", entry!["action"]!.GetValue<string>()));
    }

    /// <summary>Pages are walked with the cursor the previous page returned, with nothing repeated or skipped.</summary>
    [Fact]
    public async Task TheTrail_Pages_ByCursor()
    {
        var (agent, listing, _) = await ReadyListingAsync(factory, "audit-pages");
        using var client = factory.CreateClientFor(agent);

        var all = await EventsAsync(agent, listing);
        var first = await client.GetAsync(Audit(listing) + "?limit=3");
        var cursor = first.Headers.GetValues("X-Next-Cursor").Single();
        var second = (await client.GetFromJsonAsync<JsonArray>(Audit(listing) + "?limit=3&cursor=" + Uri.EscapeDataString(cursor)))!;

        var paged = (await first.Content.ReadFromJsonAsync<JsonArray>())!.Concat(second).Select(entry => entry!["id"]!.GetValue<Guid>()).ToList();
        Assert.Equal(all.Take(6).Select(entry => entry!["id"]!.GetValue<Guid>()), paged);
    }

    [Fact]
    public async Task AnApprovedAdminOfAnApprovedMember_ReadsIt()
    {
        var (admin, member, listing) = await BrokerageListingAsync("audit-approved", "approved");

        using var client = factory.CreateClientFor(admin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Audit(listing))).StatusCode);
        Assert.NotEqual(admin, member);
    }

    /// <summary>FR-050: a pending member's trail is not the brokerage's to read.</summary>
    [Fact]
    public async Task AnAdminOfAPendingMember_CannotReadIt()
    {
        var (admin, _, listing) = await BrokerageListingAsync("audit-pending", "pending");

        using var client = factory.CreateClientFor(admin);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Audit(listing))).StatusCode);
    }

    [Fact]
    public async Task AStranger_CannotReadIt()
    {
        var (_, listing, _) = await ReadyListingAsync(factory, "audit-stranger-owner");
        var stranger = await TestAgents.CreateAsync("audit-stranger");

        using var client = factory.CreateClientFor(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Audit(listing))).StatusCode);
    }

    [Fact]
    public async Task AnUnknownAction_Is422()
    {
        var (agent, listing, _) = await ReadyListingAsync(factory, "audit-bad-action");
        using var client = factory.CreateClientFor(agent);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(Audit(listing) + "?action=deleted_everything")).StatusCode);
    }

    // ═════════════ Brokerage-wide ═════════════

    /// <summary>The brokerage trail holds approved members' listings, and never a pending member's.</summary>
    [Fact]
    public async Task TheBrokerageTrail_ForItsAdmin_ExcludesPendingMembersListings()
    {
        var admin = await TestAgents.CreateAsync("brokerage-audit-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await ComplianceTesting.CompleteMlsAsync());
        var approved = await TestAgents.CreateAsync("brokerage-audit-approved");
        var pending = await TestAgents.CreateAsync("brokerage-audit-pending");
        await TestBrokerages.AddMemberAsync(approved, brokerage, "approved");
        await TestBrokerages.AddMemberAsync(pending, brokerage, "pending");
        var approvedListing = TestListings.IdOf(await TestListings.CreateAsync(factory, approved));
        var pendingListing = TestListings.IdOf(await TestListings.CreateAsync(factory, pending));

        using var client = factory.CreateClientFor(admin);
        var events = (await client.GetFromJsonAsync<JsonArray>(BrokerageAudit(brokerage)))!;
        var listings = events.Select(entry => entry!["listingId"]?.GetValue<Guid?>()).ToHashSet();

        Assert.Contains(approvedListing, listings);
        Assert.DoesNotContain(pendingListing, listings);
    }

    [Fact]
    public async Task TheBrokerageTrail_ForAnAgentWhoIsNotItsAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("brokerage-audit-owner");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("brokerage-audit-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");

        using var client = factory.CreateClientFor(member);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(BrokerageAudit(brokerage))).StatusCode);
    }

    private async Task<JsonArray> EventsAsync(Guid caller, Guid listing, string query = "")
    {
        using var client = factory.CreateClientFor(caller);

        return (await client.GetFromJsonAsync<JsonArray>(Audit(listing) + query))!;
    }

    private async Task<(Guid Admin, Guid Member, Guid Listing)> BrokerageListingAsync(string label, string memberStatus)
    {
        var admin = await TestAgents.CreateAsync(label + "-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await ComplianceTesting.CompleteMlsAsync());
        var member = await TestAgents.CreateAsync(label + "-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, memberStatus);

        return (admin, member, TestListings.IdOf(await TestListings.CreateAsync(factory, member)));
    }
}
