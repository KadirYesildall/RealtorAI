using Microsoft.AspNetCore.Authorization;

namespace Api.Authorization;

/// <summary>
/// T038 — the single place every endpoint's authorization posture is declared (FR-063).
///
/// <para>
/// <b>What this solves.</b> The usual failure mode for endpoint authorization is not a wrong
/// policy, it is a missing one: someone adds a route, forgets
/// <c>RequireAuthorization()</c>, and the endpoint is anonymous. Nothing fails, no test
/// notices, and the gap is invisible in review because the absence of a line is hard to see.
/// <c>EveryEndpointHasPolicyTests</c> closes that by enumerating the application's real
/// endpoints and failing when any of them is neither covered by a policy nor listed here as a
/// deliberate exception — so going anonymous becomes an explicit act with a written reason.
/// </para>
///
/// <para>
/// <b>What belongs here and what does not.</b> These policies answer questions that need only
/// the caller's identity: are they authenticated, are they a platform administrator. They
/// cannot answer "does this caller own listing X", because a policy does not see the route's
/// resource. Resource-scoped decisions therefore live in <see cref="IResourceAuthorizer"/> and
/// are made inside the handler against the actual row. Trying to express ownership as a policy
/// is how people end up reading an id from the route and trusting it.
/// </para>
/// </summary>
public static class PolicyRegistry
{
    /// <summary>
    /// Authenticated with a valid Supabase token. The baseline for anything touching an
    /// agent's own data; the resource check that follows is what narrows it to their rows.
    /// </summary>
    public const string Authenticated = "authenticated";

    /// <summary>
    /// <c>profiles.platform_admin</c> is set (FR-064). Required for the AI model registry and
    /// prompt versions, and for nothing else — it is a configuration role, not a data-reading
    /// one, so it deliberately grants no access to listing content.
    /// </summary>
    public const string PlatformAdmin = "platform-admin";

    /// <summary>
    /// Endpoints that are anonymous on purpose, each with the reason.
    ///
    /// <para>
    /// Keyed by endpoint name, so an endpoint must be explicitly named to be exempt. The test
    /// reads this, which makes every entry a decision someone wrote down rather than an
    /// oversight nobody saw.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> DeliberatelyAnonymousEndpoints { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Health"] =
                "Liveness probe. Must answer before any dependency is reachable and is polled "
                + "by infrastructure that holds no token. Returns a fixed literal and no "
                + "dependency state, so it discloses nothing — see Program.cs for why it does "
                + "not report Postgres health.",
        };

    /// <summary>
    /// Registers the identity-level policies.
    /// </summary>
    public static void AddEndpointPolicies(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser());

        // The platform-admin requirement is resolved from the database per request, never from
        // a token claim, for the same reason brokerage membership is (see SupabaseJwtSetup):
        // an hour-long token would outlive the revocation of an administrator.
        options.AddPolicy(PlatformAdmin, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new PlatformAdminRequirement());
        });

        // No FallbackPolicy is set. A fallback would make every endpoint implicitly
        // authenticated, which sounds safer but is worse here: it hides missing declarations
        // behind a default, and the coverage test could no longer tell a deliberate decision
        // from a forgotten one. Explicit declarations plus a test that enforces them gives a
        // stronger guarantee than a silent default.
    }
}

/// <summary>
/// Marker requirement satisfied by <see cref="PlatformAdminHandler"/>.
/// </summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;
