using System.Security.Claims;
using System.Text.Json;
using Api.Authentication;
using Api.Authorization;
using Api.Concurrency;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Listings;
using Npgsql;

namespace Api.Endpoints;

internal sealed record UpsertFieldRequest(string? ResoFieldKey, JsonElement? Value, string? Provenance, string? SourceNote);

/// <summary>
/// T076 — listing fields: upsert with provenance, and confirmation of AI-derived facts
/// (FR-005, FR-006, FR-007, FR-008, FR-066).
///
/// <para>
/// <b>If-Match on an upsert.</b> The version is the field's. <c>"0"</c> means "create — this
/// field does not exist yet": versions start at 1, so 0 never matches a stored field, and a
/// create racing another create for the same key is answered with 409 and the field that won,
/// instead of silently turning into an update of something the caller never read.
/// </para>
/// <para>
/// <b>Editing an approved listing</b> returns it to review (FR-025), in the same transaction as
/// the edit, through <see cref="ListingLifecycle.AfterEdit"/>.
/// </para>
/// </summary>
internal static class ListingFieldEndpoints
{
    internal const string FieldTarget = "listing_field";

    private const string NoAiMeasurementsConstraint = "listing_fields_no_ai_measurements";
    private const string UniqueKeyConstraint = "listing_fields_unique_key";

    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapListingFieldEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings/{listingId:guid}/fields")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Listings");

        group.MapPut("/", UpsertAsync).WithName("UpsertListingField");
        group.MapPost("/{fieldId:guid}/confirm", ConfirmAsync).WithName("ConfirmListingField");

        return app;
    }

    // ═════════════════════════════ Upsert ═════════════════════════════

    private static async Task<Results<Ok<ListingFieldDto>, ProblemHttpResult, UnauthorizedHttpResult>> UpsertAsync(
        Guid listingId,
        UpsertFieldRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IConcurrencyGuard guard,
        IAuditWriter audit,
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
                "Only the listing's agent can change its fields. A broker admin's access is read-only.");
        }

        if (!IfMatch.TryParse(ifMatch, out var expectedVersion, out var preconditionError))
        {
            return VersionConflict.PreconditionRequired(preconditionError);
        }

        var listing = await context.Listings
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == listingId, cancellationToken);

        var profile = await ListingRepresentation.LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken);
        var requiredKeys = ListingRepresentation.RequiredKeys(profile);

        var (field, errors) = ListingFieldService.Validate(
            new FieldUpsertInput(request.ResoFieldKey, request.Value, request.Provenance, request.SourceNote),
            ListingRepresentation.EnumeratedLookups(profile),
            DateTimeOffset.UtcNow.Year);

        if (field is null)
        {
            return ApiProblems.Validation(errors);
        }

        var existing = await context.ListingFields
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ListingId == listingId && candidate.ResoFieldKey == field.ResoFieldKey,
                cancellationToken);

        try
        {
            return expectedVersion == 0
                ? await CreateAsync(listing, field, existing, agentId, requiredKeys, context, audit, response, cancellationToken)
                : await UpdateAsync(listing, field, existing, expectedVersion, agentId, requiredKeys, context, guard, audit, response, cancellationToken);
        }
        catch (Exception exception) when (ViolatedConstraint(exception) == NoAiMeasurementsConstraint)
        {
            // The database's own FR-007 guard. Reaching it means the list in
            // ListingFieldService has fallen behind the constraint; the caller still gets a 422.
            return ApiProblems.Validation(
                "provenance",
                $"{field.ResoFieldKey} is a measurement or legal fact and cannot carry AI provenance (FR-007).");
        }
    }

    private static async Task<Results<Ok<ListingFieldDto>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        Api.Data.Entities.Listing listing,
        ValidatedField field,
        ListingField? existing,
        Guid agentId,
        IReadOnlySet<string> requiredKeys,
        AppDbContext context,
        IAuditWriter audit,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            return VersionConflict.Conflict(existing.RowVersion, ListingRepresentation.ToDto(existing, requiredKeys));
        }

        var row = new ListingField
        {
            Id = Guid.CreateVersion7(),
            ListingId = listing.Id,
            ResoFieldKey = field.ResoFieldKey,
            Value = field.Value.GetRawText(),
            Provenance = field.Provenance,
            SourceNote = field.SourceNote,
        };

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        context.ListingFields.Add(row);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ViolatedConstraint(exception) == UniqueKeyConstraint)
        {
            // A concurrent create for the same key committed first.
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();

            var winner = await context.ListingFields
                .AsNoTracking()
                .SingleAsync(candidate => candidate.ListingId == listing.Id && candidate.ResoFieldKey == field.ResoFieldKey, cancellationToken);

            return VersionConflict.Conflict(winner.RowVersion, ListingRepresentation.ToDto(winner, requiredKeys));
        }

        await ReturnApprovedListingToReviewAsync(context, listing.Id, cancellationToken);
        await AuditEditAsync(audit, agentId, listing, row.Id, before: null, after: row, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        ListingRepresentation.SetETag(response, row.RowVersion);

        return TypedResults.Ok(ListingRepresentation.ToDto(row, requiredKeys));
    }

    private static async Task<Results<Ok<ListingFieldDto>, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        Api.Data.Entities.Listing listing,
        ValidatedField field,
        ListingField? existing,
        int expectedVersion,
        Guid agentId,
        IReadOnlySet<string> requiredKeys,
        AppDbContext context,
        IConcurrencyGuard guard,
        IAuditWriter audit,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            // The caller believes they are editing a field that does not exist — it was never
            // created, or they are looking at another listing's. Neither is an update to apply.
            return VersionConflict.Conflict(currentRowVersion: null);
        }

        var before = Snapshot(existing);

        var result = await guard.MutateAsync<ListingField>(
            existing.Id,
            expectedVersion,
            row =>
            {
                if (ListingFieldService.ChangesContent(row.Value, row.Provenance, field))
                {
                    // A confirmation vouched for the old value from the old source (FR-006).
                    row.Confirmed = false;
                    row.ConfirmedBy = null;
                    row.ConfirmedAt = null;
                }

                row.Value = field.Value.GetRawText();
                row.Provenance = field.Provenance;
                row.SourceNote = field.SourceNote;
            },
            async (row, token) =>
            {
                await ReturnApprovedListingToReviewAsync(context, listing.Id, token);
                await AuditEditAsync(audit, agentId, listing, row.Id, before, row, token);
            },
            cancellationToken);

        switch (result.Outcome)
        {
            case ConcurrencyOutcome.Applied:
                ListingRepresentation.SetETag(response, result.CurrentRowVersion!.Value);
                return TypedResults.Ok(ListingRepresentation.ToDto(result.Entity!, requiredKeys));

            case ConcurrencyOutcome.Conflict:
                var current = await context.ListingFields
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == existing.Id, cancellationToken);

                return VersionConflict.Conflict(
                    current?.RowVersion ?? result.CurrentRowVersion,
                    current is null ? null : ListingRepresentation.ToDto(current, requiredKeys));

            default:
                return VersionConflict.Conflict(currentRowVersion: null);
        }
    }

    // ═════════════════════════════ Confirm ═════════════════════════════

    /// <summary>
    /// FR-006: the human act that takes responsibility for an AI-derived value. The owner's alone
    /// — the authorizer gives a broker admin read access only, so delegation is not possible.
    /// </summary>
    private static async Task<Results<Ok<ListingFieldDto>, ProblemHttpResult, UnauthorizedHttpResult>> ConfirmAsync(
        Guid listingId,
        Guid fieldId,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IAuditWriter audit,
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
                "Only the listing's agent can confirm its facts. Confirmation cannot be delegated.");
        }

        var listing = await context.Listings
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == listingId, cancellationToken);
        var requiredKeys = ListingRepresentation.RequiredKeys(
            await ListingRepresentation.LoadRuleProfileAsync(context, listing.TargetMlsId, cancellationToken));

        // Scoped to the listing in the route, so a field id from another listing is not found here.
        var field = await context.ListingFields
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == fieldId && candidate.ListingId == listingId, cancellationToken);

        if (field is null)
        {
            return ApiProblems.NotFound("This listing has no field with that id.");
        }

        if (!ListingFieldService.RequiresConfirmation(field.Provenance))
        {
            return ApiProblems.Validation(
                "provenance",
                $"{field.ResoFieldKey} was {(field.Provenance == Provenance.AgentEntered ? "entered by you" : "taken from a public record")}; "
                + "only AI-derived values need confirming (FR-006).");
        }

        if (!field.Confirmed)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Conditional on still being unconfirmed, so two confirmations at once record one.
            var changed = await context.Database.ExecuteSqlAsync(
                $"""
                update public.listing_fields
                set confirmed = true,
                    confirmed_by = {agentId},
                    confirmed_at = timezone('utc', now()),
                    row_version = row_version + 1
                where id = {fieldId} and confirmed = false
                """,
                cancellationToken);

            if (changed == 1)
            {
                await audit.WriteAsync(
                    new AuditEntry
                    {
                        Action = AuditAction.FieldConfirm,
                        ActorId = agentId,
                        ListingId = listingId,
                        BrokerageId = listing.BrokerageId,
                        TargetType = FieldTarget,
                        TargetId = fieldId,
                        AfterValue = JsonSerializer.Serialize(
                            new
                            {
                                resoFieldKey = field.ResoFieldKey,
                                value = JsonDocument.Parse(field.Value).RootElement,
                                provenance = ListingFieldService.Format(field.Provenance),
                            },
                            SnapshotOptions),
                    },
                    cancellationToken);

                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            field = await context.ListingFields
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == fieldId, cancellationToken);
        }

        ListingRepresentation.SetETag(response, field.RowVersion);

        return TypedResults.Ok(ListingRepresentation.ToDto(field, requiredKeys));
    }

    // ═════════════════════════════ Helpers ═════════════════════════════

    /// <summary>FR-025: an edit to an approved listing returns it to review.</summary>
    private static Task<int> ReturnApprovedListingToReviewAsync(AppDbContext context, Guid listingId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync(
            $"""
            update public.listings
            set status = 'ready_for_review', row_version = row_version + 1, updated_at = timezone('utc', now())
            where id = {listingId} and status = 'approved'
            """,
            cancellationToken);

    private static Task AuditEditAsync(
        IAuditWriter audit,
        Guid agentId,
        Api.Data.Entities.Listing listing,
        Guid fieldId,
        object? before,
        ListingField? after,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.FieldEdit,
                ActorId = agentId,
                ListingId = listing.Id,
                BrokerageId = listing.BrokerageId,
                TargetType = FieldTarget,
                TargetId = fieldId,
                BeforeValue = before is null ? null : JsonSerializer.Serialize(before, SnapshotOptions),
                AfterValue = after is null ? null : JsonSerializer.Serialize(Snapshot(after), SnapshotOptions),
            },
            cancellationToken);

    private static object Snapshot(ListingField field) => new
    {
        resoFieldKey = field.ResoFieldKey,
        value = JsonDocument.Parse(field.Value).RootElement.Clone(),
        provenance = ListingFieldService.Format(field.Provenance),
        sourceNote = field.SourceNote,
        confirmed = field.Confirmed,
    };

    private static string? ViolatedConstraint(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.ConstraintName;
            }
        }

        return null;
    }
}
