using System.Security.Claims;
using Api.Authentication;
using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Api.Authorization;

/// <summary>
/// Resolves <see cref="PlatformAdminRequirement"/> from the database (FR-064).
///
/// <para>
/// Reads <c>profiles.platform_admin</c> per request. Not from a token claim, and not from
/// configuration: the flag cannot be set through any API path, Server Action or user-facing
/// surface — it is granted by direct database access only — so the database is the only place
/// it exists, and reading it anywhere else would mean trusting a copy.
/// </para>
/// <para>
/// This role gates the AI model registry, which decides whether a model may run at all
/// (Principle VIII.4). That makes it the highest-value flag in the schema, which is why the
/// migration revokes UPDATE on the column and adds a trigger rejecting any change to it, and
/// why there is a test asserting an agent cannot grant it to themselves.
/// </para>
/// </summary>
internal sealed class PlatformAdminHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly AppDbContext context;

    public PlatformAdminHandler(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorizationContext,
        PlatformAdminRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(authorizationContext);

        if (authorizationContext.User.GetAgentId() is not { } agentId)
        {
            // No Fail() call: leaving the requirement unmet is enough, and an explicit Fail
            // would block any other handler that could legitimately satisfy it.
            return;
        }

        var isPlatformAdmin = await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.Id == agentId)
            .Select(profile => profile.PlatformAdmin)
            .SingleOrDefaultAsync();

        if (isPlatformAdmin)
        {
            authorizationContext.Succeed(requirement);
        }
    }
}

/// <summary>
/// Extension so <see cref="ClaimsPrincipal"/> consumers can read the agent id without taking a
/// dependency on the authentication namespace directly.
/// </summary>
internal static class PlatformAdminHandlerExtensions
{
    internal static IServiceCollection AddPlatformAdminPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuthorizationHandler, PlatformAdminHandler>();

        return services;
    }
}
