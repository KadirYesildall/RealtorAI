namespace Modules.Brokerage;

/// <summary>
/// T036 — what a membership permits, as pure predicates over a <see cref="Membership"/>.
///
/// <para>
/// FR-061 draws a specific line, and it is worth stating why rather than just encoding it. An
/// agent whose membership is still <c>pending</c> may do all of the private work: draft a
/// listing, run photo analysis, generate content, run compliance checks, and approve that
/// content for their own review. What they may not do is anything that publishes the
/// brokerage's identity to the outside world — marketing output and MLS submission both carry
/// the brokerage's verified name and its license disclosures, and a brokerage cannot be made to
/// vouch for someone it has not confirmed works there.
/// </para>
/// <para>
/// So the gate is not "is this user trusted" but "has this brokerage accepted responsibility
/// for this agent's output". That is why both predicates below require
/// <see cref="MembershipStatus.Approved"/> and neither accepts a role as a substitute.
/// </para>
/// <para>
/// These are static and side-effect-free on purpose: a capability decision that cannot touch a
/// database or a clock is one that behaves identically in a unit test and in production, and
/// every branch is reachable from a table-driven test.
/// </para>
/// </summary>
public static class Capabilities
{
    /// <summary>
    /// May the agent approve marketing output for publication (FR-061)?
    ///
    /// <para>
    /// Requires an approved membership. Marketing copy carries the brokerage's name and its
    /// required disclaimers, so publishing it without the brokerage having confirmed the agent
    /// would attribute a public claim to a firm that never accepted it.
    /// </para>
    /// <para>
    /// A null membership means the agent declared no brokerage at all, which is permitted for
    /// private drafting but cannot satisfy a rule that exists to protect a brokerage's
    /// identity. Returning false for null keeps "no brokerage" from being a way around the
    /// check rather than a case it forgot to handle.
    /// </para>
    /// </summary>
    public static bool CanApproveMarketing(Membership? membership) =>
        membership is { IsApproved: true };

    /// <summary>
    /// May the agent submit the listing to an MLS (FR-061)?
    ///
    /// <para>
    /// Requires an approved membership, for the same reason as marketing and one more: an MLS
    /// submission is made under the brokerage's MLS participation and its license number. An
    /// unconfirmed agent submitting under it would be a compliance problem for the brokerage,
    /// not merely a product one.
    /// </para>
    /// <para>
    /// Note this is the capability only. Slice A enforces it but deliberately ships no MLS
    /// adapter, so nothing yet acts on a true result (see contracts/slice-b-seams.md). The
    /// predicate exists now so Slice B cannot be built on an unguarded path.
    /// </para>
    /// </summary>
    public static bool CanSubmitToMls(Membership? membership) =>
        membership is { IsApproved: true };

    /// <summary>
    /// May the agent do the private listing-preparation work — drafting, analysis, content
    /// generation, compliance checks, and approving their own content for their own review?
    ///
    /// <para>
    /// Yes, even with a pending membership, and yes with no brokerage at all. This is the
    /// permissive half of FR-061 and it is intentional: making an agent wait for an admin
    /// before they can try the product would gate the entire feature behind someone else's
    /// availability. Nothing here leaves the agent's own account.
    /// </para>
    /// </summary>
    public static bool CanPrepareListing(Membership? membership) =>
        membership is null || membership.Status is MembershipStatus.Pending
            or MembershipStatus.Approved
            or MembershipStatus.Rejected
            or MembershipStatus.Revoked;

    /// <summary>
    /// May the agent decide other agents' membership requests for this brokerage (FR-062)?
    /// Requires an approved broker admin — a pending admin decides nothing, including their
    /// own request.
    /// </summary>
    public static bool CanDecideMemberships(Membership? membership) =>
        membership is { IsApprovedBrokerAdmin: true };

    /// <summary>
    /// May the agent read brokerage-scoped data belonging to other agents — their listings and
    /// audit trail (FR-050, FR-063)?
    ///
    /// <para>
    /// Requires an approved broker admin. FR-050 adds a second condition this predicate cannot
    /// express: the *subject* agent's membership must also be approved, so an admin never sees
    /// the listings of someone whose membership is pending or rejected. Access therefore runs
    /// through the resource authorizer, which checks both sides; this predicate only answers
    /// whether the caller holds the role at all.
    /// </para>
    /// </summary>
    public static bool CanReadBrokerageData(Membership? membership) =>
        membership is { IsApprovedBrokerAdmin: true };
}
