namespace Modules.Brokerage;

/// <summary>
/// The module's data port for membership rows. Implemented in the host, where the EF model
/// lives (<c>Api/Authorization/EfMembershipStore.cs</c>).
///
/// <para>
/// This indirection exists for one structural reason and earns one practical benefit. The
/// structural reason: the host references the modules, so a module cannot reference the host's
/// <c>AppDbContext</c> without creating a project cycle. The benefit: the caching and
/// invalidation logic in <see cref="MembershipResolver"/> can be tested against a fake store
/// that counts calls, which is the only way to actually prove the cache both caches and
/// expires rather than merely appearing to.
/// </para>
/// </summary>
public interface IMembershipStore
{
    /// <summary>
    /// Reads every membership row for one agent, or an empty list when the agent has declared
    /// no brokerage.
    ///
    /// <para>
    /// Returns all rows rather than one per brokerage because the common request pattern needs
    /// the whole set — authorizing a listing needs the membership for that listing's brokerage,
    /// and the agent's own capability checks need their own — and one round trip for the set
    /// beats several for its members.
    /// </para>
    /// <para>
    /// Implementations MUST read committed database state, not a cache. Caching is this
    /// module's responsibility and layering a second one underneath would make the expiry
    /// window unpredictable.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Membership>> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken);
}
