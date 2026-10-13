namespace Modules.Brokerage;

/// <summary>
/// Lifecycle of an agent's claim to belong to a brokerage (FR-060).
///
/// <para>
/// These are the module's own domain values, deliberately separate from the persistence enums
/// in <c>Api.Data</c>. The reason is the project reference graph: the host references the
/// modules, so a module cannot reference the host without a cycle. Rather than move the EF
/// model into a shared project — which would let persistence concerns leak into every module —
/// the boundary is made explicit, and <c>MembershipMappingTests</c> asserts the translation
/// stays total in both directions, so a value added on one side cannot silently go unhandled on
/// the other.
/// </para>
/// </summary>
public enum MembershipStatus
{
    /// <summary>
    /// Self-declared and not yet decided. The default on creation: an agent typing a brokerage
    /// name is a claim, not a credential, so nothing brokerage-scoped is readable in either
    /// direction until an admin approves (FR-060).
    /// </summary>
    Pending,

    /// <summary>Confirmed by an approved broker admin of that brokerage.</summary>
    Approved,

    /// <summary>Declined. Treated exactly as <see cref="Pending"/> for access purposes.</summary>
    Rejected,

    /// <summary>
    /// Previously approved, since withdrawn. Separate from <see cref="Rejected"/> so the audit
    /// trail distinguishes "was never a member" from "is no longer one", which matters when
    /// reviewing who could see what at a given time.
    /// </summary>
    Revoked,
}

/// <summary>Role an agent holds within a brokerage (FR-062).</summary>
public enum MembershipRole
{
    /// <summary>Ordinary agent: their own listings only.</summary>
    Agent,

    /// <summary>
    /// Broker admin: may decide membership requests and read the listings and audit trail of
    /// agents whose membership is approved. The first registrant of a brokerage receives this
    /// automatically; later admins are promoted only by an existing admin (FR-062).
    /// </summary>
    BrokerAdmin,
}

/// <summary>
/// An agent's membership in one brokerage, as resolved for the current request.
///
/// <para>
/// This type is never built from JWT claims. A Supabase access token lives for an hour, so a
/// claim-derived role would let a revoked broker admin keep reading an agent's listings and
/// audit trail until their token happened to refresh. Membership is read from the database on
/// each request instead — see <see cref="MembershipResolver"/>.
/// </para>
/// </summary>
/// <param name="AgentId">The agent's profile id (the token's <c>sub</c>).</param>
/// <param name="BrokerageId">The brokerage the membership is in.</param>
/// <param name="Status">Where the membership sits in its lifecycle.</param>
/// <param name="Role">What the agent may do inside the brokerage, if approved.</param>
public sealed record Membership(
    Guid AgentId,
    Guid BrokerageId,
    MembershipStatus Status,
    MembershipRole Role)
{
    /// <summary>
    /// True only for <see cref="MembershipStatus.Approved"/>.
    ///
    /// <para>
    /// Written as an equality test against the one permitted value rather than as a list of
    /// excluded ones. If a status is added later, this stays correct by denying it; the
    /// inverse spelling (<c>Status != Rejected</c>) would silently start granting access.
    /// </para>
    /// </summary>
    public bool IsApproved => Status == MembershipStatus.Approved;

    /// <summary>
    /// True when the agent is an approved broker admin. Both halves are required: an
    /// unapproved row carrying <see cref="MembershipRole.BrokerAdmin"/> confers nothing,
    /// which is what makes the first-registrant rule in FR-062 safe to implement by writing
    /// the role at request time.
    /// </summary>
    public bool IsApprovedBrokerAdmin => IsApproved && Role == MembershipRole.BrokerAdmin;
}
