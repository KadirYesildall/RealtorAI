using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T104 — <c>PATCH /api/listings/{id}/content/{draftId}</c> (FR-025, FR-066; Story 3 scenario 5).
///
/// <para>
/// <b>What is at stake.</b> An approval is of exact words. Change one and the approval no longer
/// describes what would be published, so it is revoked and the listing goes back to review —
/// in the same transaction as the edit, so there is no moment at which edited content is
/// approved. And two tabs editing one draft must not silently overwrite each other (FR-066).
/// </para>
/// </summary>
public sealed class ContentEditTests : IClassFixture<AiEnabledApiFactory>
{
    private const string Edited = "A bright, freshly painted home on a quiet street.";

    private readonly AiEnabledApiFactory factory;

    public ContentEditTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableContentModel(factory);
    }

    [Fact]
    public async Task Edit_WithoutIfMatch_Is428()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-428");
        using var client = factory.CreateClientFor(agent);

        var response = await client.PatchAsJsonAsync(Draft(listing, Id(draft)), new JsonObject { ["body"] = Edited });

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    /// <summary>The edit is saved, versioned, hashed afresh, and audited with before and after.</summary>
    [Fact]
    public async Task Edit_SavesTheBody_AndAuditsBeforeAndAfter()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-body");

        var response = await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = Edited });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(Edited, updated["body"]!.GetValue<string>());
        Assert.Equal(2, updated["rowVersion"]!.GetValue<int>());
        Assert.Equal(Edited.Length, updated["characterCount"]!.GetValue<int>());
        Assert.NotEqual(draft["contentHash"]!.GetValue<string>(), updated["contentHash"]!.GetValue<string>());
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'content_edited' and target_type = 'content_draft' and target_id = $1 and actor_id = $2
              and before_value ->> 'body' = $3 and after_value ->> 'body' = $4
            """,
            Id(draft),
            agent,
            draft["body"]!.GetValue<string>(),
            Edited));
    }

    /// <summary>
    /// FR-014 after an edit: the claims follow the words. Removing the sentence that rested on an
    /// unconfirmed fact removes the highlight and the citation with it.
    /// </summary>
    [Fact]
    public async Task Edit_RemovingAClaimSentence_DropsItsCitationAndHighlight()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "edit-claims", await VerifiedMlsAsync());
        var field = await AiFeatureFieldAsync(factory, agent, listing);
        var draft = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").First();
        Assert.NotEmpty(draft["unconfirmedClaims"]!.AsArray());

        var body = draft["body"]!.GetValue<string>().Replace("Vaulted ceilings open up the living room.", string.Empty, StringComparison.Ordinal).Trim();
        var updated = (await (await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = body })).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Empty(updated["unconfirmedClaims"]!.AsArray());
        Assert.DoesNotContain(field, updated["supportingFieldIds"]!.AsArray().Select(id => id!.GetValue<Guid>()));
    }

    /// <summary>FR-013: an edit cannot take the body over the MLS's limit either.</summary>
    [Fact]
    public async Task Edit_OverTheLimit_Is422()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-limit", publicLimit: 200);

        var response = await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = new string('a', 201) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>FR-066: the second of two concurrent edits is refused, with what the draft now holds.</summary>
    [Fact]
    public async Task Edit_WithAStaleIfMatch_Is409WithTheCurrentDraft()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-stale");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = Edited })).StatusCode);

        var stale = await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = "Another tab's words." });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var problem = (await stale.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(2, problem["currentRowVersion"]!.GetValue<int>());
        Assert.Equal(Edited, problem["current"]!["body"]!.GetValue<string>());
    }

    /// <summary>Scenario 4: choosing a variant unselects the others of its kind — one is published.</summary>
    [Fact]
    public async Task Select_AVariant_UnselectsTheOthersOfItsKind()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "edit-select", await VerifiedMlsAsync());
        var variants = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").ToList();

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(agent, listing, variants[0], new JsonObject { ["selected"] = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(agent, listing, variants[2], new JsonObject { ["selected"] = true })).StatusCode);

        var selected = OfKind(await DraftsAsync(factory, agent, listing), "public_remarks")
            .Where(draft => draft["selected"]!.GetValue<bool>())
            .Select(Id);

        Assert.Equal([Id(variants[2])], selected);
    }

    /// <summary>
    /// FR-025: editing approved content revokes the approval and sends the listing back to review,
    /// atomically with the edit.
    /// </summary>
    [Fact]
    public async Task Edit_OfAnApprovedDraft_RevokesTheApproval_AndReturnsTheListingToReview()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-approved");
        await TestDatabase.ExecuteAsync("update public.content_drafts set status = 'approved' where id = $1", Id(draft));
        await TestDatabase.ExecuteAsync("update public.listings set status = 'approved' where id = $1", listing);
        await TestDatabase.ExecuteAsync(
            """
            insert into public.approvals (listing_id, target_type, target_id, channel, content_hash, approved_by)
            values ($1, 'content_draft', $2, 'mls', $3, $4)
            """,
            listing,
            Id(draft),
            draft["contentHash"]!.GetValue<string>(),
            agent);

        var response = await PatchAsync(agent, listing, draft, new JsonObject { ["body"] = Edited });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("draft", (await response.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.approvals where target_id = $1 and revoked_at is null",
            Id(draft)));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.approvals where target_id = $1 and revoked_reason = 'content_edited'",
            Id(draft)));
        Assert.Equal("ready_for_review", await TestDatabase.ScalarAsync<string>("select status::text from public.listings where id = $1", listing));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'approval_revoked' and target_id = $1 and actor_id = $2",
            Id(draft),
            agent));
    }

    /// <summary>A superseded draft is history, not something to edit.</summary>
    [Fact]
    public async Task Edit_OfAStaleDraft_Is409()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "edit-superseded", await VerifiedMlsAsync());
        var first = OfKind(await GenerateAsync(factory, agent, listing, "headline"), "headline").Single();
        await GenerateAsync(factory, agent, listing, "headline");

        var response = await PatchAsync(agent, listing, first, new JsonObject { ["body"] = "Old words" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "body": "   " }""")]
    public async Task Edit_WithNothingValidToChange_Is422(string body)
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-invalid");
        using var client = factory.CreateClientFor(agent);
        using var request = new HttpRequestMessage(HttpMethod.Patch, Draft(listing, Id(draft)))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{draft["rowVersion"]!.GetValue<int>()}\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Edit_ByTheApprovedBrokerAdmin_Is403_AndByAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("edit-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await VerifiedMlsAsync());
        var member = await TestAgents.CreateAsync("edit-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        var draft = OfKind(await GenerateAsync(factory, member, listing, "headline"), "headline").Single();
        var stranger = await TestAgents.CreateAsync("edit-stranger");

        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(admin, listing, draft, new JsonObject { ["body"] = Edited })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(stranger, listing, draft, new JsonObject { ["body"] = Edited })).StatusCode);
    }

    [Fact]
    public async Task Edit_OfADraftOnAnotherListing_Is404()
    {
        var (agent, listing, draft) = await PublicRemarksAsync("edit-cross");
        var other = IdOf(await CreateAsync(factory, agent));

        using var client = factory.CreateClientFor(agent);
        using var request = Conditional(HttpMethod.Patch, Draft(other, Id(draft)), new JsonObject { ["body"] = Edited }, draft["rowVersion"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        Assert.NotEqual(listing, other);
    }

    private async Task<(Guid Agent, Guid Listing, JsonObject Draft)> PublicRemarksAsync(string label, int publicLimit = 600)
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, label, await VerifiedMlsAsync(publicLimit: publicLimit));
        var draft = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").First();

        return (agent, listing, draft);
    }

    private async Task<HttpResponseMessage> PatchAsync(Guid caller, Guid listing, JsonObject draft, JsonObject body)
    {
        using var client = factory.CreateClientFor(caller);
        using var request = Conditional(HttpMethod.Patch, Draft(listing, Id(draft)), body, draft["rowVersion"]!.GetValue<int>());

        return await client.SendAsync(request);
    }

    private static Guid Id(JsonObject draft) => draft["id"]!.GetValue<Guid>();
}
