using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Approvals;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Content;
using Modules.Listings;
using Npgsql;

namespace Api.Endpoints;

internal sealed record ApprovalRequest(string? TargetType, Guid? TargetId, string? Channel, string? ReviewedContentHash);

/// <summary>The contract's <c>Approval</c>.</summary>
internal sealed record ApprovalDto(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string Channel,
    string ContentHash,
    Guid ApprovedBy,
    DateTimeOffset ApprovedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// T148 — <c>POST /api/listings/{id}/approvals</c> (FR-026, FR-066, SC-008, SC-011; VIII.7).
///
/// <para>
/// <b>The hash the screen saw.</b> The caller sends the hash of the content it rendered. The server
/// recomputes the hash of the content as it is, inside a transaction that holds the row, and an
/// approval is recorded only when the two match — so an approval can never record words the agent
/// did not see. A mismatch is refused with <c>content_changed_since_review</c> and audited as
/// <c>approval_rejected_hash_mismatch</c>, committed on its own, since the attempt is evidence.
/// </para>
/// <para>
/// <b>Then the gate.</b> Every blocker the review screen shows refuses it with 409 and the
/// machine-readable list. Approving the same content again returns the approval already recorded.
/// </para>
/// <para>
/// <b>Here rather than in Modules.Content</b>, for the reason in research R20; the rules are the
/// module's (<see cref="ApprovalGate"/>).
/// </para>
/// </summary>
internal static partial class ApprovalEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/listings/{listingId:guid}/approvals")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Content")
            .MapPost("/", ApproveAsync)
            .WithName("Approve");

        return app;
    }

    private static async Task<Results<Created<ApprovalDto>, Ok<ApprovalDto>, ProblemHttpResult, UnauthorizedHttpResult>> ApproveAsync(
        Guid listingId,
        ApprovalRequest request,
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
            return ListingRepresentation.Denied(authorization, "Only the listing's agent can approve its content. An approval is theirs to give (VIII.7).");
        }

        if (request.Channel != ApprovalService.Channel)
        {
            return ApiProblems.Validation("channel", "Slice A approves for the mls channel only.");
        }

        if (request.TargetType is not ("listing" or "content_draft"))
        {
            return ApiProblems.Validation(
                "targetType",
                request.TargetType == "photo"
                    ? "Photos are approved with the listing: approve the listing once its photos are as you want them."
                    : "Approve a content_draft or the listing.");
        }

        if (request.TargetId is not { } targetId)
        {
            return ApiProblems.Validation("targetId", "Name the output being approved.");
        }

        if (request.ReviewedContentHash is not { } reviewed || !HashPattern().IsMatch(reviewed))
        {
            return ApiProblems.Validation("reviewedContentHash", "Send the SHA-256 of the content you reviewed, as 64 lowercase hex digits.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Hold what is being approved, so an edit cannot land between the hash check and the record.
        await context.Database.ExecuteSqlAsync($"select 1 from public.listings where id = {listingId} for update", cancellationToken);

        if (request.TargetType == ContentRepresentation.DraftTarget)
        {
            await context.Database.ExecuteSqlAsync($"select 1 from public.content_drafts where id = {targetId} for update", cancellationToken);
        }

        var outputs = await ApprovalService.OutputsAsync(context, listingId, cancellationToken);
        var output = outputs.FirstOrDefault(candidate => candidate.TargetType == request.TargetType && candidate.TargetId == targetId);

        if (output is null)
        {
            return ApiProblems.Validation(
                "targetId",
                "That is not an output of this listing. Only the listing, and the version of each kind chosen for publication, can be approved.");
        }

        var brokerageId = authorization.BrokerageId;

        if (output.ContentHash != reviewed)
        {
            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.ApprovalRejectedHashMismatch,
                    ActorId = agentId,
                    ListingId = listingId,
                    BrokerageId = brokerageId,
                    TargetType = output.TargetType,
                    TargetId = targetId,
                    AfterValue = JsonSerializer.Serialize(new { reviewedContentHash = reviewed, currentContentHash = output.ContentHash, channel = ApprovalService.Channel }, Web),
                },
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Blocked([new BlockerDto(BlockerCodes.ContentChangedSinceReview, "What you reviewed has changed since. Reload, review it again, and approve what you see.", targetId)]);
        }

        if (output.Approved && output.ApprovalId is { } existingId)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await context.Approvals.AsNoTracking().SingleAsync(approval => approval.Id == existingId, cancellationToken);

            return TypedResults.Ok(ToDto(existing));
        }

        if (output.Blockers.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Blocked(output.Blockers);
        }

        var approval = new Approval
        {
            Id = Guid.CreateVersion7(),
            ListingId = listingId,
            TargetType = output.TargetType,
            TargetId = targetId,
            Channel = ApprovalService.Channel,
            ContentHash = output.ContentHash,
            ApprovedBy = agentId,
        };

        // A live approval of earlier content of the same output is superseded by this one.
        await context.Approvals
            .Where(candidate => candidate.ListingId == listingId && candidate.TargetId == targetId && candidate.Channel == ApprovalService.Channel && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.RevokedAt, DateTimeOffset.UtcNow)
                    .SetProperty(candidate => candidate.RevokedReason, "superseded_by_approval"),
                cancellationToken);

        context.Approvals.Add(approval);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblems.Conflict("Already approved", "This output was approved at the same moment elsewhere. Reload the review screen.");
        }

        if (output.TargetType == ContentRepresentation.DraftTarget)
        {
            await context.ContentDrafts
                .Where(draft => draft.Id == targetId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(draft => draft.Status, ContentStatus.Approved), cancellationToken);
        }
        else
        {
            var status = await context.Listings.AsNoTracking().Where(listing => listing.Id == listingId).Select(listing => listing.Status).SingleAsync(cancellationToken);
            var next = status == ListingStatus.Draft
                ? ListingLifecycle.Transition(ListingLifecycle.Transition(ListingStatus.Draft, ListingStatus.ReadyForReview), ListingStatus.Approved)
                : ListingLifecycle.Transition(status, ListingStatus.Approved);

            await context.Listings
                .Where(listing => listing.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, next), cancellationToken);
        }

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.ApprovalGranted,
                ActorId = agentId,
                ListingId = listingId,
                BrokerageId = brokerageId,
                TargetType = output.TargetType,
                TargetId = targetId,
                AfterValue = JsonSerializer.Serialize(new { approvalId = approval.Id, channel = approval.Channel, contentHash = approval.ContentHash }, Web),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var saved = await context.Approvals.AsNoTracking().SingleAsync(candidate => candidate.Id == approval.Id, cancellationToken);

        return TypedResults.Created($"/api/listings/{listingId}/approvals/{approval.Id}", ToDto(saved));
    }

    private static ProblemHttpResult Blocked(IReadOnlyList<BlockerDto> blockers) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = "https://realtorai.app/problems/approval-blocked",
            Title = "This can't be approved yet",
            Status = StatusCodes.Status409Conflict,
            Detail = string.Join(" ", blockers.Select(blocker => blocker.Detail)),
            Extensions = { ["blockers"] = blockers },
        });

    private static ApprovalDto ToDto(Approval approval) => new(
        approval.Id,
        approval.TargetType,
        approval.TargetId,
        approval.Channel,
        approval.ContentHash,
        approval.ApprovedBy,
        approval.ApprovedAt,
        approval.RevokedAt);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
