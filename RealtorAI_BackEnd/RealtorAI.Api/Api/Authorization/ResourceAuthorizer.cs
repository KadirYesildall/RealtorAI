using Api.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Brokerage;
using DomainRole = Modules.Brokerage.MembershipRole;

namespace Api.Authorization;

/// <summary>What a caller may do with a listing.</summary>
public enum ListingAccess
{
    /// <summary>No access. The endpoint answers 404 or 403 — see <see cref="ListingAuthorization"/>.</summary>
    None,

    /// <summary>
    /// May read, may not modify. This is what an approved broker admin gets over their agents'
    /// listings, mirroring the <c>listings_broker_admin_select</c> policy: oversight is a read
    /// concern, and an admin editing an agent's listing would put the agent's license behind
    /// content they did not write.
    /// </summary>
    Read,

    /// <summary>May read and modify. The owning agent only.</summary>
    ReadWrite,
}

/// <summary>
/// Why a decision came out the way it did. Carried so the endpoint can answer accurately and
/// the audit record can state the reason, rather than both inferring it from a bare boolean.
/// </summary>
public enum AuthorizationReason
{
    /// <summary>The caller owns the listing.</summary>
    Owner,

    /// <summary>The caller is an approved broker admin and the owning agent is an approved member.</summary>
    ApprovedBrokerAdmin,

    /// <summary>No listing with that id exists.</summary>
    ListingNotFound,

    /// <summary>The listing exists but the caller has no relationship to it.</summary>
    NotRelated,

    /// <summary>
    /// The caller is a broker admin of the listing's brokerage, but the owning agent's
    /// membership is not approved, so FR-050 withholds the listing.
    /// </summary>
    SubjectMembershipNotApproved,

    /// <summary>
    /// The caller holds the broker-admin role in the listing's brokerage but their own
    /// membership is not approved.
    /// </summary>
    CallerMembershipNotApproved,

    /// <summary>Write was requested on a listing the caller may only read.</summary>
    WriteRequiresOwnership,

    /// <summary>Write was requested on a soft-deleted listing.</summary>
    ListingArchived,
}

/// <summary>Outcome of a listing authorization check.</summary>
/// <param name="Access">What the caller may do.</param>
/// <param name="Reason">Why, for problem details and the audit trail.</param>
/// <param name="OwnerAgentId">The listing's owner, when the listing exists.</param>
/// <param name="BrokerageId">The listing's brokerage, when it has one.</param>
public sealed record ListingAuthorization(
    ListingAccess Access,
    AuthorizationReason Reason,
    Guid? OwnerAgentId,
    Guid? BrokerageId)
{
    public bool CanRead => Access is ListingAccess.Read or ListingAccess.ReadWrite;

    public bool CanWrite => Access is ListingAccess.ReadWrite;

    /// <summary>
    /// True when the right answer is 404 rather than 403.
    ///
    /// <para>
    /// A listing the caller has no relationship to is reported as not found, not as forbidden.
    /// 403 on an unrelated id confirms that the id exists, which turns a listing endpoint into
    /// an enumeration oracle: an attacker could walk ids and learn which are real. The caller
    /// genuinely cannot distinguish the two cases, so neither should the response.
    /// </para>
    /// </summary>
    public bool ShouldReportAsNotFound =>
        Reason is AuthorizationReason.ListingNotFound
            or AuthorizationReason.NotRelated
            or AuthorizationReason.SubjectMembershipNotApproved
            or AuthorizationReason.CallerMembershipNotApproved;
}

