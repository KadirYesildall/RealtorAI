using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Api.Authentication;

/// <summary>
/// T034 — Supabase JWT validation.
///
/// Verified against the running local stack rather than assumed (research.md R17):
/// OIDC discovery is served at <c>{authority}/.well-known/openid-configuration</c>, reporting
/// issuer <c>http://127.0.0.1:54321/auth/v1</c> and a JWKS containing an ES256 (EC P-256)
/// signing key. Asymmetric signing is therefore already the default in this Supabase CLI
/// version — the commented-out <c>signing_keys_path</c> in config.toml does not need enabling.
///
/// <para>
/// <b>Only the <c>sub</c> claim is trusted.</b> Brokerage membership status and role are
/// deliberately NOT read from the token: <c>jwt_expiry</c> is 3600s, so a token can outlive a
/// membership decision by up to an hour. A revoked or rejected membership must take effect
/// immediately (FR-050, FR-060), so authorization state is resolved from the database per
/// request. Trusting a claim here would mean a revoked broker admin kept reading an agent's
/// audit trail until their token happened to refresh.
/// </para>
/// </summary>
internal static class SupabaseJwtSetup
{
    /// <summary>
    /// Supabase issues access tokens with the audience <c>authenticated</c>.
    /// </summary>
    private const string DefaultAudience = "authenticated";

    internal static IServiceCollection AddSupabaseJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Fail fast and loudly: a missing authority must not degrade to "no authentication".
        var authority = configuration["Supabase:Authority"]
            ?? throw new InvalidOperationException(
                "Supabase:Authority is not configured. Take the value from the running "
                + "stack's OIDC metadata rather than assuming it — see "
                + "specs/002-ai-listing-launch/quickstart.md. Local default: "
                + "http://127.0.0.1:54321/auth/v1");

        var audience = configuration["Supabase:Audience"] ?? DefaultAudience;

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;

                // The local stack serves plain HTTP. Anything else must use HTTPS, so this is
                // keyed off the environment rather than left configurable — a production
                // deployment cannot accidentally fetch signing keys over cleartext.
                options.RequireHttpsMetadata = !environment.IsDevelopment();

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypes.NameIdentifier,
                };
            });

        return services;
    }

    /// <summary>
    /// Resolves the authenticated agent's profile id from the token's <c>sub</c> claim.
    /// Returns <see langword="null"/> when absent or malformed, so callers must handle the
    /// unauthenticated case explicitly instead of receiving a default-valued Guid.
    /// </summary>
    internal static Guid? GetAgentId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        return Guid.TryParse(subject, out var id) ? id : null;
    }
}
