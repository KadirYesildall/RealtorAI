using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Media;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.PipelineTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T086 — <c>PATCH /api/listings/{id}/photos/{photoId}</c>: the agent's overrides of analysis
/// and the altered-photo declaration; and <c>DELETE</c> of a photo (FR-001, FR-011, FR-020;
/// Story 2 scenario 7).
///
/// <para>
/// <b>What is at stake.</b> Every AI result about a photo must be overridable by the agent, and
/// the override must be the value that counts while the model's original stays on record
/// (FR-011). The altered declaration is California's AB 723 obligation: an altered photo is
/// publishable only with its alteration type, an approved disclosure label, and its unaltered
/// original beside it — all three or none (FR-020). With no approved label configured for the
/// MLS — CRMLS's labels await legal review (T161) — the declaration is refused rather than
/// accepted with a guessed label.
/// </para>
/// <para>
/// <b>PATCH semantics.</b> Absent means unchanged. <c>roomType: null</c> or
/// <c>featureTags: null</c> clears the agent's override and returns to the model's value.
/// </para>
/// </summary>
public sealed class PhotoPatchTests : IClassFixture<ApiFactory>
{
    private const string Label = "Virtually staged — furniture added digitally";

    private readonly ApiFactory factory;

    public PhotoPatchTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<HttpResponseMessage> PatchAsync(Guid caller, Guid listing, Guid photo, JsonObject body)
    {
        using var client = factory.CreateClientFor(caller);

        return await client.PatchAsJsonAsync(Photo(listing, photo), body);
    }

