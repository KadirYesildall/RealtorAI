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
using Modules.Brokerage;
using Modules.Listings;
using Modules.Listings.PropertyData;
using Modules.Media;
using ListingEntity = Api.Data.Entities.Listing;

namespace Api.Endpoints;

/// <summary>
/// T075 — listings: create, read, list, autosave, soft delete (FR-005, FR-008, FR-058, FR-061,
/// FR-063, FR-066, FR-067).
///
/// <para>
/// <b>Authorization</b> runs in each handler through <see cref="IResourceAuthorizer"/>, against
/// committed rows, because the API's database role bypasses RLS (FR-063). Reads accept the owner
/// or an approved broker admin of an approved owner (FR-050); every write is the owner's alone.
/// </para>
/// <para>
/// <b>Location.</b> tasks.md places these in <c>Modules.Listings/Endpoints/</c>. They live in the
/// host because the EF model does, and a module cannot reference the host (research R20, the
/// precedent set by T062); the rules they apply — the lifecycle, the basics, field provenance,
/// pre-fill — are in <c>Modules.Listings</c>.
/// </para>
/// </summary>
internal static class ListingEndpoints
{
    internal const string ListingTarget = "listing";

    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapListingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Listings");

        group.MapPost("/", CreateAsync).WithName("CreateListing");
        group.MapGet("/", ListAsync).WithName("ListListings");
        group.MapGet("/{listingId:guid}", GetAsync).WithName("GetListing");
        group.MapPatch("/{listingId:guid}", AutosaveAsync).WithName("AutosaveListing");
        group.MapDelete("/{listingId:guid}", DeleteAsync).WithName("SoftDeleteListing");

