using Microsoft.Extensions.Caching.Memory;
using Modules.Brokerage;

namespace Api.Authorization;

/// <summary>
/// Owns the membership cache instance.
///
/// <para>
/// Deliberately a dedicated <see cref="MemoryCache"/> rather than the application-wide
/// <see cref="IMemoryCache"/>. Two reasons. First, isolation: authorization state should not be
/// evicted because something unrelated filled the shared cache, nor should it contribute to
/// pressure on it. Second, a size limit can be set here without forcing every other caller in
/// the application to start specifying entry sizes, which is what configuring
/// <c>SizeLimit</c> on the shared cache would require.
/// </para>
/// </summary>
internal sealed class MembershipCacheStore : IDisposable
{
    /// <summary>
    /// Entries are one small record list each. Ten thousand concurrent agents inside a
    /// ten-second window is far beyond expected load, and exceeding it costs a database query
    /// per eviction rather than a wrong answer.
    /// </summary>
    private const long MaxEntries = 10_000;

    internal IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions
    {
        SizeLimit = MaxEntries,
    });

    public void Dispose() => (Cache as IDisposable)?.Dispose();
}

internal static class AuthorizationSetup
{
    /// <summary>
    /// T035–T037 registration: membership resolution and resource authorization.
    /// </summary>
    internal static IServiceCollection AddResourceAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton: the cache must outlive a request or it would never serve a second one.
        services.AddSingleton<MembershipCacheStore>();

        // Scoped: both depend on the scoped AppDbContext.
        services.AddScoped<IMembershipStore, EfMembershipStore>();

        services.AddScoped<IMembershipResolver>(provider => new MembershipResolver(
            provider.GetRequiredService<IMembershipStore>(),
            provider.GetRequiredService<MembershipCacheStore>().Cache));

        services.AddScoped<IResourceAuthorizer, ResourceAuthorizer>();

        return services;
    }
}