    private async Task<JsonObject> PatchOkAsync(Guid caller, Guid listing, Guid photo, JsonObject body)
    {
        var response = await PatchAsync(caller, listing, photo, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>A listing targeting a test MLS whose rule profile approves one disclosure label.</summary>
    private async Task<(Guid Agent, Guid Listing, List<Guid> Photos)> ListingWithApprovedLabelAsync(string label)
    {
        var mls = "TEST-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await TestDatabase.ExecuteAsync(
            """
            insert into public.mls_rule_profiles (id, display_name, allowed_alteration_types, disclosure_labels)
            values ($1, 'Contract test MLS', '["virtual_staging"]'::jsonb, $2::jsonb)
            """,
            mls,
            new JsonArray(Label).ToJsonString());

        var agent = await TestAgents.CreateAsync(label);
        await TestBrokerages.CreateAsync(agent, targetMlsId: mls);
        var listing = IdOf(await CreateAsync(factory, agent));
        var photos = await UploadAsync(factory, agent, listing, TestImages.Jpeg(), TestImages.Jpeg());

        return (agent, listing, photos);
    }

    private static JsonObject Altered(Guid pairedOriginal, string type = "virtual_staging", string label = Label) => new()
    {
        ["altered"] = true,
        ["alterationType"] = type,
        ["disclosureLabel"] = label,
        ["pairedOriginalId"] = pairedOriginal,
    };

    // ═════════════════════════════ Overrides ═════════════════════════════

    [Fact]
    public async Task Patch_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PatchAsJsonAsync(Photo(Guid.NewGuid(), Guid.NewGuid()), new JsonObject { ["caption"] = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>FR-011: the agent's room type is the one that counts, and the change is audited.</summary>
    [Fact]
    public async Task Patch_RoomType_OverridesItAndAudits()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "patch-room", 1);

        var photo = await PatchOkAsync(agent, listing, photos[0], new JsonObject { ["roomType"] = "kitchen" });

        Assert.Equal("kitchen", photo["roomType"]!.GetValue<string>());
        Assert.True(photo["roomTypeOverridden"]!.GetValue<bool>());
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'photo_override' and listing_id = $1 and target_type = 'photo' and target_id = $2
              and actor_id = $3 and after_value ->> 'roomType' = 'kitchen'
            """,
            listing,
            photos[0],
            agent));
    }

    [Fact]
    public async Task Patch_FeatureTagsAndCaption_ReplaceThem()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "patch-tags", 1);

        var photo = await PatchOkAsync(agent, listing, photos[0], new JsonObject
        {
            ["featureTags"] = new JsonArray("Island", " Pendant lights "),
            ["caption"] = "  Open kitchen with an island  ",
        });

        Assert.Equal(["Island", "Pendant lights"], photo["featureTags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.True(photo["featureTagsOverridden"]!.GetValue<bool>());
        Assert.Equal("Open kitchen with an island", photo["caption"]!.GetValue<string>());
    }

    /// <summary>Clearing an override returns to the model's value; nothing about the model's output was ever changed.</summary>
    [Fact]
    public async Task Patch_RoomTypeNull_ClearsTheOverride()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "patch-clear", 1);
        await PatchOkAsync(agent, listing, photos[0], new JsonObject { ["roomType"] = "kitchen" });

        var photo = await PatchOkAsync(agent, listing, photos[0], new JsonObject { ["roomType"] = null });

        Assert.False(photo["roomTypeOverridden"]!.GetValue<bool>());
        Assert.Null(photo["roomType"]);
    }

    [Theory]
    [InlineData("roomType", "\"ballroom\"")]
    [InlineData("featureTags", "[\"\"]")]
    [InlineData("featureTags", "\"Island\"")]
    [InlineData("caption", "42")]
    public async Task Patch_WithAnInvalidValue_Is422(string field, string json)
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "patch-invalid", 1);

        var response = await PatchAsync(agent, listing, photos[0], new JsonObject { [field] = JsonNode.Parse(json) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Patch_WithTooManyTags_Is422()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "patch-many-tags", 1);
        var tags = new JsonArray(Enumerable.Range(0, 21).Select(index => (JsonNode)JsonValue.Create($"Tag {index}")).ToArray());

        var response = await PatchAsync(agent, listing, photos[0], new JsonObject { ["featureTags"] = tags });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ═════════════════════════════ Altered declaration (FR-020) ═════════════════════════════

    [Fact]
    public async Task Patch_DeclaringAnAlterationWithAllThreeParts_RecordsIt()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-altered");

        var photo = await PatchOkAsync(agent, listing, photos[1], Altered(photos[0]));

        Assert.True(photo["altered"]!.GetValue<bool>());
        Assert.Equal("virtual_staging", photo["alterationType"]!.GetValue<string>());
        Assert.Equal(Label, photo["disclosureLabel"]!.GetValue<string>());
        Assert.Equal(photos[0], photo["pairedOriginalId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task Patch_UndeclaringAnAlteration_ClearsAllThreeParts()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-undeclare");
        await PatchOkAsync(agent, listing, photos[1], Altered(photos[0]));

        var photo = await PatchOkAsync(agent, listing, photos[1], new JsonObject { ["altered"] = false });

        Assert.False(photo["altered"]!.GetValue<bool>());
        Assert.Null(photo["alterationType"]);
        Assert.Null(photo["disclosureLabel"]);
        Assert.Null(photo["pairedOriginalId"]);
    }

    [Theory]
    [InlineData("alterationType")]
    [InlineData("disclosureLabel")]
    [InlineData("pairedOriginalId")]
    public async Task Patch_DeclaringAnAlterationWithAPartMissing_Is422NamingIt(string missing)
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-partial");
        var body = Altered(photos[0]);
        body.Remove(missing);

        var response = await PatchAsync(agent, listing, photos[1], body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]![missing]);
    }

    /// <summary>The label must be one the MLS's rule profile approves — never free text.</summary>
    [Fact]
    public async Task Patch_WithALabelTheRuleProfileDoesNotApprove_Is422()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-label");

        var response = await PatchAsync(agent, listing, photos[1], Altered(photos[0], label: "Edited"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]!["disclosureLabel"]);
    }

    /// <summary>
    /// CRMLS's approved labels are pending legal review (T161), so its profile has none — and an
    /// alteration cannot be declared at all rather than declared with a label nobody approved.
    /// </summary>
    [Fact]
    public async Task Patch_DeclaringAnAlterationWhereNoLabelIsApprovedYet_Is422()
    {
        var agent = await TestAgents.CreateAsync("patch-crmls");
        await TestBrokerages.CreateAsync(agent, targetMlsId: "CRMLS");
        var listing = IdOf(await CreateAsync(factory, agent));
        var photos = await UploadAsync(factory, agent, listing, TestImages.Jpeg(), TestImages.Jpeg());

        var response = await PatchAsync(agent, listing, photos[1], Altered(photos[0]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(await TestDatabase.ScalarAsync<bool>("select altered from public.photos where id = $1", photos[1]));
    }

    [Fact]
    public async Task Patch_PairingAPhotoWithItself_Is422()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-self");

        var response = await PatchAsync(agent, listing, photos[1], Altered(photos[1]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>The unaltered original must be on the same listing, to be placed beside it.</summary>
    [Fact]
    public async Task Patch_PairingAPhotoFromAnotherListing_Is422()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-cross");
        var other = IdOf(await CreateAsync(factory, agent));
        var foreign = (await UploadAsync(factory, agent, other, TestImages.Jpeg()))[0];

        var response = await PatchAsync(agent, listing, photos[1], Altered(foreign));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>The paired original must itself be unaltered — it is the evidence of what was changed.</summary>
    [Fact]
    public async Task Patch_PairingWithAnAlteredPhoto_Is422()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("patch-altered-pair");
        var third = (await UploadAsync(factory, agent, listing, TestImages.Jpeg()))[0];
        await PatchOkAsync(agent, listing, photos[1], Altered(photos[0]));

        var response = await PatchAsync(agent, listing, third, Altered(photos[1]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ═════════════════════════════ Authorization ═════════════════════════════

    [Fact]
    public async Task Patch_ByAnUnrelatedAgent_Is404()
    {
        var (_, listing, photos) = await ListingWithPhotosAsync(factory, "patch-owner", 1);
        var stranger = await TestAgents.CreateAsync("patch-stranger");

        var response = await PatchAsync(stranger, listing, photos[0], new JsonObject { ["caption"] = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("patch-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("patch-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        var photo = (await UploadAsync(factory, member, listing, TestImages.Jpeg()))[0];

        var response = await PatchAsync(admin, listing, photo, new JsonObject { ["caption"] = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A photo id from another listing is not found through this one.</summary>
    [Fact]
    public async Task Patch_APhotoOfAnotherListing_Is404()
    {
        var (agent, listing, _) = await ListingWithPhotosAsync(factory, "patch-route", 1);
        var other = IdOf(await CreateAsync(factory, agent));
        var foreign = (await UploadAsync(factory, agent, other, TestImages.Jpeg()))[0];

        var response = await PatchAsync(agent, listing, foreign, new JsonObject { ["caption"] = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═════════════════════════════ DELETE /photos/{id} ═════════════════════════════

    /// <summary>
    /// Story 2 scenario 7: the photo leaves the listing, the deletion is audited — and the original
    /// stays in the write-once bucket (FR-001), reclaimed only by the retention purge (FR-065).
    /// </summary>
    [Fact]
    public async Task Delete_DetachesThePhotoAuditsAndKeepsTheOriginal()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "delete-photo", 2);
        var originalKey = (await TestDatabase.ScalarAsync<string>("select original_storage_key from public.photos where id = $1", photos[0]))!;
        using var client = factory.CreateClientFor(agent);

        var response = await client.DeleteAsync(Photo(listing, photos[0]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>("select count(*) from public.photos where id = $1", photos[0]));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'photo_delete' and listing_id = $1 and target_id = $2 and actor_id = $3
              and before_value ->> 'originalStorageKey' = $4
            """,
            listing,
            photos[0],
            agent,
            originalKey));

