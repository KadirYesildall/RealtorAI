using System.Security.Claims;
using System.Text.Json;
using Api.Authentication;
using Api.Authorization;
using Api.Compliance;
using Api.Concurrency;
using Api.Data;
using Api.Data.Entities;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Content;
using Npgsql;
using Pipeline;

namespace Api.Endpoints;

internal sealed record RunComplianceRequest(string? TargetType, Guid? TargetId);

internal sealed record AcknowledgeRequest(string? Reason);

/// <summary>The contract's <c>ComplianceFinding</c>.</summary>
/// <param name="RuleId">The rule that raised it; stable across re-checks.</param>
/// <param name="CanAcknowledge">True only for an open warning: a blocking finding never can be (FR-022).</param>
internal sealed record ComplianceFindingDto(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string RuleId,
    string CheckType,
    string Severity,
    string Source,
    int? SpanStart,
    int? SpanEnd,
    string? Phrase,
    string Reason,
    string? SuggestedFix,
    string Status,
    bool CanAcknowledge);

/// <summary>
/// T135 — compliance: run, list, apply a rewrite, acknowledge a warning (FR-017–FR-025; Story 4).
///
/// <para>
/// <b>Here rather than in Libs.Compliance.</b> tasks.md places these in the library. They live in
/// the host with the other endpoints, for the reason in research R20. The checks themselves are
/// the library's.
/// </para>
/// <para>
/// <b>A run is a pipeline run of the compliance step</b>, over the listing or one item, held to one
/// unfinished run per listing. An edit or a rewrite re-checks the edited item at once, in the
/// request, so a fix is confirmed the moment it is made (FR-025).
/// </para>
/// <para>
/// <b>No way past a blocking finding.</b> Acknowledging one is 422, and the database refuses the
/// state anyway. No endpoint changes a finding's severity, and a finding is resolved only by a
/// re-check that no longer finds it (FR-022).
/// </para>
/// </summary>
internal static class ComplianceEndpoints
{
    private const int MinimumReasonLength = 10;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, FindingStatus> Statuses = Enum.GetValues<FindingStatus>()
        .ToDictionary(ListingRepresentation.PostgresLabel, status => status, StringComparer.Ordinal);

    private static readonly Dictionary<string, FindingSeverity> Severities = Enum.GetValues<FindingSeverity>()
        .ToDictionary(ListingRepresentation.PostgresLabel, severity => severity, StringComparer.Ordinal);

