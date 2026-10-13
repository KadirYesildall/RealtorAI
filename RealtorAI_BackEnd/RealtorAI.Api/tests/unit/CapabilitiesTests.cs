using Modules.Brokerage;

namespace RealtorAI.Tests.Unit;

/// <summary>
/// T036 — capability predicates (FR-061).
///
/// <para>
/// FR-061 splits the feature in a specific place: a pending agent may do all the private
/// preparation work, but not the two things that publish the brokerage's identity. These tests
/// cover every status against every capability, because the interesting failure here is not a
/// wrong answer on the obvious case — it is a predicate that accidentally treats
/// <c>revoked</c> like <c>approved</c>, or lets a role stand in for a status.
/// </para>
/// </summary>
public sealed class CapabilitiesTests
{
    private static Membership Membership(MembershipStatus status, MembershipRole role = MembershipRole.Agent) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, role);

    public static TheoryData<MembershipStatus> AllStatuses { get; } = new()
    {
        MembershipStatus.Pending,
        MembershipStatus.Approved,
        MembershipStatus.Rejected,
        MembershipStatus.Revoked,
    };

    // ───────────────────────── Marketing and MLS: approved only ─────────────────────────

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void CanApproveMarketing_OnlyWhenApproved(MembershipStatus status)
    {
        var expected = status == MembershipStatus.Approved;

        Assert.Equal(expected, Capabilities.CanApproveMarketing(Membership(status)));
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void CanSubmitToMls_OnlyWhenApproved(MembershipStatus status)
    {
        var expected = status == MembershipStatus.Approved;

        Assert.Equal(expected, Capabilities.CanSubmitToMls(Membership(status)));
    }

    /// <summary>
    /// A revoked membership must not retain the capabilities it once had. Worth its own test
    /// because <c>revoked</c> is the status a predicate written as "not rejected" would wrongly
    /// admit.
    /// </summary>
    [Fact]
    public void RevokedMembership_LosesMarketingAndMls()
    {
        var revoked = Membership(MembershipStatus.Revoked, MembershipRole.BrokerAdmin);

        Assert.False(Capabilities.CanApproveMarketing(revoked));
        Assert.False(Capabilities.CanSubmitToMls(revoked));
        Assert.False(Capabilities.CanDecideMemberships(revoked));
        Assert.False(Capabilities.CanReadBrokerageData(revoked));
    }

    /// <summary>
    /// Having no brokerage at all must not be a way around a rule that exists to protect a
    /// brokerage's identity.
    /// </summary>
    [Fact]
    public void NoMembership_GrantsNoPublishingCapability()
    {
        Assert.False(Capabilities.CanApproveMarketing(null));
        Assert.False(Capabilities.CanSubmitToMls(null));
        Assert.False(Capabilities.CanDecideMemberships(null));
        Assert.False(Capabilities.CanReadBrokerageData(null));
    }

    /// <summary>
    /// The role is not a substitute for the status. An unapproved broker admin is what the
    /// first-registrant rule in FR-062 creates before anyone has confirmed them, so this is a
    /// state that genuinely occurs rather than a contrived one.
    /// </summary>
    [Theory]
    [InlineData(MembershipStatus.Pending)]
    [InlineData(MembershipStatus.Rejected)]
    [InlineData(MembershipStatus.Revoked)]
    public void UnapprovedBrokerAdmin_HasNoAdminCapability(MembershipStatus status)
    {
        var admin = Membership(status, MembershipRole.BrokerAdmin);

        Assert.False(Capabilities.CanDecideMemberships(admin));
        Assert.False(Capabilities.CanReadBrokerageData(admin));
    }

    [Fact]
    public void ApprovedBrokerAdmin_HasAdminCapability()
    {
        var admin = Membership(MembershipStatus.Approved, MembershipRole.BrokerAdmin);

        Assert.True(Capabilities.CanDecideMemberships(admin));
        Assert.True(Capabilities.CanReadBrokerageData(admin));
    }

    /// <summary>An approved ordinary agent is not an admin.</summary>
    [Fact]
    public void ApprovedAgent_IsNotAnAdmin()
    {
        var agent = Membership(MembershipStatus.Approved, MembershipRole.Agent);

        Assert.False(Capabilities.CanDecideMemberships(agent));
        Assert.False(Capabilities.CanReadBrokerageData(agent));

        // ...but retains the publishing capabilities, which depend on status alone.
        Assert.True(Capabilities.CanApproveMarketing(agent));
        Assert.True(Capabilities.CanSubmitToMls(agent));
    }

    // ───────────────────────── Preparation: permissive by design ─────────────────────────

    /// <summary>
    /// The permissive half of FR-061. A pending agent can draft, analyze, generate and
    /// compliance-check — gating this would put the whole product behind someone else's
    /// availability, and nothing in it leaves the agent's own account.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void CanPrepareListing_RegardlessOfStatus(MembershipStatus status)
    {
        Assert.True(Capabilities.CanPrepareListing(Membership(status)));
    }

    [Fact]
    public void CanPrepareListing_WithNoBrokerageAtAll()
    {
        Assert.True(Capabilities.CanPrepareListing(null));
    }

    /// <summary>
    /// Guards the enum itself. If a status is added to <see cref="MembershipStatus"/> without
    /// being considered here, this fails — which is the point: a new status silently inheriting
    /// whichever branch a predicate happens to fall into is how an authorization gap appears.
    /// </summary>
    [Fact]
    public void AllStatuses_AreCovered()
    {
        var declared = Enum.GetValues<MembershipStatus>().Length;

        Assert.Equal(declared, AllStatuses.Count);
    }
}
