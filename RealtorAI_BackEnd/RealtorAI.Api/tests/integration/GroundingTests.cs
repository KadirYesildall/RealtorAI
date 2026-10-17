using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T106 — generated content contains no measurement, tax, school, HOA or zoning claim without a
/// valid source (FR-007, FR-014).
///
/// <para>
/// <b>What is at stake.</b> These are the claims a buyer relies on and an agent is liable for. The
/// model is asked to cite the field behind every sentence; this proves the citation is checked
/// rather than trusted. A claim of one of these kinds survives only when it cites a field of the
/// same kind, entered by the agent or taken from a public record — never one a model supplied —
/// and every figure it states is that field's value. Anything else is removed before the draft is
/// stored, so the agent never sees it to approve.
/// </para>
/// </summary>
public sealed class GroundingTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public GroundingTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableContentModel(factory);
    }

    [Fact]
    public async Task OnlyClaimsCitingAMatchingSourcedField_Survive()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "grounding", await VerifiedMlsAsync(publicLimit: 2000));
        var livingArea = await EnterAsync(agent, listing, "LivingArea", 2840);
        var yearBuilt = await EnterAsync(agent, listing, "YearBuilt", 1998);
        var feature = await AiFeatureFieldAsync(factory, agent, listing);

        var sentences = new (string Text, Guid[] Cites, bool Survives)[]
        {
            ("Welcome to a light-filled home.", [], true),
            ("It offers 2,840 square feet of living space.", [livingArea], true),
            ("Built in 1998, it has been lovingly kept.", [yearBuilt], true),
            ("It offers 3,100 square feet of living space.", [livingArea], false),
            ("The lot spans 9,000 square feet.", [], false),
            ("Annual property taxes are about $14,250.", [feature], false),
            ("Award-winning schools are a short walk away.", [], false),
            ("Low HOA dues keep costs down.", [], false),
            ("It is zoned for a second unit.", [], false),
            ("Vaulted ceilings open up the dining room.", [Guid.NewGuid()], false),
            ("Vaulted ceilings open up the living room.", [feature], true),
        };

        factory.Models.ContentAnswer = request => (string?)request["mode"] == "structured"
            ? new JsonObject { ["variants"] = new JsonArray() }
            : new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject
                {
                    ["sentences"] = new JsonArray(sentences
                        .Select(sentence => (JsonNode)new JsonObject
                        {
                            ["text"] = sentence.Text,
                            ["supportingFieldIds"] = new JsonArray(sentence.Cites.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                        })
                        .ToArray()),
                }),
            };

        try
        {
            var draft = OfKind(await GenerateAsync(factory, agent, listing, "public_remarks"), "public_remarks").Single();

            Assert.Equal(
                string.Join(' ', sentences.Where(sentence => sentence.Survives).Select(sentence => sentence.Text)),
                draft["body"]!.GetValue<string>());
            Assert.Equal(
                new[] { livingArea, yearBuilt, feature }.Order(),
                draft["supportingFieldIds"]!.AsArray().Select(id => id!.GetValue<Guid>()).Order());
        }
        finally
        {
            factory.Models.ContentAnswer = FakeChatClientFactory.DefaultContentAnswer;
        }
    }

    /// <summary>The removals are on the record, so an evaluation can measure how often the model overreaches.</summary>
    [Fact]
    public async Task RemovedSentences_AreCountedInTheGenerationAudit()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "grounding-audit", await VerifiedMlsAsync());

        factory.Models.ContentAnswer = request => (string?)request["mode"] == "structured"
            ? new JsonObject { ["variants"] = new JsonArray() }
            : new JsonObject
            {
                ["variants"] = new JsonArray(new JsonObject
                {
                    ["sentences"] = new JsonArray(
                        new JsonObject { ["text"] = "A sunny home.", ["supportingFieldIds"] = new JsonArray() },
                        new JsonObject { ["text"] = "Taxes are low.", ["supportingFieldIds"] = new JsonArray() }),
                }),
            };

        try
        {
            await GenerateAsync(factory, agent, listing, "headline");

            Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
                """
                select count(*) from public.audit_events
                where listing_id = $1 and action = 'content_generated'
                  and (after_value ->> 'ungroundedSentencesRemoved')::int = 1
                """,
                listing));
        }
        finally
        {
            factory.Models.ContentAnswer = FakeChatClientFactory.DefaultContentAnswer;
        }
    }

    private async Task<Guid> EnterAsync(Guid agent, Guid listing, string key, int value)
    {
        using var client = factory.CreateClientFor(agent);
        using var put = Conditional(HttpMethod.Put, Fields(listing), new JsonObject
        {
            ["resoFieldKey"] = key,
            ["value"] = value,
            ["provenance"] = "agent_entered",
        }, ifMatch: 0);

        var response = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }
}
