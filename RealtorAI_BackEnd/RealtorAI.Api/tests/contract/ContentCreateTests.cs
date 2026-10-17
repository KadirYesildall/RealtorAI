using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// <c>POST /api/listings/{id}/content</c> — content written by hand (SC-010, FR-054; research R35).
///
/// <para>
/// <b>What is at stake.</b> SC-010 promises a listing can be completed and published entirely
/// by hand during a total AI outage, and every registry entry is disabled until its evaluation
/// passes. Without a way to write the description by hand, the manual path would stop at the
/// copy. So this works on the plain host, with no model enabled at all.
/// </para>
/// </summary>
public sealed class ContentCreateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ContentCreateTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Create_WithNoModelEnabled_StoresTheAgentsWordsAsTheSelectedDraft()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "manual-content", await VerifiedMlsAsync(publicLimit: 300));

        var response = await CreateAsync(agent, listing, new JsonObject { ["kind"] = "public_remarks", ["body"] = "A bright home on a quiet street.", ["tone"] = "warm" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var draft = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("public_remarks", draft["kind"]!.GetValue<string>());
        Assert.Equal("A bright home on a quiet street.", draft["body"]!.GetValue<string>());
        Assert.Equal("warm", draft["tone"]!.GetValue<string>());
        Assert.Equal("draft", draft["status"]!.GetValue<string>());
        Assert.True(draft["selected"]!.GetValue<bool>());
        Assert.Empty(draft["supportingFieldIds"]!.AsArray());
        Assert.Equal(300, draft["characterLimit"]!.GetValue<int>());
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.content_drafts
            where listing_id = $1 and model_id is null and prompt_version is null
            """,
            listing));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where listing_id = $1 and action = 'content_edited' and actor_id = $2",
            listing,
            agent));
        Assert.Single(OfKind(await DraftsAsync(factory, agent, listing), "public_remarks"));
    }

    /// <summary>The manual path does not wait on a verified profile; compliance does the checking (Story 4).</summary>
    [Fact]
    public async Task Create_AgainstTheUnverifiedCrmlsProfile_IsAccepted_WithNoLimitShown()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "manual-crmls", "CRMLS");

        var response = await CreateAsync(agent, listing, new JsonObject { ["kind"] = "headline", ["body"] = "Light-filled home in Irvine" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<JsonObject>())!["characterLimit"]);
    }

    /// <summary>One current draft set per kind: once there is one, the agent edits it.</summary>
    [Fact]
    public async Task Create_WhenTheKindAlreadyHasADraft_Is409()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "manual-twice", await VerifiedMlsAsync());
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(agent, listing, new JsonObject { ["kind"] = "headline", ["body"] = "First" })).StatusCode);

        var second = await CreateAsync(agent, listing, new JsonObject { ["kind"] = "headline", ["body"] = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("""{ "kind": "headline" }""")]
    [InlineData("""{ "kind": "headline", "body": "  " }""")]
    [InlineData("""{ "kind": "brochure", "body": "Words" }""")]
    [InlineData("""{ "kind": "headline", "body": "Words", "tone": "shouty" }""")]
    public async Task Create_WithAnInvalidRequest_Is422(string body)
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "manual-invalid", await VerifiedMlsAsync());
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsync(Content(listing), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Create_OverTheLimit_Is422()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "manual-limit", await VerifiedMlsAsync(publicLimit: 50));

        var response = await CreateAsync(agent, listing, new JsonObject { ["kind"] = "public_remarks", ["body"] = new string('a', 51) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Create_ByTheApprovedBrokerAdmin_Is403_AndByAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("manual-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await VerifiedMlsAsync());
        var member = await TestAgents.CreateAsync("manual-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await TestListings.CreateAsync(factory, member));
        var stranger = await TestAgents.CreateAsync("manual-stranger");
        var body = new JsonObject { ["kind"] = "headline", ["body"] = "Words" };

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(admin, listing, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(stranger, listing, body)).StatusCode);
    }

    private async Task<HttpResponseMessage> CreateAsync(Guid caller, Guid listing, JsonObject body)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PostAsJsonAsync(Content(listing), body);
    }
}
