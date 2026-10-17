using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Brokerage;

namespace Api.Endpoints;

/// <summary>The contract's <c>AuditEvent</c>.</summary>
/// <param name="BeforeValue">The stored JSON, as JSON.</param>
internal sealed record AuditEventDto(
    Guid Id,
    string Action,
    Guid? ListingId,
    Guid? ActorId,
    string? TargetType,
    Guid? TargetId,
    JsonElement? BeforeValue,
    JsonElement? AfterValue,
    string? ModelId,
    string? PromptVersion,
    string? PayloadHash,
    DateTimeOffset CreatedAt);

/// <summary>
/// T149 — the audit trail, read (FR-049, FR-050; SC-008).
///
/// <para>
/// <b>Who.</b> A listing's trail: its agent, or an approved broker admin of its brokerage when the
/// agent's own membership is approved — the resource authorizer's read rule, so a pending
/// applicant's work is not the brokerage's to inspect. The brokerage's trail: its approved broker
/// admins, holding only events about the brokerage itself or about listings of approved members.
/// </para>
/// <para>
/// <b>Paging.</b> Newest first, by a keyset cursor on (created_at, id) returned in
/// <c>X-Next-Cursor</c>, so an event written while the agent pages neither repeats nor shifts a
/// page. Read-only: the table is append-only for every role.
/// </para>
/// <para>
/// <b>Here rather than in Modules.Audit</b>, for the reason in research R20.
/// </para>
/// </summary>
internal static class AuditEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly Dictionary<string, AuditAction> Actions = Enum.GetValues<AuditAction>()
        .ToDictionary(Label, action => action, StringComparer.Ordinal);

    internal static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/listings/{listingId:guid}/audit", ListingAuditAsync)
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Audit")
            .WithName("ListingAudit");

        app.MapGet("/api/brokerages/{brokerageId:guid}/audit", BrokerageAuditAsync)
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Audit")
            .WithName("BrokerageAudit");

        return app;
    }

    private static async Task<Results<Ok<List<AuditEventDto>>, ProblemHttpResult, UnauthorizedHttpResult>> ListingAuditAsync(
        Guid listingId,
        [FromQuery] string? action,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var authorization = await authorizer.AuthorizeListingAsync(agentId, listingId, cancellationToken);

        if (!authorization.CanRead)
        {
            return ListingRepresentation.Denied(authorization, "You cannot read this listing's audit trail.");
        }

        return await PageAsync(context.AuditEvents.AsNoTracking().Where(entry => entry.ListingId == listingId), action, cursor, limit, response, cancellationToken);
    }

    private static async Task<Results<Ok<List<AuditEventDto>>, ProblemHttpResult, UnauthorizedHttpResult>> BrokerageAuditAsync(
        Guid brokerageId,
        [FromQuery] string? action,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        ClaimsPrincipal user,
        AppDbContext context,
        IMembershipResolver resolver,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Capabilities.CanReadBrokerageData(await resolver.GetMembershipAsync(agentId, brokerageId, cancellationToken)))
        {
            return ApiProblems.Forbidden("Only an approved broker admin of this brokerage can read its audit trail.");
        }

        // FR-050: never a listing whose agent is not an approved member.
        var approvedAgents = context.BrokerageMemberships.AsNoTracking()
            .Where(membership => membership.BrokerageId == brokerageId && membership.Status == MembershipStatus.Approved)
            .Select(membership => membership.AgentId);

        var visible = context.AuditEvents.AsNoTracking()
            .Where(entry => entry.BrokerageId == brokerageId
                && (entry.ListingId == null
                    || context.Listings.Any(listing => listing.Id == entry.ListingId && approvedAgents.Contains(listing.AgentId))));

        return await PageAsync(visible, action, cursor, limit, response, cancellationToken);
    }

    private static async Task<Results<Ok<List<AuditEventDto>>, ProblemHttpResult, UnauthorizedHttpResult>> PageAsync(
        IQueryable<AuditEvent> query,
        string? action,
        string? cursor,
        int? limit,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (action is not null)
        {
            if (!Actions.TryGetValue(action, out var parsed))
            {
                return ApiProblems.Validation("action", "Not an audit action.");
            }

            query = query.Where(entry => entry.Action == parsed);
        }

        if (cursor is not null)
        {
            if (DecodeCursor(cursor) is not { } after)
            {
                return ApiProblems.Validation("cursor", "Use the cursor a previous page returned.");
            }

            query = query.Where(entry => entry.CreatedAt < after.CreatedAt || (entry.CreatedAt == after.CreatedAt && entry.Id.CompareTo(after.Id) < 0));
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var page = await query
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        if (page.Count > size)
        {
            page.RemoveAt(size);
            response.Headers["X-Next-Cursor"] = EncodeCursor(page[^1]);
        }

        return TypedResults.Ok(page.Select(ToDto).ToList());
    }

    private static AuditEventDto ToDto(AuditEvent entry) => new(
        entry.Id,
        Label(entry.Action),
        entry.ListingId,
        entry.ActorId,
        entry.TargetType,
        entry.TargetId,
        Parse(entry.BeforeValue),
        Parse(entry.AfterValue),
        entry.ModelId,
        entry.PromptVersion,
        entry.PayloadHash,
        entry.CreatedAt);

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static string Label(AuditAction action) =>
        string.Concat(action.ToString().Select((character, index) =>
            char.IsUpper(character) ? (index > 0 ? "_" : string.Empty) + char.ToLowerInvariant(character) : character.ToString()));

    private static string EncodeCursor(AuditEvent entry) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{entry.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{entry.Id:D}"));

    private static (DateTimeOffset CreatedAt, Guid Id)? DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');

            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && Guid.TryParse(parts[1], out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