        return app;
    }

    // ═════════════════════════════ Create ═════════════════════════════

    private static async Task<Results<Created<ListingDto>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        ListingBasicsRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IAuditWriter audit,
        PrefillService prefill,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var profileCompleted = await context.Profiles
            .AsNoTracking()
            .AnyAsync(profile => profile.Id == agentId && profile.ProfileCompletedAt != null, cancellationToken);

        if (!profileCompleted)
        {
            return ApiProblems.Forbidden(
                "Complete your profile with your license number and state before creating a listing (FR-058).");
        }

        var (basics, errors) = ListingBasicsRules.Validate(request.ToInput());

        if (basics is null)
        {
            return ApiProblems.Validation(errors);
        }

        // FR-061: a pending membership is enough to file the listing under the brokerage — the
        // authorizer grants that brokerage nothing until both memberships are approved. An
        // ended membership is not: work done after a rejection is not that brokerage's.
        var brokerageId = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membership.AgentId == agentId
                && (membership.Status == MembershipStatus.Pending || membership.Status == MembershipStatus.Approved))
            .Select(membership => (Guid?)membership.BrokerageId)
            .SingleOrDefaultAsync(cancellationToken);

        // The MLS comes from the brokerage's settings, never from the request: which rulebook a
        // listing is checked against is the brokerage's decision, not something a client picks.
        var targetMlsId = brokerageId is { } id
            ? await context.BrokerageSettings
                .AsNoTracking()
                .Where(settings => settings.BrokerageId == id)
                .Select(settings => settings.TargetMlsId)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var plan = await prefill.PlanAsync(
            new PropertyAddress(basics.AddressLine1, null, basics.City, basics.State, basics.PostalCode),
            cancellationToken);

        var listing = new ListingEntity
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
            BrokerageId = brokerageId,
            Status = ListingStatus.Draft,
            TargetMlsId = targetMlsId,
        };
        Apply(listing, basics);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Saved alone first: the fields and audit rows below reference it, and the model has no
        // foreign-key metadata to order one batch by (research R31).
        context.Listings.Add(listing);
        await context.SaveChangesAsync(cancellationToken);

        var prefilled = plan.Fields
            .Select(field => new ListingField
            {
                Id = Guid.CreateVersion7(),
                ListingId = listing.Id,
                ResoFieldKey = field.ResoFieldKey,
                Value = field.Value.GetRawText(),
                Provenance = Provenance.PublicRecord,
                SourceNote = field.SourceNote,
            })
            .ToList();

        context.ListingFields.AddRange(prefilled);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var field in prefilled)
        {
            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.FieldEdit,
                    ActorId = agentId,
                    ListingId = listing.Id,
                    TargetType = ListingFieldEndpoints.FieldTarget,
                    TargetId = field.Id,
                    AfterValue = Snapshot(new
                    {
                        resoFieldKey = field.ResoFieldKey,
                        value = JsonDocument.Parse(field.Value).RootElement,
                        provenance = ListingFieldService.Format(field.Provenance),
                        sourceNote = field.SourceNote,
                    }),
                },
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        ListingRepresentation.SetETag(response, listing.RowVersion);

        return TypedResults.Created($"/api/listings/{listing.Id}", ListingRepresentation.ToDto(listing));
    }

    // ═════════════════════════════ Read ═════════════════════════════

    private static async Task<Results<Ok<List<ListingDto>>, UnauthorizedHttpResult>> ListAsync(
        bool? includeArchived,
        ClaimsPrincipal user,
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var archived = includeArchived ?? false;

        // The caller's own listings only. A broker admin reviews an agent's listing by its id;
        // this is the agent's working list, and FR-067 keeps archived listings out of it unless
        // asked for.
        var listings = await context.Listings
            .AsNoTracking()
            .Where(listing => listing.AgentId == agentId && (archived || listing.DeletedAt == null))
            .OrderByDescending(listing => listing.UpdatedAt)
            .ThenByDescending(listing => listing.Id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(listings.Select(ListingRepresentation.ToDto).ToList());
    }

    private static async Task<Results<Ok<ListingDetailDto>, ProblemHttpResult, UnauthorizedHttpResult>> GetAsync(
        Guid listingId,
        ClaimsPrincipal user,
        AppDbContext context,
        IResourceAuthorizer authorizer,
        IPhotoStorage storage,
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
            return ListingRepresentation.Denied(authorization, "You cannot read this listing.");
        }

        var listing = await context.Listings
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == listingId, cancellationToken);

        ListingRepresentation.SetETag(response, listing.RowVersion);

        return TypedResults.Ok(await ListingRepresentation.LoadDetailAsync(context, storage, listing, cancellationToken));
    }

    // ═════════════════════════════ Autosave ═════════════════════════════

    private static async Task<Results<Ok<ListingDto>, ProblemHttpResult, UnauthorizedHttpResult>> AutosaveAsync(
        Guid listingId,
        ListingBasicsRequest request,
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
                "Only the listing's agent can edit it. A broker admin's access is read-only.");
        }

        if (!IfMatch.TryParse(ifMatch, out var expectedVersion, out var preconditionError))
        {
            return VersionConflict.PreconditionRequired(preconditionError);
        }

        var (basics, errors) = ListingBasicsRules.Validate(request.ToInput());

        if (basics is null)
        {
            return ApiProblems.Validation(errors);
        }

        object? before = null;
        object? after = null;

        var result = await guard.MutateAsync<ListingEntity>(
            listingId,
            expectedVersion,
            listing =>
            {
                before = Snapshot(listing);
                Apply(listing, basics);
                after = Snapshot(listing);

                if (!Equals(before, after))
                {
                    listing.Status = ListingLifecycle.AfterEdit(listing.Status);
                    listing.UpdatedAt = DateTimeOffset.UtcNow;
                }
            },
            async (listing, token) =>
            {
                // FR-049: an edit records what it replaced. A save that changed nothing — the
                // usual autosave tick — is not an edit, and recording it would bury real ones.
                if (!Equals(before, after))
                {
                    await audit.WriteAsync(
                        new AuditEntry
                        {
                            Action = AuditAction.FieldEdit,
                            ActorId = agentId,
                            ListingId = listing.Id,
                            BrokerageId = listing.BrokerageId,
                            TargetType = ListingTarget,
                            TargetId = listing.Id,
                            BeforeValue = JsonSerializer.Serialize(before, SnapshotOptions),
                            AfterValue = JsonSerializer.Serialize(after, SnapshotOptions),
                        },
                        token);
                }
            },
            cancellationToken);

        switch (result.Outcome)
        {
            case ConcurrencyOutcome.Applied:
                ListingRepresentation.SetETag(response, result.CurrentRowVersion!.Value);
                return TypedResults.Ok(ListingRepresentation.ToDto(result.Entity!));

            case ConcurrencyOutcome.Conflict:
                var current = await context.Listings
                    .AsNoTracking()
                    .SingleOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);

                return VersionConflict.Conflict(
                    current?.RowVersion ?? result.CurrentRowVersion,
                    current is null ? null : ListingRepresentation.ToDto(current));

            default:
                return ApiProblems.NotFound("No listing with that id is available to you.");
        }
    }

    // ═════════════════════════════ Soft delete ═════════════════════════════

    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> DeleteAsync(
        Guid listingId,
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

        // Idempotent: the owner deleting an already-archived listing is told it is done.
        if (authorization.Reason == AuthorizationReason.ListingArchived)
        {
            return TypedResults.NoContent();
        }

        if (!authorization.CanWrite)
        {
            return ListingRepresentation.Denied(
                authorization,
                "Only the listing's agent can delete it. A broker admin may not delete an agent's listing.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Locked, so a concurrent delete waits here and then finds the listing archived rather
        // than archiving it a second time and writing a second audit event.
        await context.Database.ExecuteSqlAsync(
            $"select 1 from public.listings where id = {listingId} for update",
            cancellationToken);

        var listing = await context.Listings
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == listingId, cancellationToken);

        if (listing.DeletedAt is not null)
        {
            return TypedResults.NoContent();
        }

        var before = listing.Status;
        ListingLifecycle.Transition(before, ListingStatus.Archived);

        // One statement, on the database clock: deleted_at starts the FR-065 retention period,
        // and an application server's clock is not the one the purge job will compare against.
        await context.Database.ExecuteSqlAsync(
            $"""
            update public.listings
            set status = 'archived',
                deleted_at = timezone('utc', now()),
                updated_at = timezone('utc', now()),
                row_version = row_version + 1
            where id = {listingId}
            """,
            cancellationToken);

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.ListingSoftDeleted,
                ActorId = agentId,
                ListingId = listingId,
                BrokerageId = listing.BrokerageId,
                TargetType = ListingTarget,
                TargetId = listingId,
                BeforeValue = JsonSerializer.Serialize(new { status = ListingRepresentation.FormatStatus(before) }, SnapshotOptions),
                AfterValue = JsonSerializer.Serialize(new { status = ListingRepresentation.FormatStatus(ListingStatus.Archived) }, SnapshotOptions),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    // ═════════════════════════════ Helpers ═════════════════════════════

    private static void Apply(ListingEntity listing, ListingBasics basics)
    {
        listing.AddressLine1 = basics.AddressLine1;
        listing.City = basics.City;
        listing.State = basics.State;
        listing.PostalCode = basics.PostalCode;
        listing.PropertyType = basics.PropertyType;
        listing.ListPrice = basics.ListPrice;
        listing.Bedrooms = basics.Bedrooms;
        listing.Bathrooms = basics.Bathrooms;
        listing.AgentNotes = basics.AgentNotes;
    }

    /// <summary>
    /// The basics as an audit snapshot. A record, so two snapshots compare by value — which is
    /// how a no-change save is recognised (decimal equality ignores scale, so 3.5 equals 3.50).
    /// </summary>
    private sealed record BasicsSnapshot(
        string AddressLine1,
        string City,
        string State,
        string PostalCode,
        string PropertyType,
        decimal? ListPrice,
        int? Bedrooms,
        decimal? Bathrooms,
        string? AgentNotes);

    private static BasicsSnapshot Snapshot(ListingEntity listing) => new(
        listing.AddressLine1,
        listing.City,
        listing.State.Trim(),
        listing.PostalCode,
        listing.PropertyType,
        listing.ListPrice,
        listing.Bedrooms,
        listing.Bathrooms,
        listing.AgentNotes);

    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value, SnapshotOptions);
}
