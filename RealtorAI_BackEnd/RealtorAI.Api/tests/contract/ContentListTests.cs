using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T102 — <c>GET /api/listings/{id}/content</c> (FR-013, FR-014; Story 3 scenarios 1, 4, 6).
///
/// <para>
/// <b>What is at stake.</b> Each draft carries what it rests on: the fields its claims cite, and
/// the spans resting on an AI-derived fact nobody has confirmed — which the review screen
/// highlights and the approval gate refuses (FR-014). The spans are computed when the draft is
/// read, so confirming a fact clears them without regenerating anything.
/// </para>
/// </summary>
public sealed class ContentListTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public ContentListTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableContentModel(factory);
    }

    [Fact]
    public async Task List_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Content(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task List_BeforeAnyGeneration_IsEmpty()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "content-empty", await VerifiedMlsAsync());

        Assert.Empty(await DraftsAsync(factory, agent, listing));
    }

    /// <summary>Scenario 4: two or three alternatives of the description, each in the contract shape.</summary>
    [Fact]
    public async Task List_AfterGeneration_ShowsTheVariantsInTheContractShape()
    {
        var mls = await VerifiedMlsAsync(publicLimit: 600);
        var (agent, listing) = await ListingOnMlsAsync(factory, "content-shape", mls);

        var drafts = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks", "warm"), "public_remarks").ToList();

        Assert.Equal(3, drafts.Count);
        Assert.Equal([0, 1, 2], drafts.Select(draft => draft["variantIndex"]!.GetValue<int>()));

        foreach (var draft in drafts)
        {
            var body = draft["body"]!.GetValue<string>();

            Assert.Equal("warm", draft["tone"]!.GetValue<string>());
            Assert.Equal("draft", draft["status"]!.GetValue<string>());
            Assert.Equal(1, draft["version"]!.GetValue<int>());
            Assert.Equal(1, draft["rowVersion"]!.GetValue<int>());
            Assert.Equal(body.Length, draft["characterCount"]!.GetValue<int>());
            Assert.Equal(600, draft["characterLimit"]!.GetValue<int>());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), draft["contentHash"]!.GetValue<string>());
            Assert.False(draft["selected"]!.GetValue<bool>());
        }
    }

    /// <summary>
    /// FR-014: a sentence citing an unconfirmed AI-detected fact is a claim to highlight, with its
    /// exact span; confirming the fact clears it on the next read.
    /// </summary>
    [Fact]
    public async Task List_MarksClaimsRestingOnUnconfirmedAiFacts_UntilTheyAreConfirmed()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "content-claims", await VerifiedMlsAsync());
        var field = await AiFeatureFieldAsync(factory, agent, listing);

        var draft = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").First();
        var body = draft["body"]!.GetValue<string>();
        var claim = draft["unconfirmedClaims"]!.AsArray().Single()!.AsObject();

        Assert.Contains(field, draft["supportingFieldIds"]!.AsArray().Select(id => id!.GetValue<Guid>()));
        Assert.Equal(field, claim["fieldId"]!.GetValue<Guid>());
        Assert.Equal(
            "Vaulted ceilings open up the living room.",
            body[claim["spanStart"]!.GetValue<int>()..claim["spanEnd"]!.GetValue<int>()]);

        using (var client = factory.CreateClientFor(agent))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Confirm(listing, field), content: null)).StatusCode);
        }

        var after = OfKind(await DraftsAsync(factory, agent, listing), "public_remarks").First();
        Assert.Empty(after["unconfirmedClaims"]!.AsArray());
    }

    /// <summary>Single-version kinds are selected for the agent; a choice between alternatives is theirs to make.</summary>
    [Fact]
    public async Task List_SelectsTheOnlyDraftOfASingleVariantKind()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "content-private", await VerifiedMlsAsync());

        var drafts = OfKind(await GenerateAsync(factory, agent, listing, "private_remarks"), "private_remarks").ToList();

        Assert.True(Assert.Single(drafts)["selected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task List_ForTheApprovedBrokerAdmin_Is200_AndForAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("content-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await VerifiedMlsAsync());
        var member = await TestAgents.CreateAsync("content-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        var stranger = await TestAgents.CreateAsync("content-stranger");

        using var adminClient = factory.CreateClientFor(admin);
        using var strangerClient = factory.CreateClientFor(stranger);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(Content(listing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(Content(listing))).StatusCode);
    }

    /// <summary>Regenerating supersedes: the list shows the newest version of each kind only.</summary>
    [Fact]
    public async Task List_AfterRegenerating_ShowsOnlyTheNewestVersion()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "content-versions", await VerifiedMlsAsync());
        await GenerateAsync(factory, agent, listing, "headline");

        var drafts = OfKind(await GenerateAsync(factory, agent, listing, "headline", "luxury"), "headline").ToList();

        Assert.All(drafts, draft => Assert.Equal(2, draft["version"]!.GetValue<int>()));
        Assert.All(drafts, draft => Assert.Equal("luxury", draft["tone"]!.GetValue<string>()));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(distinct version) from public.content_drafts where listing_id = $1 and kind = 'headline' and status = 'stale'",
            listing));
    }
}
