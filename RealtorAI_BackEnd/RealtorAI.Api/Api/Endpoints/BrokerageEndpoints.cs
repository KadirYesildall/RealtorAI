using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Api.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Modules.Audit;
using Modules.Brokerage;
using Npgsql;

namespace Api.Endpoints;

internal sealed record DeclareMembershipRequest(
    Guid? BrokerageId,
    string? BrokerageName,
    string? BrokerageLicenseNumber,
    string? BrokerageState);

internal sealed record MembershipDecisionRequest(string? Decision);

/// <summary>Derived, never stored. Both require an approved membership (FR-061).</summary>
internal sealed record MembershipCapabilitiesDto(bool CanApproveMarketing, bool CanSubmitToMls);

/// <summary>
/// A membership as the API returns it. The agent's name and license are included because a
/// broker admin deciding a request needs to know who is asking and verify their license.
/// </summary>
internal sealed record MembershipDto(
    Guid Id,
    Guid AgentId,
    string? AgentName,
    string? AgentLicenseNumber,
    string? AgentLicenseState,
    Guid BrokerageId,
    string BrokerageName,
    string Status,
    string Role,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    MembershipCapabilitiesDto Capabilities);

internal sealed record BrokerageSettingsDto(
    Guid? BrokerageId,
    string? Name,
    string? LicenseNumber,
    string? State,
    string? LogoStorageKey,
    IReadOnlyList<string>? RequiredDisclaimers,
    string? TargetMlsId,
    string? DefaultTone,
    int? AiBudgetTokens,
    int? AiBudgetImages);

/// <summary>
/// T062 — brokerage membership and settings (FR-057, FR-060, FR-061, FR-062, FR-063).
///
/// <para>
/// <b>Authorization</b> is decided in each handler against the caller's membership of the
/// brokerage in the route, read from the database through <see cref="IMembershipResolver"/> —
/// never from the token (R7). The rules, from the contract:
/// </para>
/// <list type="bullet">
///   <item>declare / read own: any authenticated agent, for themselves only;</item>
///   <item>list and decide memberships, change settings: an <b>approved broker admin</b> of
///   that brokerage;</item>
///   <item>read settings: an <b>approved member</b>, any role, of that brokerage.</item>
/// </list>
/// <para>
/// A platform administrator has no brokerage role and gets 403 here like anyone else (FR-064).
/// After any change to a membership the resolver's cache for that agent is invalidated, so a
/// decision takes effect on the very next request rather than up to a cache window later.
/// </para>
/// <para>
/// <b>Location.</b> tasks.md places these in <c>Modules.Brokerage/Endpoints/</c>. They live in
/// the host because the EF model does, and a module cannot reference the host (research R20);
/// the lifecycle rules they apply are in <see cref="MembershipRules"/> in the module.
/// </para>
/// </summary>
internal static partial class BrokerageEndpoints
{
    private const string MembershipTarget = "brokerage_membership";

    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    internal static IEndpointRouteBuilder MapBrokerageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/brokerages")
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Brokerage");

        group.MapPost("/memberships", DeclareAsync).WithName("DeclareMembership");
        group.MapGet("/memberships", GetOwnAsync).WithName("GetOwnMembership");
        group.MapGet("/{brokerageId:guid}/memberships", ListAsync).WithName("ListBrokerageMemberships");
        group.MapPost("/{brokerageId:guid}/memberships/{membershipId:guid}/decision", DecideAsync)
            .WithName("DecideMembership");
        group.MapGet("/{brokerageId:guid}/settings", GetSettingsAsync).WithName("GetBrokerageSettings");
        group.MapPut("/{brokerageId:guid}/settings", PutSettingsAsync).WithName("UpdateBrokerageSettings");

