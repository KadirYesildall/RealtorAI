using System.Security.Claims;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Hangfire;
using Hangfire.States;
using Libs.Ai;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Listings;
using Npgsql;
using Pipeline;
using Pipeline.Steps;

namespace Api.Endpoints;

internal sealed record StartPipelineRequest(string? Tone, int? VariantCount);

internal sealed record PipelineStepDto(string Name, string State, string? FailureReason);

internal sealed record PhotoProgressDto(Guid PhotoId, string State, string? FailureReason);

/// <summary>The contract's <c>PipelineStatus</c>.</summary>
/// <param name="ManualCompletionAvailable">Always true: SC-010 guarantees a hand-completion path.</param>
internal sealed record PipelineStatusDto(
    Guid ListingId,
    string ListingStatus,
    IReadOnlyList<PipelineStepDto> Steps,
    IReadOnlyList<PhotoProgressDto> PhotoProgress,
    bool ManualCompletionAvailable);

/// <summary>
/// T095 — <c>POST</c> and <c>GET /api/listings/{id}/pipeline</c> (FR-053, FR-054, FR-055,
/// FR-061, SC-010).
///
/// <para>
/// <b>Start.</b> Owner only — it spends the listing's AI budget. Refused before anything is
/// queued when no model is enabled for photo analysis (503) or the budget cannot cover the photos
/// still to analyse (409), each saying the listing can be finished by hand. Starting while a run
/// is unfinished returns that run: there is at most one per listing (a partial unique index).
/// A pending membership does not hold the agent back (FR-061).
/// </para>
/// <para>
/// <b>Status.</b> The owner, or an approved broker admin of an approved member (FR-050) — the
/// poll is the admin's view of progress, since the SignalR channel is owner-only. It reads the
/// persisted state of the latest run, which is the record; notifications only prompt a re-read.
/// </para>
/// </summary>
internal static class PipelineEndpoints
{
    internal static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings/{listingId:guid}/pipeline")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Pipeline");

        group.MapPost("/", StartAsync).WithName("StartPipeline");
        group.MapGet("/", GetAsync).WithName("GetPipelineStatus");

        return app;
    }

    private static async Task<Results<JsonHttpResult<PipelineStatusDto>, ProblemHttpResult, UnauthorizedHttpResult>> StartAsync(
        Guid listingId,
        [FromBody] StartPipelineRequest? request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IModelRegistry registry,
        ICostTracker costs,
        PipelineRunner runner,
        PhotoAnalysisOptions analysis,
        PipelineOptions pipeline,
        IBackgroundJobClient jobs,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(
                authorization,
                "Only the listing's agent can start analysis; it spends the listing's AI budget.");
        }

        Tone? tone = null;

        if (request?.Tone is { } toneText)
        {
            tone = toneText switch
            {
                "professional" => Tone.Professional,
                "warm" => Tone.Warm,
                "luxury" => Tone.Luxury,
                "concise" => Tone.Concise,
                _ => null,
            };

            if (tone is null)
            {
                return ApiProblems.Validation("tone", "Use professional, warm, luxury or concise.");
            }
        }

        if (request?.VariantCount is { } variants && variants is < 2 or > 3)
        {
            return ApiProblems.Validation("variantCount", "Ask for 2 or 3 variants.");
        }

        var photoIds = await context.Photos.AsNoTracking()
            .Where(photo => photo.ListingId == listingId)
            .Select(photo => photo.Id)
            .ToListAsync(cancellationToken);

        if (photoIds.Count == 0)
        {
            return ApiProblems.Validation("photos", "Add at least one photo before starting analysis (FR-003).");
        }

        if (await ActiveRunAsync(context, listingId, cancellationToken) is not null)
        {
            return Accepted(await LoadStatusAsync(context, listingId, cancellationToken));
        }

        try
        {
            await registry.ResolveAsync(AiTaskKey.PhotoAnalysis, cancellationToken);
        }
        catch (ModelUnavailableException)
        {
            return ManualPath(
                StatusCodes.Status503ServiceUnavailable,
                "Photo analysis isn't available right now",
                "No model is enabled for photo analysis. Every result it would produce — room types, "
                + "features, captions, order and cover — can be entered by hand, and the listing can be "
                + "finished without it (SC-010).");
        }

        var toAnalyse = photoIds.Count - await context.PhotoAnalyses.AsNoTracking()
            .CountAsync(row => photoIds.Contains(row.PhotoId) && row.ModelId != null && !row.Failed, cancellationToken);

        if (toAnalyse > 0)
        {
            try
            {
                await costs.EnsureWithinBudgetAsync(listingId, toAnalyse * analysis.EstimatedTokensPerPhoto, toAnalyse, cancellationToken);
            }
            catch (AiBudgetExceededException exception)
            {
                return ManualPath(
                    StatusCodes.Status409Conflict,
                    "This listing's AI budget is used up",
                    exception.Message + " Finish the remaining photos by hand (FR-055).");
            }
        }

        var runId = Guid.CreateVersion7();

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            context.PipelineRuns.Add(new PipelineRun
            {
                Id = runId,
                ListingId = listingId,
                StartedBy = agentId,
                Tone = tone,
                VariantCount = request?.VariantCount,
            });

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { ConstraintName: "pipeline_runs_one_active_per_listing" })
            {
                // Started at the same moment from elsewhere: that run is the answer.
                await transaction.RollbackAsync(cancellationToken);
                return Accepted(await LoadStatusAsync(context, listingId, cancellationToken));
            }

            context.PipelineStepStates.AddRange(runner.Steps.Select((step, position) => new PipelineStepState
            {
                RunId = runId,
                Step = PipelineSpellings.Of(step),
                Position = position,
            }));
            context.PipelinePhotoStates.AddRange(photoIds.Select(photoId => new PipelinePhotoState { RunId = runId, PhotoId = photoId }));
            await context.SaveChangesAsync(cancellationToken);

            // draft → analyzing. A listing already past draft keeps its status: re-analysing
            // a reviewed listing does not take it back out of review.
            await context.Listings
                .Where(listing => listing.Id == listingId && listing.Status == ListingStatus.Draft)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(listing => listing.Status, ListingLifecycle.Transition(ListingStatus.Draft, ListingStatus.Analyzing)),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // After the commit, so the job never starts before the run it reads exists.
        jobs.Create<PipelineRunner>(job => job.RunAsync(runId, CancellationToken.None), new EnqueuedState(pipeline.Queue));

        return Accepted(await LoadStatusAsync(context, listingId, cancellationToken));
    }

    private static async Task<Results<Ok<PipelineStatusDto>, ProblemHttpResult, UnauthorizedHttpResult>> GetAsync(
        Guid listingId,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanRead)
        {
            return ListingRepresentation.Denied(authorization, "You cannot read this listing.");
        }

        return TypedResults.Ok(await LoadStatusAsync(context, listingId, cancellationToken));
    }

    private static JsonHttpResult<PipelineStatusDto> Accepted(PipelineStatusDto status) =>
        TypedResults.Json(status, statusCode: StatusCodes.Status202Accepted);

    /// <summary>A refusal that names the way forward: the listing can always be finished by hand.</summary>
    private static ProblemHttpResult ManualPath(int status, string title, string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = "https://realtorai.app/problems/manual-completion",
            Title = title,
            Status = status,
            Detail = detail,
            Extensions = { ["manualCompletionAvailable"] = true },
        });

    private static Task<PipelineRun?> ActiveRunAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken) =>
        context.PipelineRuns.AsNoTracking().SingleOrDefaultAsync(run => run.ListingId == listingId && run.FinishedAt == null, cancellationToken);

    /// <summary>The latest run's state. With no run yet: no steps, and the manual path open.</summary>
    internal static async Task<PipelineStatusDto> LoadStatusAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken)
    {
        var status = await context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.Status)
            .SingleAsync(cancellationToken);

        var run = await context.PipelineRuns.AsNoTracking()
            .Where(candidate => candidate.ListingId == listingId)
            .OrderByDescending(candidate => candidate.StartedAt)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
        {
            return new PipelineStatusDto(listingId, ListingRepresentation.FormatStatus(status), [], [], ManualCompletionAvailable: true);
        }

        var steps = await context.PipelineStepStates.AsNoTracking()
            .Where(step => step.RunId == run.Id)
            .OrderBy(step => step.Position)
            .Select(step => new PipelineStepDto(step.Step, step.State, step.FailureReason))
            .ToListAsync(cancellationToken);

        var photos = await context.PipelinePhotoStates.AsNoTracking()
            .Where(photo => photo.RunId == run.Id)
            .Join(context.Photos.AsNoTracking(), state => state.PhotoId, photo => photo.Id, (state, photo) => new { state, photo.OrderIndex })
            .OrderBy(row => row.OrderIndex)
            .Select(row => new PhotoProgressDto(row.state.PhotoId, row.state.State, row.state.FailureReason))
            .ToListAsync(cancellationToken);

        return new PipelineStatusDto(listingId, ListingRepresentation.FormatStatus(status), steps, photos, ManualCompletionAvailable: true);
    }
}
