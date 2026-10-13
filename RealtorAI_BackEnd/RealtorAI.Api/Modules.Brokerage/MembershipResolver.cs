using Microsoft.Extensions.Caching.Memory;

namespace Modules.Brokerage;

/// <summary>Resolves the current caller's brokerage membership state.</summary>
public interface IMembershipResolver
{
    /// <summary>
    /// The agent's membership in one specific brokerage, or null when they have none there.
    /// </summary>
    Task<Membership?> GetMembershipAsync(
        Guid agentId,
        Guid brokerageId,
        CancellationToken cancellationToken);

    /// <summary>All of the agent's memberships.</summary>
    Task<IReadOnlyList<Membership>> GetMembershipsAsync(
        Guid agentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached memberships for one agent. Called by the code that writes a membership
    /// decision, so an approval or revocation takes effect on the agent's very next request
    /// rather than after the cache window.
    /// </summary>
    void Invalidate(Guid agentId);
}

/// <summary>
/// T037 — per-request membership resolution from the database, with a short invalidating cache.
///
/// <para>
/// <b>Never reads membership from a token claim.</b> Supabase access tokens live for an hour
/// (<c>jwt_expiry = 3600</c>). If status or role were carried in the JWT, revoking a broker
/// admin would leave them reading their agents' listings and audit trail for up to an hour
/// after the revocation — the exact situation FR-050 and FR-060 exist to prevent. So the
/// database is consulted, and the token supplies only <c>sub</c>.
/// </para>
///
/// <para>
/// <b>Why cache at all, and why so briefly.</b> A single request can authorize many resources —
/// a listing, its fields, its photos, its drafts, its findings — and each check needs the same
/// membership row. Without a cache that is one query per check. With an unbounded cache it is
/// a stale authorization decision. The window is therefore deliberately short
/// (<see cref="DefaultCacheDuration"/>) and, more importantly, <b>explicitly invalidated</b>
/// by the code that changes a membership, so the expiry is a backstop for changes made
/// elsewhere (another instance, a direct database edit) rather than the primary mechanism.
/// </para>
///
/// <para>
/// The residual exposure is bounded and stated plainly: a membership changed by another process
/// may be honoured for up to the cache duration. That is acceptable for a window measured in
/// seconds, and it is not acceptable for an hour, which is why the token was ruled out.
/// </para>
/// </summary>
public sealed class MembershipResolver : IMembershipResolver
{
    /// <summary>
    /// Short enough that a change made elsewhere is picked up within seconds, long enough to
    /// collapse the repeated lookups inside one request. Not configurable: a deployment that
    /// raised this would weaken an authorization guarantee through a config file, which is
    /// exactly the kind of change that should require a code review.
    /// </summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(10);

    private const string CacheKeyPrefix = "brokerage:memberships:";

    private readonly IMembershipStore store;
    private readonly IMemoryCache cache;
    private readonly TimeSpan cacheDuration;

    public MembershipResolver(IMembershipStore store, IMemoryCache cache)
        : this(store, cache, DefaultCacheDuration)
    {
    }

    /// <summary>
    /// Duration-injecting constructor, used by tests to exercise expiry without sleeping for
    /// the production window.
    /// </summary>
    internal MembershipResolver(IMembershipStore store, IMemoryCache cache, TimeSpan cacheDuration)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);

        if (cacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheDuration),
                cacheDuration,
                "Cache duration must be positive. To disable caching, resolve without this type.");
        }

        this.store = store;
        this.cache = cache;
        this.cacheDuration = cacheDuration;
    }

    public async Task<Membership?> GetMembershipAsync(
        Guid agentId,
        Guid brokerageId,
        CancellationToken cancellationToken)
    {
        var memberships = await GetMembershipsAsync(agentId, cancellationToken);

        return memberships.SingleOrDefault(membership => membership.BrokerageId == brokerageId);
    }

    public async Task<IReadOnlyList<Membership>> GetMembershipsAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var key = CacheKeyPrefix + agentId;

        if (cache.TryGetValue(key, out IReadOnlyList<Membership>? cached) && cached is not null)
        {
            return cached;
        }

        var memberships = await store.GetForAgentAsync(agentId, cancellationToken);

        // Absolute expiry, not sliding. A sliding window on a busy account would keep a stale
        // membership alive indefinitely, which defeats the whole point of a short cache.
        cache.Set(key, memberships, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = cacheDuration,

            // Bound the memory an unbounded agent population could consume; authorization
            // state is cheap to recompute, so eviction costs a query rather than correctness.
            Size = 1,
        });

        return memberships;
    }

    public void Invalidate(Guid agentId) => cache.Remove(CacheKeyPrefix + agentId);
}