        return app;
    }

    // ═════════════════════════════ Declare ═════════════════════════════

    private static async Task<Results<Created<MembershipDto>, ProblemHttpResult, UnauthorizedHttpResult>> DeclareAsync(
        DeclareMembershipRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IAuditWriter audit,
        IMembershipResolver resolver,
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
                "Complete your profile with your license number and state before declaring a "
                + "brokerage (FR-058).");
        }

        if (await HasLiveMembershipAsync(context, agentId, cancellationToken))
        {
            return LiveMembershipConflict();
        }

        var name = request.BrokerageName?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            return ApiProblems.Validation("brokerageName", "Enter your brokerage's name.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        Brokerage brokerage;

        if (request.BrokerageId is { } existingId)
        {
            // Locks the brokerage for the rest of the transaction, so two people declaring the
            // same empty brokerage at once cannot both become its first admin.
            await context.Database.ExecuteSqlAsync(
                $"select 1 from public.brokerages where id = {existingId} for update",
                cancellationToken);

            var found = await context.Brokerages.SingleOrDefaultAsync(
                candidate => candidate.Id == existingId,
                cancellationToken);

            if (found is null)
            {
                return ApiProblems.Validation("brokerageId", "No brokerage has that id.");
            }

            if (!string.Equals(found.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return ApiProblems.Validation(
                    "brokerageName",
                    "That name does not match the brokerage with this id. Check the id you "
                    + "were given, so you do not ask to join the wrong brokerage.");
            }

            brokerage = found;
        }
        else
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var license = request.BrokerageLicenseNumber?.Trim();
            var state = request.BrokerageState?.Trim();

            if (string.IsNullOrEmpty(license))
            {
                errors["brokerageLicenseNumber"] =
                    ["A new brokerage needs its license number; it appears in advertising disclosures."];
            }

            if (state is null || !StateCode().IsMatch(state))
            {
                errors["brokerageState"] = ["A new brokerage needs its two-letter state code, e.g. CA."];
            }

            if (errors.Count > 0)
            {
                return ApiProblems.Validation(errors);
            }

            brokerage = new Brokerage
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                LicenseNumber = license!,
                State = state!,
            };

            // Saved on its own first, inside the transaction: the rows below reference it, and
            // with no foreign keys in the EF model, EF cannot order a single batch correctly.
            context.Brokerages.Add(brokerage);
            await context.SaveChangesAsync(cancellationToken);

            context.BrokerageSettings.Add(new BrokerageSettings { BrokerageId = brokerage.Id });
        }

        var occupied = request.BrokerageId is not null
            && await context.BrokerageMemberships.AnyAsync(
                membership => membership.BrokerageId == brokerage.Id
                    && (membership.Status == MembershipStatus.Pending || membership.Status == MembershipStatus.Approved),
                cancellationToken);

        var (status, role) = MembershipRules.ForDeclaration(occupied);
        var firstRegistrant = status == MembershipStatus.Approved;

        var membership = new BrokerageMembership
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId,
            BrokerageId = brokerage.Id,
            Status = status,
            Role = role,

            // FR-062's first registrant is approved on creation, by the only person who could
            // approve them. The decision is recorded as theirs, so the decision columns are
            // complete (membership_decision_complete) and the audit trail names who decided.
            DecidedBy = firstRegistrant ? agentId : null,
            DecidedAt = firstRegistrant ? DateTimeOffset.UtcNow : null,
        };

        context.BrokerageMemberships.Add(membership);

        try
        {
            // Saved before the audit rows, for the same ordering reason. Atomicity comes from
            // the transaction, not from a single SaveChanges.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            ConstraintName: "brokerage_memberships_one_live_per_agent",
        })
        {
            // A concurrent declaration by the same agent got there first.
            resolver.Invalidate(agentId);
            return LiveMembershipConflict();
        }

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.MembershipRequested,
                ActorId = agentId,
                BrokerageId = brokerage.Id,
                TargetType = MembershipTarget,
                TargetId = membership.Id,
                AfterValue = Snapshot(new { status = FormatStatus(MembershipStatus.Pending), role = FormatRole(role) }),
            },
            cancellationToken);

        if (firstRegistrant)
        {
            await audit.WriteAsync(
                new AuditEntry
                {
                    Action = AuditAction.MembershipDecided,
                    ActorId = agentId,
                    BrokerageId = brokerage.Id,
                    TargetType = MembershipTarget,
                    TargetId = membership.Id,
                    BeforeValue = Snapshot(new { status = FormatStatus(MembershipStatus.Pending) }),
                    AfterValue = Snapshot(new
                    {
                        status = FormatStatus(status),
                        role = FormatRole(role),
                        basis = "first_registrant",
                    }),
                },
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        resolver.Invalidate(agentId);

        var dto = await LoadMembershipAsync(context, membership.Id, cancellationToken);

        return TypedResults.Created("/api/brokerages/memberships", dto!);
    }

    private static ProblemHttpResult LiveMembershipConflict() =>
        ApiProblems.Conflict(
            "You already have a brokerage membership",
            "An agent belongs to one brokerage at a time. Your current request or membership "
            + "has to be decided or end before you can declare another.");

    private static Task<bool> HasLiveMembershipAsync(AppDbContext context, Guid agentId, CancellationToken cancellationToken) =>
        context.BrokerageMemberships.AnyAsync(
            membership => membership.AgentId == agentId
                && (membership.Status == MembershipStatus.Pending || membership.Status == MembershipStatus.Approved),
            cancellationToken);

    // ═════════════════════════════ Read own ═════════════════════════════

    /// <summary>
    /// The caller's current membership: the live one if there is one, otherwise the most recent
    /// ended one — so a rejected agent is told they were rejected rather than seeing nothing.
    /// </summary>
    private static async Task<Results<Ok<MembershipDto>, ContentHttpResult, UnauthorizedHttpResult>> GetOwnAsync(
        ClaimsPrincipal user,
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var membershipId = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membership.AgentId == agentId)
            .OrderByDescending(membership =>
                membership.Status == MembershipStatus.Pending || membership.Status == MembershipStatus.Approved)
            .ThenByDescending(membership => membership.RequestedAt)
            .Select(membership => (Guid?)membership.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (membershipId is { } id && await LoadMembershipAsync(context, id, cancellationToken) is { } membership)
        {
            return TypedResults.Ok(membership);
        }

        // The contract's "or null" is the JSON literal. Ok(null) would write an empty body,
        // which a client's JSON parser rejects.
        return TypedResults.Content("null", "application/json");
    }

    // ═════════════════════════════ List ═════════════════════════════

    private static async Task<Results<Ok<List<MembershipDto>>, ProblemHttpResult, UnauthorizedHttpResult>> ListAsync(
        Guid brokerageId,
        string? status,
        ClaimsPrincipal user,
        AppDbContext context,
        IMembershipResolver resolver,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var caller = await resolver.GetMembershipAsync(agentId, brokerageId, cancellationToken);

        if (!Capabilities.CanDecideMemberships(caller))
        {
            return NotAnApprovedAdmin();
        }

        MembershipStatus? filter = null;

        if (status is not null)
        {
            if (ParseStatus(status) is not { } parsed)
            {
                return ApiProblems.Validation("status", "Use pending, approved, rejected or revoked.");
            }

            filter = parsed;
        }

        var ids = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membership.BrokerageId == brokerageId
                && (filter == null || membership.Status == filter))
            .OrderBy(membership => membership.RequestedAt)
            .Select(membership => membership.Id)
            .ToListAsync(cancellationToken);

        var rows = await LoadMembershipsAsync(context, ids, cancellationToken);

        return TypedResults.Ok(rows);
    }

    // ═════════════════════════════ Decide ═════════════════════════════

    private static async Task<Results<Ok<MembershipDto>, ProblemHttpResult, UnauthorizedHttpResult>> DecideAsync(
        Guid brokerageId,
        Guid membershipId,
        MembershipDecisionRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        IAuditWriter audit,
        IMembershipResolver resolver,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var caller = await resolver.GetMembershipAsync(agentId, brokerageId, cancellationToken);

        if (!Capabilities.CanDecideMemberships(caller))
        {
            return NotAnApprovedAdmin();
        }

        if (!MembershipRules.TryParseDecision(request.Decision, out var decided))
        {
            return ApiProblems.Validation("decision", "A decision is either approved or rejected.");
        }

        // Scoped to the brokerage in the route: an admin cannot decide another brokerage's
        // applicants by putting their own brokerage id in the path.
        var target = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId && membership.BrokerageId == brokerageId)
            .Select(membership => new { membership.AgentId, membership.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return ApiProblems.NotFound("This brokerage has no membership request with that id.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var decidedAt = DateTimeOffset.UtcNow;

        // Conditional on still being pending, so two admins deciding at once cannot both
        // succeed and leave the second decision silently overwriting the first.
        var changed = await context.BrokerageMemberships
            .Where(membership => membership.Id == membershipId
                && membership.BrokerageId == brokerageId
                && membership.Status == MembershipStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(membership => membership.Status, decided)
                    .SetProperty(membership => membership.DecidedBy, agentId)
                    .SetProperty(membership => membership.DecidedAt, decidedAt),
                cancellationToken);

        if (changed == 0)
        {
            return ApiProblems.Conflict(
                "This request has already been decided",
                $"The membership is {FormatStatus(target.Status)}. A request is decided once.");
        }

        await audit.WriteAsync(
            new AuditEntry
            {
                Action = AuditAction.MembershipDecided,
                ActorId = agentId,
                BrokerageId = brokerageId,
                TargetType = MembershipTarget,
                TargetId = membershipId,
                BeforeValue = Snapshot(new { status = FormatStatus(MembershipStatus.Pending) }),
                AfterValue = Snapshot(new { status = FormatStatus(decided) }),
            },
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // FR-060: the decision governs access from the very next request.
        resolver.Invalidate(target.AgentId);

        return TypedResults.Ok((await LoadMembershipAsync(context, membershipId, cancellationToken))!);
    }

    // ═════════════════════════════ Settings ═════════════════════════════

    private static async Task<Results<Ok<BrokerageSettingsDto>, ProblemHttpResult, UnauthorizedHttpResult>> GetSettingsAsync(
        Guid brokerageId,
        ClaimsPrincipal user,
        AppDbContext context,
        IMembershipResolver resolver,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var caller = await resolver.GetMembershipAsync(agentId, brokerageId, cancellationToken);

        if (caller is not { IsApproved: true })
        {
            return ApiProblems.Forbidden("Only approved members of this brokerage can read its settings.");
        }

        var (brokerage, settings) = await LoadSettingsAsync(context, brokerageId, tracked: false, cancellationToken);

        return brokerage is null
            ? ApiProblems.NotFound("No brokerage has that id.")
            : TypedResults.Ok(ToSettingsDto(brokerage, settings));
    }

    private static async Task<Results<Ok<BrokerageSettingsDto>, ProblemHttpResult, UnauthorizedHttpResult>> PutSettingsAsync(
        Guid brokerageId,
        BrokerageSettingsDto request,
        ClaimsPrincipal user,
        AppDbContext context,
        IMembershipResolver resolver,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var caller = await resolver.GetMembershipAsync(agentId, brokerageId, cancellationToken);

        if (!Capabilities.CanDecideMemberships(caller))
        {
            return ApiProblems.Forbidden(
                "Only an approved broker admin of this brokerage can change its settings (FR-057).");
        }

        var (brokerage, settings) = await LoadSettingsAsync(context, brokerageId, tracked: true, cancellationToken);

        if (brokerage is null)
        {
            return ApiProblems.NotFound("No brokerage has that id.");
        }

        var errors = await ValidateSettingsAsync(request, brokerageId, brokerage, context, cancellationToken);

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        brokerage.Name = request.Name!.Trim();
        brokerage.LicenseNumber = request.LicenseNumber!.Trim();
        brokerage.State = request.State!.Trim();
        brokerage.RequiredDisclaimers = JsonSerializer.Serialize(
            request.RequiredDisclaimers!.Select(disclaimer => disclaimer.Trim()).ToList(),
            SnapshotOptions);

        if (settings is null)
        {
            settings = new BrokerageSettings { BrokerageId = brokerageId };
            context.BrokerageSettings.Add(settings);
        }

        settings.TargetMlsId = string.IsNullOrWhiteSpace(request.TargetMlsId) ? null : request.TargetMlsId.Trim();
        settings.DefaultTone = ParseTone(request.DefaultTone)!.Value;
        settings.AiBudgetTokens = request.AiBudgetTokens!.Value;
        settings.AiBudgetImages = request.AiBudgetImages!.Value;

        await context.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToSettingsDto(brokerage, settings));
    }

    private static async Task<Dictionary<string, string[]>> ValidateSettingsAsync(
        BrokerageSettingsDto request,
        Guid brokerageId,
        Brokerage brokerage,
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.BrokerageId is { } bodyId && bodyId != brokerageId)
        {
            errors["brokerageId"] = ["The brokerage id in the body does not match the one in the address."];
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Enter the brokerage name; it appears in advertising disclosures."];
        }

        if (string.IsNullOrWhiteSpace(request.LicenseNumber))
        {
            errors["licenseNumber"] = ["Enter the brokerage license number."];
        }

        if (request.State is null || !StateCode().IsMatch(request.State.Trim()))
        {
            errors["state"] = ["Use the two-letter state code, e.g. CA."];
        }

        if (request.RequiredDisclaimers is null)
        {
            errors["requiredDisclaimers"] = ["Send the list of required disclaimers, or an empty list."];
        }
        else if (request.RequiredDisclaimers.Any(string.IsNullOrWhiteSpace))
        {
            errors["requiredDisclaimers"] = ["A disclaimer cannot be blank."];
        }

        if (ParseTone(request.DefaultTone) is null)
        {
            errors["defaultTone"] = ["Use professional, warm, luxury or concise."];
        }

        if (request.AiBudgetTokens is not > 0)
        {
            errors["aiBudgetTokens"] = ["The per-listing token budget must be a positive number."];
        }

        if (request.AiBudgetImages is not > 0)
        {
            errors["aiBudgetImages"] = ["The per-listing image budget must be a positive number."];
        }

        // The logo is a storage key, and no upload endpoint exists in Slice A to produce one
        // (research R32). Accepting an arbitrary key would let a brokerage point its logo at any
        // object in storage, so the value can only be sent back unchanged.
        if (request.LogoStorageKey != brokerage.LogoStorageKey)
        {
            errors["logoStorageKey"] = ["The logo cannot be changed here."];
        }

        if (!string.IsNullOrWhiteSpace(request.TargetMlsId)
            && !await context.MlsRuleProfiles.AnyAsync(
                profile => profile.Id == request.TargetMlsId.Trim(),
                cancellationToken))
        {
            errors["targetMlsId"] = [$"'{request.TargetMlsId}' is not an MLS this system has rules for."];
        }

        return errors;
    }

    // ═════════════════════════════ Loading and representation ═════════════════════════════

    private static async Task<(Brokerage? Brokerage, BrokerageSettings? Settings)> LoadSettingsAsync(
        AppDbContext context,
        Guid brokerageId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var brokerages = tracked ? context.Brokerages : context.Brokerages.AsNoTracking();
        var settings = tracked ? context.BrokerageSettings : context.BrokerageSettings.AsNoTracking();

        var brokerage = await brokerages.SingleOrDefaultAsync(candidate => candidate.Id == brokerageId, cancellationToken);

        return brokerage is null
            ? (null, null)
            : (brokerage, await settings.SingleOrDefaultAsync(candidate => candidate.BrokerageId == brokerageId, cancellationToken));
    }

    private static BrokerageSettingsDto ToSettingsDto(Brokerage brokerage, BrokerageSettings? settings)
    {
        // A brokerage without a settings row reads as the database defaults.
        settings ??= new BrokerageSettings { BrokerageId = brokerage.Id };

        return new BrokerageSettingsDto(
            brokerage.Id,
            brokerage.Name,
            brokerage.LicenseNumber,
            brokerage.State,
            brokerage.LogoStorageKey,
            JsonSerializer.Deserialize<List<string>>(brokerage.RequiredDisclaimers, SnapshotOptions) ?? [],
            settings.TargetMlsId,
            FormatTone(settings.DefaultTone),
            settings.AiBudgetTokens,
            settings.AiBudgetImages);
    }

    private static async Task<MembershipDto?> LoadMembershipAsync(
        AppDbContext context,
        Guid membershipId,
        CancellationToken cancellationToken) =>
        (await LoadMembershipsAsync(context, [membershipId], cancellationToken)).SingleOrDefault();

    private static async Task<List<MembershipDto>> LoadMembershipsAsync(
        AppDbContext context,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        var rows = await context.BrokerageMemberships
            .AsNoTracking()
            .Where(membership => membershipIds.Contains(membership.Id))
            .Join(
                context.Brokerages.AsNoTracking(),
                membership => membership.BrokerageId,
                brokerage => brokerage.Id,
                (membership, brokerage) => new { membership, BrokerageName = brokerage.Name })
            .Join(
                context.Profiles.AsNoTracking(),
                row => row.membership.AgentId,
                profile => profile.Id,
                (row, profile) => new
                {
                    row.membership,
                    row.BrokerageName,
                    profile.FullName,
                    profile.LicenseNumber,
                    profile.LicenseState,
                })
            .OrderBy(row => row.membership.RequestedAt)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                var domain = new Membership(
                    row.membership.AgentId,
                    row.membership.BrokerageId,
                    row.membership.Status,
                    row.membership.Role);

                return new MembershipDto(
                    row.membership.Id,
                    row.membership.AgentId,
                    row.FullName,
                    row.LicenseNumber,
                    row.LicenseState?.Trim(),
                    row.membership.BrokerageId,
                    row.BrokerageName,
                    FormatStatus(row.membership.Status),
                    FormatRole(row.membership.Role),
                    row.membership.RequestedAt,
                    row.membership.DecidedAt,
                    new MembershipCapabilitiesDto(
                        Capabilities.CanApproveMarketing(domain),
                        Capabilities.CanSubmitToMls(domain)));
            })
            .ToList();
    }

    private static ProblemHttpResult NotAnApprovedAdmin() =>
        ApiProblems.Forbidden(
            "Only an approved broker admin of this brokerage can see and decide its membership "
            + "requests.");

    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value, SnapshotOptions);

    // ───────────────────────────── Spellings ─────────────────────────────
    //
    // The contract's spellings, which are also the database enum labels.

    private static string FormatStatus(MembershipStatus status) => status switch
    {
        MembershipStatus.Pending => "pending",
        MembershipStatus.Approved => "approved",
        MembershipStatus.Rejected => "rejected",
        MembershipStatus.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static MembershipStatus? ParseStatus(string value) => value switch
    {
        "pending" => MembershipStatus.Pending,
        "approved" => MembershipStatus.Approved,
        "rejected" => MembershipStatus.Rejected,
        "revoked" => MembershipStatus.Revoked,
        _ => null,
    };

    private static string FormatRole(MembershipRole role) => role switch
    {
        MembershipRole.Agent => "agent",
        MembershipRole.BrokerAdmin => "broker_admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string FormatTone(Tone tone) => tone switch
    {
        Tone.Professional => "professional",
        Tone.Warm => "warm",
        Tone.Luxury => "luxury",
        Tone.Concise => "concise",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    private static Tone? ParseTone(string? value) => value switch
    {
        "professional" => Tone.Professional,
        "warm" => Tone.Warm,
        "luxury" => Tone.Luxury,
        "concise" => Tone.Concise,
        _ => null,
    };

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex StateCode();
}
