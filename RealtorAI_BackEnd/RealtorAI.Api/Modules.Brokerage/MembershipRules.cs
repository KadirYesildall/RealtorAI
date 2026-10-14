namespace Modules.Brokerage;

/// <summary>
/// T062 — the membership lifecycle rules (FR-060, FR-062), as pure functions so every branch is
/// unit-testable without a database.
/// </summary>
public static class MembershipRules
{
    /// <summary>
    /// The membership a declaration creates.
    ///
    /// <para>
    /// FR-062: the first registrant of a brokerage becomes its approved broker admin — someone
    /// has to be able to approve everyone else. Anyone joining a brokerage that already has a
    /// live member makes a claim, which starts <see cref="MembershipStatus.Pending"/> and
    /// confers nothing until an admin decides it (FR-060).
    /// </para>
    /// <para>
    /// "Live" means pending or approved. History — rejected or revoked rows — does not make a
    /// brokerage occupied: a brokerage whose every membership has ended has nobody left to
    /// approve a newcomer, and leaving it permanently unjoinable would strand it.
    /// </para>
    /// </summary>
    public static (MembershipStatus Status, MembershipRole Role) ForDeclaration(bool brokerageHasLiveMembers) =>
        brokerageHasLiveMembers
            ? (MembershipStatus.Pending, MembershipRole.Agent)
            : (MembershipStatus.Approved, MembershipRole.BrokerAdmin);

    /// <summary>Whether a status counts as a live membership (the partial unique index's set).</summary>
    public static bool IsLive(MembershipStatus status) =>
        status is MembershipStatus.Pending or MembershipStatus.Approved;

    /// <summary>
    /// Parses a broker admin's decision. Only approval and rejection are decisions: setting a
    /// request back to pending, or revoking one that was never granted, are not.
    /// </summary>
    public static bool TryParseDecision(string? value, out MembershipStatus decided)
    {
        switch (value)
        {
            case "approved":
                decided = MembershipStatus.Approved;
                return true;

            case "rejected":
                decided = MembershipStatus.Rejected;
                return true;

            default:
                decided = default;
                return false;
        }
    }

    /// <summary>Only a pending request can be decided; a decision is made once.</summary>
    public static bool CanBeDecided(MembershipStatus current) => current == MembershipStatus.Pending;
}