    internal static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings/{listingId:guid}/compliance")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Compliance");

        group.MapPost("/", RunAsync).WithName("RunCompliance");
        group.MapGet("/", ListAsync).WithName("ListComplianceFindings");
        group.MapPost("/{findingId:guid}/apply-fix", ApplyFixAsync).WithName("ApplyComplianceFix");
        group.MapPost("/{findingId:guid}/acknowledge", AcknowledgeAsync).WithName("AcknowledgeComplianceFinding");

        return app;
    }

    // ═════════════════════════════ Run ═════════════════════════════

    private static async Task<Results<JsonHttpResult<PipelineStatusDto>, ProblemHttpResult, UnauthorizedHttpResult>> RunAsync(
        Guid listingId,
        [FromBody] RunComplianceRequest? request,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
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
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can run its compliance checks.");
        }

        string? targetType = null;
        Guid? targetId = null;

        if (request?.TargetType is { } type && type != "listing")
        {
            if (type is not ("content_draft" or "photo") || request.TargetId is not { } id)
            {
                return ApiProblems.Validation("targetType", "Name a content_draft or photo of this listing with its targetId, or leave both out to check the whole listing.");
            }

            var onListing = type == "photo"
                ? await context.Photos.AnyAsync(photo => photo.Id == id && photo.ListingId == listingId, cancellationToken)
                : await context.ContentDrafts.AnyAsync(draft => draft.Id == id && draft.ListingId == listingId && draft.Status != ContentStatus.Stale, cancellationToken);

            if (!onListing)
            {
                return ApiProblems.Validation("targetId", "That item is not a current item of this listing.");
            }

            (targetType, targetId) = (type, id);
        }

        var runId = Guid.CreateVersion7();

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            context.PipelineRuns.Add(new PipelineRun
            {
                Id = runId,
                ListingId = listingId,
                StartedBy = agentId,
                ComplianceTargetType = targetType,
                ComplianceTargetId = targetId,
            });

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { ConstraintName: "pipeline_runs_one_active_per_listing" })
            {
                await transaction.RollbackAsync(cancellationToken);

                return TypedResults.Problem(new ProblemDetails
                {
                    Title = "This listing's pipeline is already running",
                    Detail = "Its run ends with a compliance check. Wait for it to finish.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["reason"] = "pipeline_running" },
                });
            }

            context.PipelineStepStates.Add(new PipelineStepState { RunId = runId, Step = PipelineSpellings.Of(PipelineStep.Compliance), Position = 0 });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        jobs.Create<PipelineRunner>(job => job.RunAsync(runId, CancellationToken.None), new EnqueuedState(pipeline.Queue));

        return TypedResults.Json(await PipelineEndpoints.LoadStatusAsync(context, listingId, cancellationToken), statusCode: StatusCodes.Status202Accepted);
    }

    // ═════════════════════════════ List ═════════════════════════════

    private static async Task<Results<Ok<List<ComplianceFindingDto>>, ProblemHttpResult, UnauthorizedHttpResult>> ListAsync(
        Guid listingId,
        [FromQuery] string? severity,
        [FromQuery] string? status,
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

        FindingSeverity? severityFilter = null;
        FindingStatus? statusFilter = null;

        if (severity is not null)
        {
            if (!Severities.TryGetValue(severity, out var parsed))
            {
                return ApiProblems.Validation("severity", "Use blocking or warning.");
            }

            severityFilter = parsed;
        }

        if (status is not null)
        {
            if (!Statuses.TryGetValue(status, out var parsed))
            {
                return ApiProblems.Validation("status", "Use open, resolved, acknowledged or stale.");
            }

            statusFilter = parsed;
        }

        var query = context.ComplianceFindings.AsNoTracking().Where(finding => finding.ListingId == listingId);

        // Superseded items' findings are history; they are listed only when asked for.
        query = statusFilter is { } wanted ? query.Where(finding => finding.Status == wanted) : query.Where(finding => finding.Status != FindingStatus.Stale);

        if (severityFilter is { } level)
        {
            query = query.Where(finding => finding.Severity == level);
        }

        var findings = await query.ToListAsync(cancellationToken);

        return TypedResults.Ok(findings
            .OrderBy(finding => finding.Severity == FindingSeverity.Blocking ? 0 : 1)
            .ThenBy(finding => finding.TargetType switch { "listing" => 0, "content_draft" => 1, _ => 2 })
            .ThenBy(finding => finding.TargetId)
            .ThenBy(finding => finding.SpanStart ?? -1)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList());
    }

    // ═════════════════════════════ Apply a rewrite ═════════════════════════════

    private static async Task<Results<Ok<ContentDraftDto>, ProblemHttpResult, UnauthorizedHttpResult>> ApplyFixAsync(
        Guid listingId,
        Guid findingId,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IConcurrencyGuard guard,
        IAuditWriter audit,
        ComplianceService compliance,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can change its content.");
        }

        var finding = await context.ComplianceFindings.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == findingId && candidate.ListingId == listingId, cancellationToken);

        if (finding is null)
        {
            return ApiProblems.NotFound("No such finding on this listing.");
        }

        if (finding.Status != FindingStatus.Open)
        {
            return ApiProblems.Conflict("This finding is no longer open", "It has been resolved, acknowledged or superseded. Reload the findings.");
        }

        if (finding.TargetType != ContentRepresentation.DraftTarget || finding.SuggestedFix is null || finding.SpanStart is not { } start || finding.SpanEnd is not { } end)
        {
            return ApiProblems.Validation("finding", "This finding has no rewrite to apply. Edit the content, or the listing, by hand.");
        }

        var draft = await context.ContentDrafts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == finding.TargetId, cancellationToken);

        if (draft is null || draft.Status == ContentStatus.Stale)
        {
            return ApiProblems.Conflict("The content has been superseded", "A newer version of this content exists. Check it again.");
        }

        if (end > draft.Body.Length || draft.Body[start..end] != finding.Phrase)
        {
            return ApiProblems.Conflict("The content has changed", "The words this finding pointed at are no longer where they were. Check the content again.");
        }

        var body = draft.Body[..start] + finding.SuggestedFix + draft.Body[end..];
        var (_, profile) = await ContentRepresentation.LoadContextAsync(context, listingId, cancellationToken);

        if (ContentRules.LimitFor(ContentRepresentation.Label(draft.Kind), profile) is { } limit && body.Length > limit)
        {
            return ApiProblems.Validation("finding", $"The rewrite would take the content past the MLS's {limit}-character limit. Edit it by hand.");
        }

        var result = await ContentEndpoints.ApplyEditAsync(context, guard, audit, agentId, listingId, draft.Id, draft.RowVersion, body, null, findingId, cancellationToken);

        if (result.Outcome != ConcurrencyOutcome.Applied)
        {
            return ApiProblems.Conflict("The content changed while the rewrite was applied", "Reload it and try again.");
        }

        await compliance.CheckDraftAsync(listingId, draft.Id, agentId, cancellationToken);

        var updated = await context.ContentDrafts.AsNoTracking().SingleAsync(candidate => candidate.Id == draft.Id, cancellationToken);

        return TypedResults.Ok(await ContentRepresentation.LoadOneAsync(context, updated, cancellationToken));
    }

    // ═════════════════════════════ Acknowledge ═════════════════════════════

    private static async Task<Results<Ok<ComplianceFindingDto>, ProblemHttpResult, UnauthorizedHttpResult>> AcknowledgeAsync(
        Guid listingId,
        Guid findingId,
        AcknowledgeRequest request,
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
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can acknowledge its findings.");
        }

        var finding = await context.ComplianceFindings.SingleOrDefaultAsync(candidate => candidate.Id == findingId && candidate.ListingId == listingId, cancellationToken);

        if (finding is null)
        {
            return ApiProblems.NotFound("No such finding on this listing.");
        }

        if (finding.Severity == FindingSeverity.Blocking)
        {
            return ApiProblems.Validation(
                "finding",
                "A blocking finding cannot be acknowledged or dismissed (FR-022). Change the content so the check no longer finds it.");
        }

        var reason = request.Reason?.Trim();

        if (reason is null || reason.Length < MinimumReasonLength)
        {
            return ApiProblems.Validation("reason", $"Say why, in at least {MinimumReasonLength} characters. The reason is kept in the audit trail.");
        }

        if (finding.Status != FindingStatus.Open)
        {
            return ApiProblems.Conflict("This finding is no longer open", "It has been resolved, acknowledged or superseded. Reload the findings.");
        }

        finding.Status = FindingStatus.Acknowledged;
        finding.AcknowledgedReason = reason;
        finding.AcknowledgedBy = agentId;
        finding.AcknowledgedAt = DateTimeOffset.UtcNow;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.FindingAcknowledged,
                ActorId = agentId,
                ListingId = listingId,
                BrokerageId = await context.Listings.AsNoTracking().Where(listing => listing.Id == listingId).Select(listing => listing.BrokerageId).SingleAsync(cancellationToken),
                TargetType = "compliance_finding",
                TargetId = finding.Id,
                BeforeValue = JsonSerializer.Serialize(new { status = "open" }, Web),
                AfterValue = JsonSerializer.Serialize(new { status = "acknowledged", reason, ruleId = finding.RuleId }, Web),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(ToDto(finding));
    }

    internal static ComplianceFindingDto ToDto(ComplianceFinding finding) => new(
        finding.Id,
        finding.TargetType,
        finding.TargetId,
        finding.RuleId,
        ListingRepresentation.PostgresLabel(finding.CheckType),
        ListingRepresentation.PostgresLabel(finding.Severity),
        ListingRepresentation.PostgresLabel(finding.Source),
        finding.SpanStart,
        finding.SpanEnd,
        finding.Phrase,
        finding.Reason,
        finding.SuggestedFix,
        ListingRepresentation.PostgresLabel(finding.Status),
        finding.Severity == FindingSeverity.Warning && finding.Status == FindingStatus.Open);
}
