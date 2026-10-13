using Microsoft.Extensions.Caching.Memory;
using Modules.Brokerage;

namespace RealtorAI.Tests.Unit;

/// <summary>
/// T037 — membership resolution and its cache.
///
/// <para>
/// The point of these tests is that the cache measurably caches, measurably expires, and
/// measurably invalidates. A caching layer that silently did nothing would still pass every
/// functional test — all the right answers, just more queries — and a caching layer that never
/// expired would pass them too, right up until a revoked broker admin kept their access. So the
/// fake store counts calls, and the assertions are about those counts.
/// </para>
/// </summary>
public sealed class MembershipResolverTests
{
    private static readonly Guid AgentId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid OtherAgentId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid BrokerageId = Guid.Parse("11110000-0000-4000-8000-000000000001");
    private static readonly Guid OtherBrokerageId = Guid.Parse("22220000-0000-4000-8000-000000000002");

    /// <summary>
    /// Counts reads so the tests can assert on them, and lets a test change the answer
    /// mid-flight to prove a stale value is or is not served.
    /// </summary>
    private sealed class CountingStore : IMembershipStore
    {
        private readonly Dictionary<Guid, List<Membership>> rows = [];

        internal int ReadCount { get; private set; }

        internal void Set(Guid agentId, params Membership[] memberships) =>
            rows[agentId] = [.. memberships];

        public Task<IReadOnlyList<Membership>> GetForAgentAsync(
            Guid agentId,
            CancellationToken cancellationToken)
        {
            ReadCount++;

            return Task.FromResult<IReadOnlyList<Membership>>(
                rows.TryGetValue(agentId, out var found) ? found : []);
        }
    }

    private static (MembershipResolver Resolver, CountingStore Store) Build(TimeSpan? cacheDuration = null)
    {
        var store = new CountingStore();
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });

        var resolver = new MembershipResolver(
            store,
            cache,
            cacheDuration ?? TimeSpan.FromMinutes(5));

        return (resolver, store);
    }

    [Fact]
    public async Task GetMembership_ReturnsTheMatchingBrokerage()
    {
        var (resolver, store) = Build();

        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.BrokerAdmin),
            new Membership(AgentId, OtherBrokerageId, MembershipStatus.Pending, MembershipRole.Agent));

        var membership = await resolver.GetMembershipAsync(AgentId, BrokerageId, default);

        Assert.NotNull(membership);
        Assert.Equal(BrokerageId, membership.BrokerageId);
        Assert.True(membership.IsApprovedBrokerAdmin);
    }

    [Fact]
    public async Task GetMembership_ReturnsNullForABrokerageTheAgentIsNotIn()
    {
        var (resolver, store) = Build();

        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.Agent));

        Assert.Null(await resolver.GetMembershipAsync(AgentId, OtherBrokerageId, default));
    }

    [Fact]
    public async Task GetMembership_ReturnsNullWhenTheAgentHasDeclaredNoBrokerage()
    {
        var (resolver, _) = Build();

        Assert.Null(await resolver.GetMembershipAsync(AgentId, BrokerageId, default));
    }

    /// <summary>
    /// The reason the cache exists: one request authorizes a listing, its fields, its photos,
    /// its drafts and its findings, and each check needs the same membership row.
    /// </summary>
    [Fact]
    public async Task RepeatedLookups_HitTheDatabaseOnce()
    {
        var (resolver, store) = Build();

        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.Agent));

        for (var i = 0; i < 10; i++)
        {
            await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        }

        Assert.Equal(1, store.ReadCount);
    }

    /// <summary>Each agent is cached separately; one agent's entry must not answer for another.</summary>
    [Fact]
    public async Task CacheIsKeyedByAgent()
    {
        var (resolver, store) = Build();

        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.BrokerAdmin));
        store.Set(
            OtherAgentId,
            new Membership(OtherAgentId, BrokerageId, MembershipStatus.Pending, MembershipRole.Agent));

        var first = await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        var second = await resolver.GetMembershipAsync(OtherAgentId, BrokerageId, default);

        Assert.True(first!.IsApproved);
        Assert.False(second!.IsApproved);
        Assert.Equal(2, store.ReadCount);
    }

    /// <summary>
    /// FR-060 and FR-050 require a membership decision to take effect immediately, not after a
    /// cache window. This is the mechanism that delivers that, so it is tested directly: a
    /// revocation followed by invalidation must be visible on the very next lookup.
    /// </summary>
    [Fact]
    public async Task Invalidate_MakesTheNextLookupSeeTheNewState()
    {
        var (resolver, store) = Build(TimeSpan.FromMinutes(10));

        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.BrokerAdmin));

        var before = await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        Assert.True(before!.IsApprovedBrokerAdmin);

        // The admin is revoked by another part of the system, which then invalidates.
        store.Set(
            AgentId,
            new Membership(AgentId, BrokerageId, MembershipStatus.Revoked, MembershipRole.BrokerAdmin));

        resolver.Invalidate(AgentId);

        var after = await resolver.GetMembershipAsync(AgentId, BrokerageId, default);

        Assert.False(after!.IsApprovedBrokerAdmin);
        Assert.Equal(2, store.ReadCount);
    }

    /// <summary>
    /// Invalidating one agent must not flush every other agent's entry — otherwise a busy
    /// brokerage processing membership decisions would effectively run with no cache.
    /// </summary>
    [Fact]
    public async Task Invalidate_AffectsOnlyThatAgent()
    {
        var (resolver, store) = Build(TimeSpan.FromMinutes(10));

        store.Set(AgentId, new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.Agent));
        store.Set(OtherAgentId, new Membership(OtherAgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.Agent));

        await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        await resolver.GetMembershipAsync(OtherAgentId, BrokerageId, default);
        Assert.Equal(2, store.ReadCount);

        resolver.Invalidate(AgentId);

        await resolver.GetMembershipAsync(AgentId, BrokerageId, default);      // re-reads
        await resolver.GetMembershipAsync(OtherAgentId, BrokerageId, default); // still cached

        Assert.Equal(3, store.ReadCount);
    }

    /// <summary>
    /// The expiry backstop, for changes made somewhere this process cannot invalidate from —
    /// another instance, or a direct database edit. Driven with a tiny window so the test does
    /// not sit for the production duration.
    /// </summary>
    [Fact]
    public async Task CacheExpires_SoChangesMadeElsewhereAreEventuallySeen()
    {
        var (resolver, store) = Build(TimeSpan.FromMilliseconds(50));

        store.Set(AgentId, new Membership(AgentId, BrokerageId, MembershipStatus.Approved, MembershipRole.Agent));

        await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        Assert.Equal(1, store.ReadCount);

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        await resolver.GetMembershipAsync(AgentId, BrokerageId, default);
        Assert.Equal(2, store.ReadCount);
    }

    /// <summary>
    /// The production window is deliberately seconds, not minutes. If someone widens it, this
    /// fails and the trade-off gets looked at again — a long window is how membership
    /// revocation quietly stops being immediate.
    /// </summary>
    [Fact]
    public void DefaultCacheDuration_StaysShort()
    {
        Assert.InRange(
            MembershipResolver.DefaultCacheDuration,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCacheDuration()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MembershipResolver(new CountingStore(), cache, TimeSpan.Zero));
    }
}
