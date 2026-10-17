using System.Collections.Concurrent;
using System.Diagnostics;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ContentTesting;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T162 — every pipeline run and every step is a trace span, so a slow or failed run can be read
/// step by step in the tracing backend (SC-005 is measured from these).
///
/// <para>
/// Listened to in-process: the Hangfire worker runs in the test host, so the spans it starts are
/// visible to an <see cref="ActivityListener"/> here, exactly as the OTLP exporter sees them.
/// </para>
/// </summary>
public sealed class PipelineTelemetryTests : IClassFixture<AiEnabledApiFactory>, IDisposable
{
    private readonly AiEnabledApiFactory factory;
    private readonly ConcurrentBag<Activity> spans = [];
    private readonly ActivityListener listener;

    public PipelineTelemetryTests(AiEnabledApiFactory factory)
    {
        this.factory = factory;
        EnableContentModel(factory);

        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RealtorAI.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Dispose() => listener.Dispose();

    [Fact]
    public async Task ARun_IsASpan_WithOneChildSpanPerStep_CarryingItsOutcome()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "telemetry", await VerifiedMlsAsync());

        await GenerateAsync(factory, agent, listing, "headline");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        Activity? run = null;

        while (run is null && DateTime.UtcNow < deadline)
        {
            run = spans.FirstOrDefault(span => span.OperationName == "pipeline.run" && (string?)span.GetTagItem("realtorai.listing_id") == listing.ToString());
            await Task.Delay(100);
        }

        Assert.NotNull(run);

        var steps = spans.Where(span => span.OperationName == "pipeline.step" && span.ParentSpanId == run.SpanId).ToList();

        Assert.Equal(
            ["compliance", "content_generation"],
            steps.Select(span => (string)span.GetTagItem("realtorai.step")!).Order(StringComparer.Ordinal));
        Assert.All(steps, span => Assert.Equal("done", span.GetTagItem("realtorai.step.outcome")));
        Assert.All(steps, span => Assert.Equal(run.TraceId, span.TraceId));
        Assert.Equal(ActivityStatusCode.Ok, run.Status);
    }

    /// <summary>A step that fails is visible as a failed span with its reason, without failing the run's trace.</summary>
    [Fact]
    public async Task AFailedStep_IsAnErrorSpan_WithItsReason()
    {
        var (agent, listing) = await ListingOnMlsAsync(factory, "telemetry-failed", await VerifiedMlsAsync());
        var previous = factory.Models.ContentAnswer;
        factory.Models.ContentAnswer = _ => new System.Text.Json.Nodes.JsonObject { ["variants"] = "not a list" };

        try
        {
            await RegenerateAsync(factory, agent, listing, "headline");
            await PipelineTesting.WaitForCompletionAsync(factory, agent, listing);
        }
        finally
        {
            factory.Models.ContentAnswer = previous;
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        Activity? failed = null;

        while (failed is null && DateTime.UtcNow < deadline)
        {
            failed = spans.FirstOrDefault(span => span.OperationName == "pipeline.step"
                && (string?)span.GetTagItem("realtorai.listing_id") == listing.ToString()
                && (string?)span.GetTagItem("realtorai.step") == "content_generation");
            await Task.Delay(100);
        }

        Assert.NotNull(failed);
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal("failed", failed.GetTagItem("realtorai.step.outcome"));
        Assert.False(string.IsNullOrWhiteSpace(failed.StatusDescription));
    }
}
