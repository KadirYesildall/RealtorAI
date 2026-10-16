using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T069 — <c>PUT /api/listings/{id}/fields</c> and <c>POST .../fields/{fieldId}/confirm</c>
/// (FR-005, FR-006, FR-007, FR-008, FR-066).
///
/// <para>
/// <b>What is at stake.</b> Every value carries where it came from, and two kinds of value are
/// never AI's to supply: measurements and legal facts. The database refuses those combinations
/// too (FieldProvenanceCheckTests); this suite proves the API refuses them first, with a 422 a
/// client can act on rather than a constraint error.
/// </para>
/// <para>
/// <b>Creating versus updating.</b> <c>If-Match: "0"</c> means "this field does not exist yet".
/// Row versions start at 1, so 0 can never match a stored field, and a create that races
/// another create for the same key is refused instead of silently becoming an update.
/// </para>
/// </summary>
public sealed class FieldUpsertTests : IClassFixture<ApiFactory>
{
    /// <summary>FR-007's keys, exactly as listing_fields_no_ai_measurements lists them.</summary>
    public static TheoryData<string> MeasurementAndLegalKeys { get; } = new()
    {
        "LivingArea",
        "LivingAreaSquareFeet",
        "LotSizeSquareFeet",
        "LotSizeAcres",
        "YearBuilt",
        "TaxAnnualAmount",
        "TaxYear",
        "AssociationFee",
        "AssociationFeeFrequency",
        "HighSchoolDistrict",
        "ElementarySchoolDistrict",
        "MiddleOrJuniorSchoolDistrict",
        "Zoning",
    };

    private readonly ApiFactory factory;

    public FieldUpsertTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private static JsonObject Field(string key, JsonNode? value, string provenance, string? sourceNote = null) => new()
    {
        ["resoFieldKey"] = key,
        ["value"] = value,
        ["provenance"] = provenance,
        ["sourceNote"] = sourceNote,
    };

    private async Task<HttpResponseMessage> PutAsync(Guid caller, Guid listingId, JsonObject body, int? ifMatch)
    {
        using var client = factory.CreateClientFor(caller);
        using var request = Conditional(HttpMethod.Put, Fields(listingId), body, ifMatch);

        return await client.SendAsync(request);
    }

