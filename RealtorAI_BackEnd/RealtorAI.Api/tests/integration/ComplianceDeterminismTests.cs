using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T124 — determinism: identical input yields identical findings (FR-024; Story 4 scenario 10).
///
/// <para>
/// Two things make it so, and both are checked. The deterministic layers are pure functions of
/// the text and the profile. The judge is dispatched at the temperature its registry entry sets
/// (0), and the parameter must actually reach the model — a temperature recorded in
/// configuration and dropped on the way to the provider is how "deterministic" quietly stops
/// being true.
/// </para>
/// </summary>
public sealed class ComplianceDeterminismTests : IClassFixture<AiEnabledApiFactory>
{
    private readonly AiEnabledApiFactory factory;

    public ComplianceDeterminismTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableJudge(factory);
    }

    [Fact]
    public async Task TwoListingsWithTheSameWords_GetTheSameFindings()
    {
        const string remarks = ViolatingRemarks + " The best schools in the district, and it will appreciate.";
        factory.Models.JudgeAnswer = request => (string?)request["text"] == remarks
            ? ComplianceRunTestsShape.Judged("it will appreciate")
            : FakeChatClientFactory.DefaultJudgeAnswer(request);

        try
        {
            var mls = await CompleteMlsAsync();
            var (firstAgent, first, _) = await ListingWithRemarksAsync(factory, "determinism-a", remarks, mls);
            var (secondAgent, second, _) = await ListingWithRemarksAsync(factory, "determinism-b", remarks, mls);

            var a = Signature(await CheckAsync(factory, firstAgent, first));
            var b = Signature(await CheckAsync(factory, secondAgent, second));

            Assert.NotEmpty(a);
            Assert.Equal(a, b);
        }
        finally
        {
            factory.Models.JudgeAnswer = FakeChatClientFactory.DefaultJudgeAnswer;
        }
    }

    /// <summary>The registry's temperature reaches the provider call.</summary>
    [Fact]
    public async Task TheJudge_IsDispatchedAtTemperatureZero()
    {
        const string remarks = "A calm home on a quiet lane, unique wording for the temperature check.";
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "determinism-temperature", remarks);

        await CheckAsync(factory, agent, listing);

        var judged = factory.Models.Temperatures.Where(call => call.Input.Contains(remarks, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(judged);
        Assert.All(judged, call => Assert.Equal(0f, call.Temperature));
    }

    /// <summary>What a finding is, independent of which row or listing holds it.</summary>
    private static List<string> Signature(JsonArray findings) =>
        Open(findings)
            .Where(finding => finding["targetType"]!.GetValue<string>() == "content_draft")
            .Select(finding => string.Join('|',
                finding["ruleId"]!.GetValue<string>(),
                finding["source"]!.GetValue<string>(),
                finding["severity"]!.GetValue<string>(),
                finding["spanStart"]?.GetValue<int>(),
                finding["spanEnd"]?.GetValue<int>(),
                finding["phrase"]?.GetValue<string>(),
                finding["suggestedFix"]?.GetValue<string>()))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static class ComplianceRunTestsShape
    {
        internal static JsonObject Judged(string phrase) => new()
        {
            ["findings"] = new JsonArray(new JsonObject
            {
                ["phrase"] = phrase,
                ["protectedClass"] = "none",
                ["severity"] = "warning",
                ["reason"] = "A prediction about value is not something the listing can promise.",
                ["rewrite"] = "it has been well kept",
            }),
        };
    }
}
