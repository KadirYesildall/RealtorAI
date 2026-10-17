using System.Net;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>T117 — <c>GET /api/listings/{id}/compliance</c> with its severity and status filters.</summary>
public sealed class ComplianceListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ComplianceListTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task List_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Compliance(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task List_BeforeAnyRun_IsEmpty()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "findings-empty", ViolatingRemarks);

        Assert.Empty(await FindingsAsync(factory, agent, listing));
    }

    [Fact]
    public async Task List_FiltersBySeverity()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "findings-severity", ViolatingRemarks);
        await CheckAsync(factory, agent, listing);

        var blocking = await FindingsAsync(factory, agent, listing, "?severity=blocking");
        var warnings = await FindingsAsync(factory, agent, listing, "?severity=warning");

        Assert.NotEmpty(blocking);
        Assert.NotEmpty(warnings);
        Assert.All(blocking, finding => Assert.Equal("blocking", finding!["severity"]!.GetValue<string>()));
        Assert.All(warnings, finding => Assert.Equal("warning", finding!["severity"]!.GetValue<string>()));
        Assert.All(blocking, finding => Assert.False(finding!["canAcknowledge"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(factory, "findings-status", ViolatingRemarks);
        await CheckAsync(factory, agent, listing);
        await TestDatabase.ExecuteAsync("update public.compliance_findings set status = 'resolved' where target_id = $1 and check_type = 'mls_rule'", draft);

        var resolved = await FindingsAsync(factory, agent, listing, "?status=resolved");
        var open = await FindingsAsync(factory, agent, listing, "?status=open");

        Assert.Equal("mls.forbidden.phone_number", Assert.Single(resolved)!["ruleId"]!.GetValue<string>());
        Assert.DoesNotContain(open, finding => finding!["ruleId"]!.GetValue<string>() == "mls.forbidden.phone_number");
    }

    /// <summary>Ordered for review: blocking first, then by item and position in the text.</summary>
    [Fact]
    public async Task List_PutsBlockingFindingsFirst_ThenOrdersBySpan()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "findings-order", ViolatingRemarks);
        await CheckAsync(factory, agent, listing);

        var findings = (await FindingsAsync(factory, agent, listing)).Select(finding => finding!.AsObject()).ToList();
        var severities = findings.Select(finding => finding["severity"]!.GetValue<string>()).ToList();

        Assert.Equal(severities.OrderBy(severity => severity == "blocking" ? 0 : 1).ToList(), severities);
    }

    [Theory]
    [InlineData("?severity=urgent")]
    [InlineData("?status=dismissed")]
    public async Task List_WithAnUnknownFilter_Is422(string query)
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "findings-bad-filter", "A bright home.");
        using var client = factory.CreateClientFor(agent);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(Compliance(listing) + query)).StatusCode);
    }

    [Fact]
    public async Task List_ForTheApprovedBrokerAdmin_Is200_AndForAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("findings-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("findings-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(factory, member));
        var stranger = await TestAgents.CreateAsync("findings-stranger");

        using var adminClient = factory.CreateClientFor(admin);
        using var strangerClient = factory.CreateClientFor(stranger);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(Compliance(listing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(Compliance(listing))).StatusCode);
    }
}
