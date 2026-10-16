using Modules.Brokerage;

namespace RealtorAI.Tests.Unit;

/// <summary>T062 — membership lifecycle rules (FR-060, FR-062).</summary>
public sealed class MembershipRulesTests
{
    [Fact]
    public void TheFirstRegistrant_BecomesAnApprovedBrokerAdmin()
    {
        Assert.Equal(
            (MembershipStatus.Approved, MembershipRole.BrokerAdmin),
            MembershipRules.ForDeclaration(brokerageHasLiveMembers: false));
    }

    [Fact]
    public void AnyoneJoiningAnOccupiedBrokerage_StartsAsAPendingAgent()
    {
        Assert.Equal(
            (MembershipStatus.Pending, MembershipRole.Agent),
            MembershipRules.ForDeclaration(brokerageHasLiveMembers: true));
    }

    [Theory]
    [InlineData(MembershipStatus.Pending, true)]
    [InlineData(MembershipStatus.Approved, true)]
    [InlineData(MembershipStatus.Rejected, false)]
    [InlineData(MembershipStatus.Revoked, false)]
    public void Liveness_MatchesTheUniqueIndex(MembershipStatus status, bool live)
    {
        Assert.Equal(live, MembershipRules.IsLive(status));
    }

    [Theory]
    [InlineData("approved", MembershipStatus.Approved)]
    [InlineData("rejected", MembershipStatus.Rejected)]
    public void ApprovalAndRejection_AreDecisions(string value, MembershipStatus expected)
    {
        Assert.True(MembershipRules.TryParseDecision(value, out var decided));
        Assert.Equal(expected, decided);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("revoked")]
    [InlineData("Approved")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNotADecision(string? value)
    {
        Assert.False(MembershipRules.TryParseDecision(value, out _));
    }

    [Theory]
    [InlineData(MembershipStatus.Pending, true)]
    [InlineData(MembershipStatus.Approved, false)]
    [InlineData(MembershipStatus.Rejected, false)]
    [InlineData(MembershipStatus.Revoked, false)]
    public void OnlyAPendingRequest_CanBeDecided(MembershipStatus status, bool decidable)
    {
        Assert.Equal(decidable, MembershipRules.CanBeDecided(status));
    }
}
