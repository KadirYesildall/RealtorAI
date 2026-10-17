using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T116 — <c>POST /api/listings/{id}/compliance</c> (FR-017–FR-021, FR-025; Story 4).
///
/// <para>
/// <b>What is at stake.</b> Every draft is checked before it can be approved, and each finding
/// says what is wrong, where, which layer saw it, and what to write instead. The deterministic
/// layers run whether or not a model is enabled. Without the judge the check says plainly that
/// the contextual review did not run, rather than passing silently or blocking a listing an agent
/// is entitled to finish by hand (SC-010).
/// </para>
/// </summary>
public sealed class ComplianceRunTests : IClassFixture<ApiFactory>, IClassFixture<AiEnabledApiFactory>
{
    private readonly ApiFactory plain;
    private readonly AiEnabledApiFactory ai;

    public ComplianceRunTests(ApiFactory plain, AiEnabledApiFactory ai)
    {
        this.plain = plain;
        this.ai = ai;
        EnableJudge(ai);
    }

    [Fact]
    public async Task Run_Anonymous_Is401()
    {
        using var client = plain.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Compliance(Guid.NewGuid()), new JsonObject())).StatusCode);
    }

    /// <summary>Accepted as a pipeline run of the compliance step, so progress reads like any run.</summary>
    [Fact]
    public async Task Run_IsAcceptedAsARunOfTheComplianceStep()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-accepted", "A bright home.");

        var response = await RunAsync(plain, agent, listing);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["compliance"], status["steps"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal("done", PipelineTesting.StepState(await PipelineTesting.WaitForCompletionAsync(plain, agent, listing), "compliance"));
    }

    /// <summary>The Independent Test of Story 4: each violation found, with span, layer and rewrite.</summary>
    [Fact]
    public async Task Run_FindsEachViolation_WithSpanLayerAndRewrite()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(plain, "check-violations", ViolatingRemarks);

        var findings = await CheckAsync(plain, agent, listing);

        var families = Rule(findings, "fair_housing.familial_status.perfect_for_families");
        Assert.Equal("fair_housing", families["checkType"]!.GetValue<string>());
        Assert.Equal("lexicon", families["source"]!.GetValue<string>());
        Assert.Equal("blocking", families["severity"]!.GetValue<string>());
        Assert.Equal("content_draft", families["targetType"]!.GetValue<string>());
        Assert.Equal(draft, families["targetId"]!.GetValue<Guid>());
        Assert.Equal(0, families["spanStart"]!.GetValue<int>());
        Assert.Equal("Perfect for young families".Length, families["spanEnd"]!.GetValue<int>());
        Assert.Equal("Perfect for young families", families["phrase"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(families["suggestedFix"]!.GetValue<string>()));
        Assert.False(families["canAcknowledge"]!.GetValue<bool>());

        var church = Rule(findings, "fair_housing.religion.distance_to_worship");
        Assert.Equal("warning", church["severity"]!.GetValue<string>());
        Assert.True(church["canAcknowledge"]!.GetValue<bool>());

        var phone = Rule(findings, "mls.forbidden.phone_number");
        Assert.Equal("mls_rule", phone["checkType"]!.GetValue<string>());
        Assert.Equal("deterministic", phone["source"]!.GetValue<string>());
        Assert.Equal("(949) 555-0123", phone["phrase"]!.GetValue<string>());
    }

    /// <summary>Research R2: against CRMLS, unverified, the listing carries a blocking finding saying so.</summary>
    [Fact]
    public async Task Run_AgainstTheUnverifiedCrmlsProfile_FailsClosed()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-crmls", "A bright home.", "CRMLS");

        var finding = Rule(await CheckAsync(plain, agent, listing), "mls.profile_incomplete");

        Assert.Equal("blocking", finding["severity"]!.GetValue<string>());
        Assert.Equal("listing", finding["targetType"]!.GetValue<string>());
        Assert.Equal(listing, finding["targetId"]!.GetValue<Guid>());
    }

    /// <summary>
    /// FR-017 with no judge model: the lexicon still runs, and the gap is a warning the agent can
    /// acknowledge, not a silent pass and not a dead end.
    /// </summary>
    [Fact]
    public async Task Run_WithNoJudgeModel_SaysTheContextualReviewDidNotRun()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-no-judge", "A bright home.");

        var finding = Rule(await CheckAsync(plain, agent, listing), "fair_housing.judge_unavailable");

        Assert.Equal("warning", finding["severity"]!.GetValue<string>());
        Assert.Equal("deterministic", finding["source"]!.GetValue<string>());
        Assert.True(finding["canAcknowledge"]!.GetValue<bool>());
    }

    /// <summary>FR-017: the judge's findings are recorded as the judge's, with the span located in the text.</summary>
    [Fact]
    public async Task Run_WithTheJudge_RecordsItsFindingsAsTheJudges()
    {
        const string remarks = "A calm retreat, ideal for empty nesters.";
        var (agent, listing, _) = await ListingWithRemarksAsync(ai, "check-judge", remarks);
        ai.Models.JudgeAnswer = request => (string?)request["text"] == remarks
            ? Judged(("ideal for empty nesters", "familial_status", "warning", "suited to anyone who wants a quieter home"))
            : FakeChatClientFactory.DefaultJudgeAnswer(request);

        try
        {
            var findings = await CheckAsync(ai, agent, listing);
            var judged = Open(findings).Single(finding => finding["source"]!.GetValue<string>() == "judge");

            Assert.Equal("fair_housing", judged["checkType"]!.GetValue<string>());
            Assert.Equal(remarks.IndexOf("ideal for empty nesters", StringComparison.Ordinal), judged["spanStart"]!.GetValue<int>());
            Assert.Equal("suited to anyone who wants a quieter home", judged["suggestedFix"]!.GetValue<string>());
            Assert.DoesNotContain(Open(findings), finding => finding["ruleId"]!.GetValue<string>() == "fair_housing.judge_unavailable");
        }
        finally
        {
            ai.Models.JudgeAnswer = FakeChatClientFactory.DefaultJudgeAnswer;
        }
    }

    /// <summary>FR-017 merge: what both layers see is one finding, the lexicon's.</summary>
    [Fact]
    public async Task Run_WhenTheJudgeAgreesWithTheLexicon_RecordsOneFinding()
    {
        const string remarks = "Perfect for young families.";
        var (agent, listing, _) = await ListingWithRemarksAsync(ai, "check-merge", remarks);
        ai.Models.JudgeAnswer = request => (string?)request["text"] == remarks
            ? Judged(("young families", "familial_status", "blocking", "anyone"))
            : FakeChatClientFactory.DefaultJudgeAnswer(request);

        try
        {
            var fairHousing = Open(await CheckAsync(ai, agent, listing))
                .Where(finding => finding["checkType"]!.GetValue<string>() == "fair_housing")
                .ToList();

            Assert.Equal("lexicon", Assert.Single(fairHousing)["source"]!.GetValue<string>());
        }
        finally
        {
            ai.Models.JudgeAnswer = FakeChatClientFactory.DefaultJudgeAnswer;
        }
    }

    /// <summary>A phrase the judge names that is not in the text is not a finding anyone can locate.</summary>
    [Fact]
    public async Task Run_DropsAJudgeFindingWhosePhraseIsNotInTheText()
    {
        const string remarks = "A bright home with a garden.";
        var (agent, listing, _) = await ListingWithRemarksAsync(ai, "check-hallucinated", remarks);
        ai.Models.JudgeAnswer = request => (string?)request["text"] == remarks
            ? Judged(("adults only", "familial_status", "blocking", "anyone"))
            : FakeChatClientFactory.DefaultJudgeAnswer(request);

        try
        {
            Assert.DoesNotContain(Open(await CheckAsync(ai, agent, listing)), finding => finding["source"]!.GetValue<string>() == "judge");
        }
        finally
        {
            ai.Models.JudgeAnswer = FakeChatClientFactory.DefaultJudgeAnswer;
        }
    }

    /// <summary>A run checks the photos too: below the profile's minimum resolution is blocking.</summary>
    [Fact]
    public async Task Run_ChecksPhotoResolution()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-photo", "A bright home.");
        var photo = (await PipelineTesting.UploadAsync(plain, agent, listing, TestImages.Jpeg(320, 240)))[0];

        var finding = Rule(await CheckAsync(plain, agent, listing), "mls.photo_below_min_resolution");

        Assert.Equal("photo", finding["targetType"]!.GetValue<string>());
        Assert.Equal(photo, finding["targetId"]!.GetValue<Guid>());
    }

    /// <summary>FR-021: an agent with no brokerage on record has no brokerage to disclose.</summary>
    [Fact]
    public async Task Run_WithNoBrokerageOnRecord_RaisesABlockingDisclosureFinding()
    {
        var agent = await TestAgents.CreateAsync("check-disclosure");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(plain, agent));

        var finding = Rule(await CheckAsync(plain, agent, listing), "disclosure.brokerage_missing");

        Assert.Equal("advertising_disclosure", finding["checkType"]!.GetValue<string>());
        Assert.Equal("blocking", finding["severity"]!.GetValue<string>());
    }

    /// <summary>Scoped to one item: only that item's findings change.</summary>
    [Fact]
    public async Task Run_ForOneDraft_ChecksOnlyThatDraft()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(plain, "check-scoped", ViolatingRemarks);

        var findings = await CheckAsync(plain, agent, listing, new JsonObject { ["targetType"] = "content_draft", ["targetId"] = draft });

        Assert.NotEmpty(Open(findings));
        Assert.All(Open(findings), finding => Assert.Equal(draft, finding["targetId"]!.GetValue<Guid>()));
    }

    /// <summary>FR-025: an edit re-checks the edited item at once — no run to start.</summary>
    [Fact]
    public async Task EditingADraft_ReChecksIt()
    {
        var (agent, listing, draft) = await ListingWithRemarksAsync(plain, "check-edit", ViolatingRemarks);
        await CheckAsync(plain, agent, listing);
        var rowVersion = OfKind(await DraftsAsync(plain, agent, listing), "public_remarks").Single()["rowVersion"]!.GetValue<int>();

        using (var client = plain.CreateClientFor(agent))
        {
            using var patch = TestListings.Conditional(HttpMethod.Patch, Draft(listing, draft), new JsonObject { ["body"] = "A bright home near the park." }, rowVersion);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(patch)).StatusCode);
        }

        var open = Open(await FindingsAsync(plain, agent, listing)).Where(finding => finding["targetId"]!.GetValue<Guid>() == draft);
        Assert.Empty(open);
        Assert.Equal(3L, await TestDatabase.ScalarAsync<long>(
            "select count(*) from public.compliance_findings where target_id = $1 and status = 'resolved'",
            draft));
    }

    /// <summary>A second run over the same words keeps the same findings — no duplicates.</summary>
    [Fact]
    public async Task Run_Twice_DoesNotDuplicateFindings()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-twice", ViolatingRemarks);

        var first = Open(await CheckAsync(plain, agent, listing)).Select(IdOf).Order().ToList();
        var second = Open(await CheckAsync(plain, agent, listing)).Select(IdOf).Order().ToList();

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("""{ "targetType": "brochure", "targetId": "00000000-0000-4000-8000-000000000001" }""")]
    [InlineData("""{ "targetType": "content_draft" }""")]
    [InlineData("""{ "targetType": "photo", "targetId": "00000000-0000-4000-8000-000000000001" }""")]
    public async Task Run_WithAnInvalidTarget_Is422(string body)
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(plain, "check-invalid", "A bright home.");
        using var client = plain.CreateClientFor(agent);

        var response = await client.PostAsync(Compliance(listing), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Run_ByTheApprovedBrokerAdmin_Is403_AndByAStranger_404()
    {
        var admin = await TestAgents.CreateAsync("check-admin");
        var brokerage = await TestBrokerages.CreateAsync(admin, targetMlsId: await CompleteMlsAsync());
        var member = await TestAgents.CreateAsync("check-member");
        await TestBrokerages.AddMemberAsync(member, brokerage, "approved");
        var listing = TestListings.IdOf(await TestListings.CreateAsync(plain, member));
        var stranger = await TestAgents.CreateAsync("check-stranger");

        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(plain, admin, listing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RunAsync(plain, stranger, listing)).StatusCode);
    }

    internal static JsonObject Judged(params (string Phrase, string ProtectedClass, string Severity, string Rewrite)[] findings) => new()
    {
        ["findings"] = new JsonArray(findings
            .Select(finding => (JsonNode)new JsonObject
            {
                ["phrase"] = finding.Phrase,
                ["protectedClass"] = finding.ProtectedClass,
                ["severity"] = finding.Severity,
                ["reason"] = "The phrase suggests a preference about who should live here.",
                ["rewrite"] = finding.Rewrite,
            })
            .ToArray()),
    };
}
