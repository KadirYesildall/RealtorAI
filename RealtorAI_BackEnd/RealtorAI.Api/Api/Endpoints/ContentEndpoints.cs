using System.Security.Claims;
using System.Text.Json;
using Api.Authentication;
using Api.Authorization;
using Api.Concurrency;
using Api.Data;
using Api.Data.Entities;
using Hangfire;
using Hangfire.States;
using Libs.Ai;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Content;
using Modules.Listings;
using Npgsql;
using Pipeline;
using Pipeline.Steps;

namespace Api.Endpoints;

internal sealed record RegenerateContentRequest(string? Kind, string? Tone);

internal sealed record EditContentRequest(string? Body, bool? Selected);

internal sealed record CreateContentRequest(string? Kind, string? Body, string? Tone);

/// <summary>
/// T112 — content: list, regenerate, edit and select (FR-013, FR-014, FR-015, FR-025, FR-066;
/// Story 3).
///
/// <para>
/// <b>Here rather than in Modules.Content.</b> tasks.md places these in the module. They live in
/// the host with the other endpoints, because the EF model they read and write is the host's
/// (research R20). What content may say and how it is grounded stays in the module.
/// </para>
/// <para>
/// <b>Regenerate is a pipeline run of the content step, then compliance.</b> It is queued, reported through
/// <c>GET /pipeline</c> like any run, and held to one unfinished run per listing. It is refused
/// before anything is queued: with no writer model (503), with no verified limit for the kind
/// (409, research R2), or with the budget spent (409). Each refusal names the manual path.
/// </para>
/// <para>
/// <b>Written by hand.</b> <c>POST /content</c> stores the agent's own words as a kind's draft,
/// with no model involved, so the manual path SC-010 promises reaches the copy too. It needs no
/// verified profile. The compliance checks (Story 4) hold the listing to the MLS's rules, and a
/// limit that is known is enforced here as well.
/// </para>
/// <para>
/// <b>Editing approved words revokes the approval</b>, in the same transaction as the edit, and
/// returns an approved listing to review (FR-025). An approval is of exact words, and once the
/// words change it no longer describes them.
/// </para>
/// </summary>
internal static class ContentEndpoints
{
    private const string EditedReason = "content_edited";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings/{listingId:guid}/content")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Content");

        group.MapGet("/", ListAsync).WithName("ListContentDrafts");
        group.MapPost("/", CreateAsync).WithName("CreateContentDraft");
        group.MapPost("/regenerate", RegenerateAsync).WithName("RegenerateContent");
        group.MapPatch("/{draftId:guid}", EditAsync).WithName("EditContentDraft");

        return app;
    }

    // ═════════════════════════════ List ═════════════════════════════

    private static async Task<Results<Ok<IReadOnlyList<ContentDraftDto>>, ProblemHttpResult, UnauthorizedHttpResult>> ListAsync(
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

        return TypedResults.Ok(await ContentRepresentation.LoadCurrentAsync(context, listingId, cancellationToken));
    }

    // ═════════════════════════════ Create by hand ═════════════════════════════

    private static async Task<Results<Created<ContentDraftDto>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        Guid listingId,
        CreateContentRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IAuditWriter audit,
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
                "Only the listing's agent can write its content. A broker admin's access is read-only.");
        }

        if (ContentRepresentation.ParseKind(request.Kind) is not { } kind)
        {
            return ApiProblems.Validation("kind", "Use public_remarks, private_remarks or headline.");
        }

        Tone? tone = null;

        if (request.Tone is not null && (tone = ContentRepresentation.ParseTone(request.Tone)) is null)
        {
            return ApiProblems.Validation("tone", "Use professional, warm, luxury or concise.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return ApiProblems.Validation("body", "Write the content before saving it.");
        }

        var (_, profile) = await ContentRepresentation.LoadContextAsync(context, listingId, cancellationToken);

        if (ContentRules.LimitFor(ContentRepresentation.Label(kind), profile) is { } limit && request.Body.Length > limit)
        {
            return ApiProblems.Validation("body", $"The MLS allows at most {limit} characters here; this is {request.Body.Length}.");
        }

        // Two writers at once both read no current draft and pick the same next version; the
        // unique (listing, kind, variant, version) constraint lets one of them in.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var versions = await context.ContentDrafts
            .Where(draft => draft.ListingId == listingId && draft.Kind == kind)
            .Select(draft => new { draft.Version, draft.Status })
            .ToListAsync(cancellationToken);

        if (versions.Any(draft => draft.Status != ContentStatus.Stale))
        {
            await transaction.RollbackAsync(cancellationToken);

            return ApiProblems.Conflict(
                "This listing already has this content",
                "Edit the current draft instead of writing a new one.");
        }

        var created = new ContentDraft
        {
            Id = Guid.CreateVersion7(),
            ListingId = listingId,
            Kind = kind,
            VariantIndex = 0,
            Tone = tone,
            Body = request.Body,
            SupportingFieldIds = ClaimStorage.Serialize([]),
            Version = versions.Count == 0 ? 1 : versions.Max(draft => draft.Version) + 1,
            Status = ContentStatus.Draft,
            Selected = true,
        };

        context.ContentDrafts.Add(created);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.ContentEdited,
                    ActorId = agentId,
                    ListingId = listingId,
                    BrokerageId = await BrokerageOfAsync(context, listingId, cancellationToken),
                    TargetType = ContentRepresentation.DraftTarget,
                    TargetId = created.Id,
                    AfterValue = JsonSerializer.Serialize(Snapshot(created), Web),
                },
                cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Written at the same moment from elsewhere: that draft is the one to edit.
            await transaction.RollbackAsync(cancellationToken);

            return ApiProblems.Conflict(
                "This listing already has this content",
                "Edit the current draft instead of writing a new one.");
        }

        return TypedResults.Created(
            $"/api/listings/{listingId}/content/{created.Id}",
            await ContentRepresentation.LoadOneAsync(context, created, cancellationToken));
    }

    // ═════════════════════════════ Regenerate ═════════════════════════════

    private static async Task<Results<JsonHttpResult<PipelineStatusDto>, ProblemHttpResult, UnauthorizedHttpResult>> RegenerateAsync(
        Guid listingId,
        [FromBody] RegenerateContentRequest? request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IModelRegistry registry,
        ICostTracker costs,
        ContentGenerationOptions generation,
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
                "Only the listing's agent can generate its content; it spends the listing's AI budget.");
        }

        if (ContentRepresentation.ParseKind(request?.Kind) is not { } kind)
        {
            return ApiProblems.Validation("kind", "Use public_remarks, private_remarks or headline.");
        }

        Tone? tone = null;

        if (request!.Tone is not null && (tone = ContentRepresentation.ParseTone(request.Tone)) is null)
        {
            return ApiProblems.Validation("tone", "Use professional, warm, luxury or concise.");
        }

        try
        {
            await registry.ResolveAsync(AiTaskKey.ListingContent, cancellationToken);
        }
        catch (ModelUnavailableException)
        {
            return ManualPath(
                StatusCodes.Status503ServiceUnavailable,
                "no_model",
                "Writing listing content isn't available right now",
                "No model is enabled for writing listing content. The description, private remarks and "
                + "headline can all be written by hand (SC-010).");
        }

        var (_, profile) = await ContentRepresentation.LoadContextAsync(context, listingId, cancellationToken);

        if (ContentRules.LimitFor(ContentRepresentation.Label(kind), profile) is null)
        {
            return ManualPath(
                StatusCodes.Status409Conflict,
                "mls_rule_profile_incomplete",
                "The MLS's limits aren't verified",
                "Content is generated to the MLS's own character limit, and this listing's MLS has no verified "
                + "limit for it yet, so nothing is generated to a guessed one (research R2). Write it by hand.");
        }

        try
        {
            await costs.EnsureWithinBudgetAsync(listingId, generation.EstimatedTokensPerCall, 0, cancellationToken);
        }
        catch (AiBudgetExceededException exception)
        {
            return ManualPath(
                StatusCodes.Status409Conflict,
                "budget_exceeded",
                "This listing's AI budget is used up",
                exception.Message + " Write the content by hand (FR-055).");
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
                ContentKind = kind,
            });

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { ConstraintName: "pipeline_runs_one_active_per_listing" })
            {
                await transaction.RollbackAsync(cancellationToken);

                return ManualPath(
                    StatusCodes.Status409Conflict,
                    "pipeline_running",
                    "This listing's pipeline is already running",
                    "Wait for the current run to finish, then generate again. Its progress is on the listing.");
            }

            // The new words are checked in the same run (FR-025).
            context.PipelineStepStates.AddRange(
                new PipelineStepState { RunId = runId, Step = PipelineSpellings.Of(PipelineStep.ContentGeneration), Position = 0 },
                new PipelineStepState { RunId = runId, Step = PipelineSpellings.Of(PipelineStep.Compliance), Position = 1 });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // After the commit, so the job never starts before the run it reads exists.
        jobs.Create<PipelineRunner>(job => job.RunAsync(runId, CancellationToken.None), new EnqueuedState(pipeline.Queue));

        return TypedResults.Json(
            await PipelineEndpoints.LoadStatusAsync(context, listingId, cancellationToken),
            statusCode: StatusCodes.Status202Accepted);
    }

    // ═════════════════════════════ Edit / select ═════════════════════════════

    private static async Task<Results<Ok<ContentDraftDto>, ProblemHttpResult, UnauthorizedHttpResult>> EditAsync(
        Guid listingId,
        Guid draftId,
        EditContentRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IConcurrencyGuard guard,
        IAuditWriter audit,
        Api.Compliance.ComplianceService compliance,
        HttpResponse response,
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
                "Only the listing's agent can edit its content. A broker admin's access is read-only.");
        }

        if (!IfMatch.TryParse(ifMatch, out var expectedVersion, out var preconditionError))
        {
            return VersionConflict.PreconditionRequired(preconditionError);
        }

        if (request.Body is null && request.Selected is null)
        {
            return ApiProblems.Validation("body", "Send the edited body, a selection, or both.");
        }

        if (request.Body is { } text && string.IsNullOrWhiteSpace(text))
        {
            return ApiProblems.Validation("body", "The content cannot be empty. Edit it, or choose another version.");
        }

        var existing = await context.ContentDrafts.AsNoTracking()
            .SingleOrDefaultAsync(draft => draft.Id == draftId && draft.ListingId == listingId, cancellationToken);

        if (existing is null)
        {
            return ApiProblems.NotFound("No such draft on this listing.");
        }

        if (existing.Status == ContentStatus.Stale)
        {
            return ApiProblems.Conflict(
                "This draft has been superseded",
                "A newer version of this content has been generated. Edit the current version instead.");
        }

        var (facts, profile) = await ContentRepresentation.LoadContextAsync(context, listingId, cancellationToken);

        if (request.Body is { } body
            && ContentRules.LimitFor(ContentRepresentation.Label(existing.Kind), profile) is { } limit
            && body.Length > limit)
        {
            return ApiProblems.Validation("body", $"The MLS allows at most {limit} characters here; this is {body.Length}.");
        }

        var result = await ApplyEditAsync(context, guard, audit, agentId, listingId, draftId, expectedVersion, request.Body, request.Selected, null, cancellationToken);

        switch (result.Outcome)
        {
            case ConcurrencyOutcome.NotFound:
                return ApiProblems.NotFound("No such draft on this listing.");

            case ConcurrencyOutcome.Conflict:
                var current = await context.ContentDrafts.AsNoTracking().SingleOrDefaultAsync(draft => draft.Id == draftId, cancellationToken);

                return VersionConflict.Conflict(
                    result.CurrentRowVersion,
                    current is null ? null : await ContentRepresentation.LoadOneAsync(context, current, cancellationToken));
        }

        // FR-025: edited words are re-checked at once, in this request.
        if (request.Body is not null)
        {
            await compliance.CheckDraftAsync(listingId, draftId, agentId, cancellationToken);
        }

        ListingRepresentation.SetETag(response, result.Entity!.RowVersion);

        return TypedResults.Ok(await ContentRepresentation.LoadOneAsync(context, result.Entity, cancellationToken));
    }

    /// <summary>
    /// An edit, a selection, or both, of one draft, from <paramref name="expectedVersion"/>
    /// (FR-066): the claims follow the words, an approval of edited words is revoked, an approved
    /// listing returns to review (FR-025), and the change is audited with before and after — all
    /// in one transaction. Shared by the inline edit and the one-click compliance rewrite, which
    /// records the finding it applied.
    /// </summary>
    internal static async Task<ConcurrencyResult<ContentDraft>> ApplyEditAsync(
        AppDbContext context,
        IConcurrencyGuard guard,
        IAuditWriter audit,
        Guid agentId,
        Guid listingId,
        Guid draftId,
        int expectedVersion,
        string? newBody,
        bool? selected,
        Guid? appliedFinding,
        CancellationToken cancellationToken)
    {
        object? before = null;
        var edited = false;
        var wasApproved = false;

        return await guard.MutateAsync<ContentDraft>(
            draftId,
            expectedVersion,
            draft =>
            {
                before = Snapshot(draft);
                wasApproved = draft.Status == ContentStatus.Approved;

                if (newBody is not null && newBody != draft.Body)
                {
                    var claims = GroundingService.Relocate(draft.Body, ClaimStorage.Parse(draft.SupportingFieldIds), newBody);

                    draft.Body = newBody;
                    draft.SupportingFieldIds = ClaimStorage.Serialize(claims);
                    edited = true;

                    if (wasApproved)
                    {
                        draft.Status = ContentStatus.Draft;
                    }
                }
            },
            async (draft, token) =>
            {
                var selectionChanged = selected is { } choice && choice != draft.Selected;

                if (selected == true && selectionChanged)
                {
                    // The others first: one selected draft per kind is a unique index.
                    await context.ContentDrafts
                        .Where(other => other.ListingId == listingId && other.Kind == draft.Kind && other.Id != draft.Id && other.Selected)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(other => other.Selected, false)
                                .SetProperty(other => other.RowVersion, other => other.RowVersion + 1),
                            token);
                }

                if (selected is { } chosen)
                {
                    draft.Selected = chosen;
                }

                if (edited && wasApproved)
                {
                    await RevokeAsync(context, audit, agentId, listingId, draft.Id, token);
                }

                if (edited || selectionChanged)
                {
                    // What the listing would publish changed, so an approval of the listing no longer covers it.
                    await context.Listings
                        .Where(listing => listing.Id == listingId && listing.Status == ListingStatus.Approved)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(listing => listing.Status, ListingLifecycle.AfterEdit(ListingStatus.Approved)),
                            token);
                }

                await audit.WriteAsync(
                    new AuditEntry
                    {
                        Action = AuditAction.ContentEdited,
                        ActorId = agentId,
                        ListingId = listingId,
                        BrokerageId = await BrokerageOfAsync(context, listingId, token),
                        TargetType = ContentRepresentation.DraftTarget,
                        TargetId = draft.Id,
                        BeforeValue = JsonSerializer.Serialize(before, Web),
                        AfterValue = After(draft, appliedFinding),
                    },
                    token);
            },
            cancellationToken);
    }

    private static string After(ContentDraft draft, Guid? appliedFinding)
    {
        var after = JsonSerializer.SerializeToNode(Snapshot(draft), Web)!.AsObject();

        if (appliedFinding is { } finding)
        {
            after["appliedFinding"] = finding.ToString();
        }

        return after.ToJsonString();
    }

    /// <summary>Revokes the draft's live approvals, each audited (FR-025).</summary>
    private static async Task RevokeAsync(
        AppDbContext context,
        IAuditWriter audit,
        Guid agentId,
        Guid listingId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var approvals = await context.Approvals
            .Where(approval => approval.ListingId == listingId && approval.TargetId == draftId && approval.RevokedAt == null)
            .ToListAsync(cancellationToken);

        var brokerageId = await BrokerageOfAsync(context, listingId, cancellationToken);

        foreach (var approval in approvals)
        {
            approval.RevokedAt = DateTimeOffset.UtcNow;
            approval.RevokedReason = EditedReason;

            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.ApprovalRevoked,
                    ActorId = agentId,
                    ListingId = listingId,
                    BrokerageId = brokerageId,
                    TargetType = ContentRepresentation.DraftTarget,
                    TargetId = draftId,
                    AfterValue = JsonSerializer.Serialize(new { reason = EditedReason, approvalId = approval.Id }, Web),
                },
                cancellationToken);
        }
    }

    private static Task<Guid?> BrokerageOfAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken) =>
        context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => listing.BrokerageId)
            .SingleAsync(cancellationToken);

    private static object Snapshot(ContentDraft draft) => new
    {
        body = draft.Body,
        selected = draft.Selected,
        status = ListingRepresentation.PostgresLabel(draft.Status),
    };

    /// <summary>A refusal that names the way forward, with a machine-readable reason.</summary>
    private static ProblemHttpResult ManualPath(int status, string reason, string title, string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = "https://realtorai.app/problems/manual-completion",
            Title = title,
            Status = status,
            Detail = detail,
            Extensions =
            {
                ["reason"] = reason,
                ["manualCompletionAvailable"] = true,
            },
        });
}
