using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;
using static RealtorAI.Tests.Contract.Infrastructure.TestListings;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T103 — <c>POST /api/listings/{id}/content/regenerate</c> (FR-013, FR-015, FR-054, FR-055;
/// research R2).
///
/// <para>
/// <b>What is at stake.</b> Content is generated to the MLS's own character limit, and the limit
/// is a fact about the MLS that someone has to have verified. With no verified limit there is
/// nothing to generate to, and the answer is 409 — never a guessed limit, which is how a listing
/// gets rejected by the MLS after the agent approved it. CRMLS's profile is unverified until
/// T160, so content against CRMLS fails closed today, by design.
/// </para>
/// </summary>
public sealed class ContentRegenerateTests : IClassFixture<ApiFactory>, IClassFixture<AiEnabledApiFactory>
{
    private readonly ApiFactory plain;
    private readonly AiEnabledApiFactory ai;

    public ContentRegenerateTests(ApiFactory plain, AiEnabledApiFactory ai)
    {
        this.plain = plain;
        this.ai = ai;
        EnableContentModel(ai);
    }

    [Fact]
    public async Task Regenerate_Anonymous_Is401()
    {
        using var client = ai.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(Regenerate(Guid.NewGuid()), new JsonObject { ["kind"] = "headline" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Accepted as a pipeline run of the one step, so progress reads like any other run.</summary>
    [Fact]
    public async Task Regenerate_IsAcceptedAsARunOfTheContentStepAlone()
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-accepted", await VerifiedMlsAsync());

        var response = await RegenerateAsync(ai, agent, listing, "public_remarks", "concise");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["content_generation"], status["steps"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));

        var finished = await PipelineTesting.WaitForCompletionAsync(ai, agent, listing);
        Assert.Equal("done", PipelineTesting.StepState(finished, "content_generation"));
        Assert.All(OfKind(await DraftsAsync(ai, agent, listing), "public_remarks"), draft => Assert.Equal("concise", draft["tone"]!.GetValue<string>()));
    }

    /// <summary>With no tone asked for, the brokerage's default tone is used.</summary>
    [Fact]
    public async Task Regenerate_WithoutATone_UsesTheBrokerageDefault()
    {
        var mls = await VerifiedMlsAsync();
        var agent = await TestAgents.CreateAsync("regen-default-tone");
        var brokerage = await TestBrokerages.CreateAsync(agent, targetMlsId: mls);
        await TestDatabase.ExecuteAsync("update public.brokerage_settings set default_tone = 'luxury' where brokerage_id = $1", brokerage);
        var listing = IdOf(await CreateAsync(ai, agent));

        var drafts = OfKind(await GenerateAsync(ai, agent, listing, "headline"), "headline");

        Assert.Equal("luxury", Assert.Single(drafts)["tone"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{ "kind": "brochure" }""")]
    [InlineData("""{ "kind": "headline", "tone": "shouty" }""")]
    [InlineData("""{ }""")]
    public async Task Regenerate_WithAnInvalidRequest_Is422(string body)
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-invalid", await VerifiedMlsAsync());
        using var client = ai.CreateClientFor(agent);

        var response = await client.PostAsync(Regenerate(listing), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>Research R2: the seeded CRMLS profile has no verified limits, so generation fails closed.</summary>
    [Fact]
    public async Task Regenerate_AgainstTheUnverifiedCrmlsProfile_Is409AndQueuesNothing()
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-crmls", "CRMLS");

        var response = await RegenerateAsync(ai, agent, listing, "public_remarks");

        await AssertFailsClosedAsync(response, listing, "mls_rule_profile_incomplete");
    }

    /// <summary>An agent with no brokerage, or a brokerage with no target MLS, has no limit either.</summary>
    [Fact]
    public async Task Regenerate_WithNoTargetMls_Is409()
    {
        var agent = await TestAgents.CreateAsync("regen-no-mls");
        var listing = IdOf(await CreateAsync(ai, agent));

        var response = await RegenerateAsync(ai, agent, listing, "headline");

        await AssertFailsClosedAsync(response, listing, "mls_rule_profile_incomplete");
    }

    /// <summary>A verified profile still needs the limit for the kind asked for.</summary>
    [Fact]
    public async Task Regenerate_WhenTheKindsLimitIsUnset_Is409()
    {
        var mls = "TEST-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await TestDatabase.ExecuteAsync(
            """
            insert into public.mls_rule_profiles
                (id, display_name, public_remarks_max_chars, private_remarks_max_chars, rulebook_version, verified_at)
            values ($1, 'Partial test MLS', 600, null, 'contract-test', timezone('utc', now()))
            """,
            mls);
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-partial", mls);

        await AssertFailsClosedAsync(await RegenerateAsync(ai, agent, listing, "private_remarks"), listing, "mls_rule_profile_incomplete");
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(ai, agent, listing, "public_remarks")).StatusCode);
        await PipelineTesting.WaitForCompletionAsync(ai, agent, listing);
    }

    /// <summary>FR-054: no enabled writer model is 503 with the manual path named — never a dead end.</summary>
    [Fact]
    public async Task Regenerate_WithNoEnabledModel_Is503AndSaysTheContentCanBeWrittenByHand()
    {
        var (agent, listing) = await ListingOnMlsAsync(plain, "regen-no-model", await VerifiedMlsAsync());

        var response = await RegenerateAsync(plain, agent, listing, "public_remarks");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonObject>())!["manualCompletionAvailable"]!.GetValue<bool>());
        Assert.Equal(0L, await RunCountAsync(listing));
    }

    /// <summary>FR-055: the budget is checked before dispatch.</summary>
    [Fact]
    public async Task Regenerate_WithTheBudgetExhausted_Is409()
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-budget", await VerifiedMlsAsync());
        await TestDatabase.ExecuteAsync(
            """
            insert into public.ai_usage (listing_id, task_key, model_id, tokens_in, tokens_out, image_count)
            values ($1, 'listing.content', 'test/writer-model', 250000, 0, 0)
            """,
            listing);

        await AssertFailsClosedAsync(await RegenerateAsync(ai, agent, listing, "headline"), listing, "budget_exceeded");
    }

    /// <summary>
    /// VIII.5: every call is audited with the prompt version and hash, and the drafts themselves
    /// record which model and prompt wrote them.
    /// </summary>
    [Fact]
    public async Task Regenerate_RecordsTheModelAndPromptOfEveryCallAndDraft()
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-audit", await VerifiedMlsAsync());

        await GenerateAsync(ai, agent, listing, "public_remarks");

        Assert.True(await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.audit_events
            where listing_id = $1 and action = 'ai_call' and model_id = 'test/writer-model'
              and prompt_version = '1' and prompt_hash ~ '^[0-9a-f]{64}$'
            """,
            listing) >= 1);
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.audit_events where listing_id = $1 and action = 'content_generated'",
            listing));
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            """
            select count(*) from public.content_drafts
            where listing_id = $1
              and (model_id is distinct from 'test/writer-model' or prompt_version is distinct from '1' or prompt_hash is null)
            """,
            listing));
    }

    /// <summary>FR-015: output that fails validation twice is an explicit failure, and leaves no drafts behind.</summary>
    [Fact]
    public async Task Regenerate_WhenTheModelKeepsAnsweringOffSchema_FailsTheStepExplicitly()
    {
        var (agent, listing) = await ListingOnMlsAsync(ai, "regen-off-schema", await VerifiedMlsAsync());
        var previous = ai.Models.ContentAnswer;
        ai.Models.ContentAnswer = _ => new JsonObject { ["variants"] = "not a list" };

        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(ai, agent, listing, "headline")).StatusCode);

            var finished = await PipelineTesting.WaitForCompletionAsync(ai, agent, listing);

            Assert.Equal("failed", PipelineTesting.StepState(finished, "content_generation"));
            Assert.Empty(await DraftsAsync(ai, agent, listing));
        }
        finally
        {
            ai.Models.ContentAnswer = previous;
        }
    }

    [Fact]
    public async Task Regenerate_ByTheApprovedBrokerAdmin_Is403_AndByAStranger_404()
    {
        var mls = await VerifiedMlsAsync();
        var admin = await TestAgents.CreateAsync("regen-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: mls);
        var member = await TestAgents.CreateAsync("regen-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = IdOf(await CreateAsync(ai, member));
        var stranger = await TestAgents.CreateAsync("regen-stranger");

        Assert.Equal(HttpStatusCode.Forbidden, (await RegenerateAsync(ai, admin, listing, "headline")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RegenerateAsync(ai, stranger, listing, "headline")).StatusCode);
    }

    private static async Task AssertFailsClosedAsync(HttpResponseMessage response, Guid listing, string reason)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(reason, problem["reason"]!.GetValue<string>());
        Assert.True(problem["manualCompletionAvailable"]!.GetValue<bool>());
        Assert.Equal(0L, await RunCountAsync(listing));
    }

    private static async Task<long> RunCountAsync(Guid listing) =>
        (await TestDatabase.ScalarAsync<long>("select count(*) from public.pipeline_runs where listing_id = $1", listing))!;
}