    private async Task<JsonObject> PutOkAsync(Guid caller, Guid listingId, JsonObject body, int ifMatch)
    {
        var response = await PutAsync(caller, listingId, body, ifMatch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private async Task<(Guid Agent, Guid Listing)> NewListingAsync(string label)
    {
        var agent = await TestAgents.CreateAsync(label);

        return (agent, IdOf(await CreateAsync(factory, agent)));
    }

    private static Task<long> FieldCountAsync(Guid listingId, string key) =>
        TestDatabase.ScalarAsync<long>(
            "select count(*) from public.listing_fields where listing_id = $1 and reso_field_key = $2",
            listingId,
            key);

    // ═════════════════════════════ PUT /fields ═════════════════════════════

    [Fact]
    public async Task Upsert_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();
        using var request = Conditional(HttpMethod.Put, Fields(Guid.NewGuid()), Field("LivingArea", 2840, "agent_entered"), 0);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_ANewField_StoresItWithItsProvenance()
    {
        var (agent, listing) = await NewListingAsync("field-new");

        var response = await PutAsync(agent, listing, Field("InteriorFeatures", new JsonArray("Fireplace", "VaultedCeilings"), "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);

        var field = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("InteriorFeatures", field["resoFieldKey"]!.GetValue<string>());
        Assert.Equal("agent_entered", field["provenance"]!.GetValue<string>());
        Assert.Equal(["Fireplace", "VaultedCeilings"], field["value"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.False(field["confirmed"]!.GetValue<bool>());
        Assert.False(field["requiredBeforeApproval"]!.GetValue<bool>());
        Assert.Equal(1, field["rowVersion"]!.GetValue<int>());

        Assert.Equal(
            "agent_entered",
            await TestDatabase.ScalarAsync<string>(
                "select provenance::text from public.listing_fields where listing_id = $1 and reso_field_key = 'InteriorFeatures'",
                listing));
    }

    /// <summary>FR-007, the core of this suite: no AI provenance on any measurement or legal key.</summary>
    [Theory]
    [MemberData(nameof(MeasurementAndLegalKeys))]
    public async Task Upsert_AMeasurementOrLegalFieldWithAiProvenance_Is422(string key)
    {
        var (agent, listing) = await NewListingAsync("field-fr007");

        foreach (var provenance in new[] { "ai_detected_from_photo", "ai_suggested" })
        {
            var response = await PutAsync(agent, listing, Field(key, 1, provenance), 0);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.NotNull(problem["errors"]!["provenance"]);
        }

        Assert.Equal(0L, await FieldCountAsync(listing, key));
    }

    /// <summary>The same keys are fine when an agent or a public record supplies them.</summary>
    [Fact]
    public async Task Upsert_AMeasurementFieldEnteredByTheAgent_IsAccepted()
    {
        var (agent, listing) = await NewListingAsync("field-measure");

        var field = await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        Assert.Equal(2840, field["value"]!.GetValue<int>());
        Assert.True(field["requiredBeforeApproval"]!.GetValue<bool>());
    }

    /// <summary>FR-008: a public-record value must say where it came from and when.</summary>
    [Fact]
    public async Task Upsert_APublicRecordValueWithoutItsSource_Is422()
    {
        var (agent, listing) = await NewListingAsync("field-public-record");

        var response = await PutAsync(agent, listing, Field("YearBuilt", 1998, "public_record"), 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]!["sourceNote"]);

        var accepted = await PutOkAsync(agent, listing, Field("YearBuilt", 1998, "public_record", "County assessor, retrieved 2026-10-16"), 0);
        Assert.Equal("County assessor, retrieved 2026-10-16", accepted["sourceNote"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("LivingArea", "\"large\"")]
    [InlineData("LivingArea", "-10")]
    [InlineData("LotSizeSquareFeet", "true")]
    [InlineData("YearBuilt", "1998.5")]
    [InlineData("YearBuilt", "1200")]
    [InlineData("TaxAnnualAmount", "\"$12,000\"")]
    public async Task Upsert_AMeasurementWithAValueThatIsNotAPlausibleNumber_Is422(string key, string json)
    {
        var (agent, listing) = await NewListingAsync("field-bad-number");

        var response = await PutAsync(agent, listing, Field(key, JsonNode.Parse(json), "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]!["value"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("livingArea")]
    [InlineData("Living Area")]
    [InlineData("Living-Area")]
    public async Task Upsert_WithAKeyThatIsNotARESOFieldName_Is422(string key)
    {
        var (agent, listing) = await NewListingAsync("field-bad-key");

        var response = await PutAsync(agent, listing, Field(key, "x", "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]!["resoFieldKey"]);
    }

    [Fact]
    public async Task Upsert_WithANullValue_Is422()
    {
        var (agent, listing) = await NewListingAsync("field-null");

        var response = await PutAsync(agent, listing, Field("InteriorFeatures", null, "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_WithAnUnknownProvenance_Is422()
    {
        var (agent, listing) = await NewListingAsync("field-provenance");

        var response = await PutAsync(agent, listing, Field("InteriorFeatures", "Fireplace", "guessed"), 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_AnExistingFieldAtItsVersion_UpdatesIt()
    {
        var (agent, listing) = await NewListingAsync("field-update");
        await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        var updated = await PutOkAsync(agent, listing, Field("LivingArea", 2900, "agent_entered"), 1);

        Assert.Equal(2900, updated["value"]!.GetValue<int>());
        Assert.Equal(2, updated["rowVersion"]!.GetValue<int>());
        Assert.Equal(1L, await FieldCountAsync(listing, "LivingArea"));
    }

    /// <summary>FR-066 for fields: the second of two concurrent edits is refused with what the field now holds.</summary>
    [Fact]
    public async Task Upsert_AtAStaleVersion_Is409WithTheCurrentState()
    {
        var (agent, listing) = await NewListingAsync("field-stale");
        await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);
        await PutOkAsync(agent, listing, Field("LivingArea", 2900, "agent_entered"), 1);

        var stale = await PutAsync(agent, listing, Field("LivingArea", 3000, "agent_entered"), 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = (await stale.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(2, problem["currentRowVersion"]!.GetValue<int>());
        Assert.Equal(2900, problem["current"]!["value"]!.GetValue<int>());
    }

    /// <summary>Two people creating the same field: the second is told it now exists.</summary>
    [Fact]
    public async Task Upsert_CreatingAFieldThatAlreadyExists_Is409()
    {
        var (agent, listing) = await NewListingAsync("field-create-race");
        await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        var second = await PutAsync(agent, listing, Field("LivingArea", 2500, "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = (await second.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, problem["currentRowVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Upsert_UpdatingAFieldThatDoesNotExist_Is409()
    {
        var (agent, listing) = await NewListingAsync("field-update-missing");

        var response = await PutAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 3);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await FieldCountAsync(listing, "LivingArea"));
    }

    [Fact]
    public async Task Upsert_WithoutIfMatch_Is428()
    {
        var (agent, listing) = await NewListingAsync("field-no-precondition");

        var response = await PutAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    /// <summary>FR-049: the trail shows the value and provenance that were replaced.</summary>
    [Fact]
    public async Task Upsert_IsAuditedWithBeforeAndAfterValues()
    {
        var (agent, listing) = await NewListingAsync("field-audit");
        var created = await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);
        await PutOkAsync(agent, listing, Field("LivingArea", 2900, "agent_entered"), 1);

        var fieldId = created["id"]!.GetValue<Guid>();

        Assert.Equal(2L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'field_edit' and listing_id = $1 and target_type = 'listing_field' and target_id = $2
              and actor_id = $3
            """,
            listing,
            fieldId,
            agent));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where action = 'field_edit' and target_id = $1
              and (before_value -> 'value')::text = '2840' and (after_value -> 'value')::text = '2900'
              and after_value ->> 'provenance' = 'agent_entered'
            """,
            fieldId));
    }

    /// <summary>FR-008: once entered, a required field is no longer reported missing.</summary>
    [Fact]
    public async Task Upsert_ARequiredField_RemovesItFromTheMissingList()
    {
        var (agent, listing) = await NewListingAsync("field-required");
        await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        using var client = factory.CreateClientFor(agent);
        var detail = (await client.GetFromJsonAsync<JsonObject>(Listing(listing)))!;

        var missing = detail["missingRequiredFieldKeys"]!.AsArray().Select(key => key!.GetValue<string>()).ToList();
        Assert.DoesNotContain("LivingArea", missing);
        Assert.Contains("YearBuilt", missing);
        Assert.Single(detail["fields"]!.AsArray());
    }

    [Fact]
    public async Task Upsert_ByAnUnrelatedAgent_Is404()
    {
        var (_, listing) = await NewListingAsync("field-owner");
        var stranger = await TestAgents.CreateAsync("field-stranger");

        var response = await PutAsync(stranger, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("field-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("field-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));

        var response = await PutAsync(admin, listing, Field("LivingArea", 2840, "agent_entered"), 0);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ═════════════════════════════ POST /fields/{id}/confirm ═════════════════════════════

    /// <summary>FR-006: confirmation is the human act that takes responsibility for an AI-derived value.</summary>
    [Fact]
    public async Task Confirm_AnAiDerivedField_RecordsWhoConfirmedAndWhen()
    {
        var (agent, listing) = await NewListingAsync("confirm");
        var field = await PutOkAsync(agent, listing, Field("InteriorFeatures", new JsonArray("Fireplace"), "ai_detected_from_photo"), 0);
        var fieldId = field["id"]!.GetValue<Guid>();
        Assert.False(field["confirmed"]!.GetValue<bool>());

        using var client = factory.CreateClientFor(agent);
        var response = await client.PostAsync(Confirm(listing, fieldId), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var confirmed = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(confirmed["confirmed"]!.GetValue<bool>());
        Assert.NotNull(confirmed["confirmedAt"]);
        Assert.Equal(2, confirmed["rowVersion"]!.GetValue<int>());

        Assert.Equal(agent, await TestDatabase.ScalarAsync<Guid>("select confirmed_by from public.listing_fields where id = $1", fieldId));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'field_confirm' and target_id = $1 and actor_id = $2",
            fieldId,
            agent));
    }

    [Fact]
    public async Task Confirm_Twice_IsIdempotent()
    {
        var (agent, listing) = await NewListingAsync("confirm-twice");
        var field = await PutOkAsync(agent, listing, Field("InteriorFeatures", "Fireplace", "ai_suggested"), 0);
        var fieldId = field["id"]!.GetValue<Guid>();
        using var client = factory.CreateClientFor(agent);

        await client.PostAsync(Confirm(listing, fieldId), content: null);
        var again = await client.PostAsync(Confirm(listing, fieldId), content: null);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where action = 'field_confirm' and target_id = $1",
            fieldId));
    }

    /// <summary>Only AI-derived values need a human to vouch for them; an agent's own entry is already theirs.</summary>
    [Fact]
    public async Task Confirm_AnAgentEnteredField_Is422()
    {
        var (agent, listing) = await NewListingAsync("confirm-agent-entered");
        var field = await PutOkAsync(agent, listing, Field("LivingArea", 2840, "agent_entered"), 0);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsync(Confirm(listing, field["id"]!.GetValue<Guid>()), content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>A confirmation covers the value that was confirmed. A new value needs a new one.</summary>
    [Fact]
    public async Task Upsert_AConfirmedFieldWithANewValue_ClearsTheConfirmation()
    {
        var (agent, listing) = await NewListingAsync("confirm-then-edit");
        var field = await PutOkAsync(agent, listing, Field("InteriorFeatures", "Fireplace", "ai_suggested"), 0);
        var fieldId = field["id"]!.GetValue<Guid>();
        using (var client = factory.CreateClientFor(agent))
        {
            await client.PostAsync(Confirm(listing, fieldId), content: null);
        }

        var edited = await PutOkAsync(agent, listing, Field("InteriorFeatures", "WetBar", "ai_suggested"), 2);

        Assert.False(edited["confirmed"]!.GetValue<bool>());
        Assert.Null(edited["confirmedAt"]);
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select confirmed_by is null from public.listing_fields where id = $1",
            fieldId));
    }

    /// <summary>Confirmation is the owner's act and cannot be delegated, even to an approved broker admin.</summary>
    [Fact]
    public async Task Confirm_ByTheApprovedBrokerAdmin_Is403()
    {
        var admin = await TestAgents.CreateAsync("confirm-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin);
        var member = await TestAgents.CreateAsync("confirm-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(factory, member));
        var field = await PutOkAsync(member, listing, Field("InteriorFeatures", "Fireplace", "ai_suggested"), 0);
        using var client = factory.CreateClientFor(admin);

        var response = await client.PostAsync(Confirm(listing, field["id"]!.GetValue<Guid>()), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A field id from a different listing is not found through this one.</summary>
    [Fact]
    public async Task Confirm_AFieldOfAnotherListing_Is404()
    {
        var (agent, listing) = await NewListingAsync("confirm-cross");
        var other = IdOf(await CreateAsync(factory, agent));
        var field = await PutOkAsync(agent, other, Field("InteriorFeatures", "Fireplace", "ai_suggested"), 0);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsync(Confirm(listing, field["id"]!.GetValue<Guid>()), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