/// <summary>
/// T035 — decides what a caller may do with a listing (FR-063).
///
/// <para>
/// <b>This exists because the API connects with a privileged role that bypasses RLS.</b> The
/// row-level policies in the migrations are defence in depth for direct client reads; they do
/// not constrain this service at all. So every endpoint authorizes here independently, and the
/// decision is made against committed database state rather than anything the caller supplied.
/// </para>
/// <para>
/// The rules, and the reasoning behind the non-obvious ones:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The owning agent gets read and write.
///   </description></item>
///   <item><description>
///     An approved broker admin of the listing's brokerage gets read only — oversight without
///     the ability to alter content the agent is licensed for.
///   </description></item>
///   <item><description>
///     <b>Both</b> memberships must be approved. FR-050 is explicit that an admin never sees
///     the data of an agent whose membership is pending or rejected, so a brokerage cannot
///     acquire visibility into an agent's work simply by having that agent type its name.
///   </description></item>
///   <item><description>
///     A soft-deleted listing stays readable (its audit trail must remain queryable, FR-067)
///     but is not writable.
///   </description></item>
/// </list>
/// <para>
/// Platform administrators are deliberately absent. <c>platform_admin</c> governs the AI model
/// registry and prompt versions (FR-064); it confers no access to listing content, and adding
/// it here would quietly turn a configuration role into a data-reading one.
/// </para>
/// </summary>
public interface IResourceAuthorizer
{
    Task<ListingAuthorization> AuthorizeListingAsync(
        Guid callerAgentId,
        Guid listingId,
        CancellationToken cancellationToken);
}

internal sealed class ResourceAuthorizer : IResourceAuthorizer
{
    private readonly AppDbContext context;
    private readonly IMembershipResolver memberships;

    public ResourceAuthorizer(AppDbContext context, IMembershipResolver memberships)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(memberships);

        this.context = context;
        this.memberships = memberships;
    }

    public async Task<ListingAuthorization> AuthorizeListingAsync(
        Guid callerAgentId,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var listing = await context.Listings
            .AsNoTracking()
            .Where(candidate => candidate.Id == listingId)
            .Select(candidate => new
            {
                candidate.AgentId,
                candidate.BrokerageId,
                candidate.DeletedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (listing is null)
        {
            return new ListingAuthorization(
                ListingAccess.None,
                AuthorizationReason.ListingNotFound,
                OwnerAgentId: null,
                BrokerageId: null);
        }

        // ── Owner ──
        if (listing.AgentId == callerAgentId)
        {
            // A soft-deleted listing is readable so its history stays available, but not
            // writable: editing an archived listing would restart work the agent chose to
            // stop, and the retention clock in FR-065 runs from deleted_at.
            var permission = listing.DeletedAt is null
                ? ListingAccess.ReadWrite
                : ListingAccess.Read;

            var reason = listing.DeletedAt is null
                ? AuthorizationReason.Owner
                : AuthorizationReason.ListingArchived;

            return new ListingAuthorization(
                permission,
                reason,
                listing.AgentId,
                listing.BrokerageId);
        }

        // ── Broker admin ──
        //
        // A listing with no brokerage can only ever be reached by its owner; there is no
        // brokerage for an admin to derive authority from.
        if (listing.BrokerageId is not { } brokerageId)
        {
            return Denied(AuthorizationReason.NotRelated, listing.AgentId, listing.BrokerageId);
        }

        var callerMembership = await memberships.GetMembershipAsync(
            callerAgentId,
            brokerageId,
            cancellationToken);

        if (callerMembership is null)
        {
            return Denied(AuthorizationReason.NotRelated, listing.AgentId, brokerageId);
        }

        if (!Capabilities.CanReadBrokerageData(callerMembership))
        {
            // Distinguishes "not approved yet" from "approved but only an agent", because the
            // first is a pending state the UI explains and the second is a settled no.
            var reason = callerMembership.Role == DomainRole.BrokerAdmin
                ? AuthorizationReason.CallerMembershipNotApproved
                : AuthorizationReason.NotRelated;

            return Denied(reason, listing.AgentId, brokerageId);
        }

        // FR-050's second condition: the subject agent's own membership must be approved.
        var subjectMembership = await memberships.GetMembershipAsync(
            listing.AgentId,
            brokerageId,
            cancellationToken);

        if (subjectMembership is null || !subjectMembership.IsApproved)
        {
            return Denied(
                AuthorizationReason.SubjectMembershipNotApproved,
                listing.AgentId,
                brokerageId);
        }

        return new ListingAuthorization(
            ListingAccess.Read,
            AuthorizationReason.ApprovedBrokerAdmin,
            listing.AgentId,
            brokerageId);
    }

    private static ListingAuthorization Denied(
        AuthorizationReason reason,
        Guid ownerAgentId,
        Guid? brokerageId) =>
        new(ListingAccess.None, reason, ownerAgentId, brokerageId);
}