        var storage = factory.Services.GetRequiredService<IPhotoStorage>();
        Assert.NotEmpty(await storage.GetOriginalAsync(originalKey, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_TheCoverPhoto_ClearsTheCover()
    {
        var (agent, listing, photos) = await ListingWithPhotosAsync(factory, "delete-cover", 2);
        using var client = factory.CreateClientFor(agent);
        await client.PutAsJsonAsync(Order(listing), new JsonObject
        {
            ["orderedPhotoIds"] = new JsonArray(photos.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
            ["coverPhotoId"] = photos[0],
        });

        await client.DeleteAsync(Photo(listing, photos[0]));

        Assert.True(await TestDatabase.ScalarAsync<bool>("select cover_photo_id is null from public.listings where id = $1", listing));
    }

    /// <summary>The unaltered original an altered photo is paired with cannot be removed from under it.</summary>
    [Fact]
    public async Task Delete_ThePairedOriginalOfAnAlteredPhoto_Is409()
    {
        var (agent, listing, photos) = await ListingWithApprovedLabelAsync("delete-paired");
        await PatchOkAsync(agent, listing, photos[1], Altered(photos[0]));
        using var client = factory.CreateClientFor(agent);

        var response = await client.DeleteAsync(Photo(listing, photos[0]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("select count(*) from public.photos where id = $1", photos[0]));
    }

    [Fact]
    public async Task Delete_ByAnUnrelatedAgent_Is404()
    {
        var (_, listing, photos) = await ListingWithPhotosAsync(factory, "delete-owner", 1);
        var stranger = await TestAgents.CreateAsync("delete-stranger");
        using var client = factory.CreateClientFor(stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Photo(listing, photos[0]))).StatusCode);
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("select count(*) from public.photos where id = $1", photos[0]));
    }
}
