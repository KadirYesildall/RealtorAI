using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>
/// The real API host, reachable over HTTP, with one substitution: the key that signs tokens.
///
/// <para>
/// <b>What is real.</b> The composition root (<c>Program</c>), every endpoint, every policy and
/// handler, and the database — requests run against the local Supabase Postgres exactly as they
/// would under <c>dotnet run</c>. A contract test that hand-built its own host could register
/// different endpoints or a different authorization pipeline than production, and would then be
/// verifying something nobody deploys.
/// </para>
/// <para>
/// <b>What is substituted, and why only that.</b> The production host fetches its signing keys
/// from Supabase's JWKS endpoint. Tests instead sign with a key generated per run, so they can
/// mint tokens for any persona — including expired ones and ones with the wrong audience, which
/// GoTrue will never issue on request. Everything else in token validation is left as
/// <c>SupabaseJwtSetup</c> configured it: issuer, audience, lifetime and signature are all still
/// checked, and <see cref="TestTokens"/> deliberately signs with the production issuer and
/// audience so those checks are exercised rather than relaxed. The JWKS path itself was
/// verified against the running stack in T034 (research R17).
/// </para>
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The issuer the production configuration validates against.</summary>
    internal const string Issuer = "http://127.0.0.1:54321/auth/v1";

    internal const string Audience = "authenticated";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Development supplies the verified local connection string and authority.
        builder.UseEnvironment("Development");

        // One background worker per test host. The suites run many hosts at once against one
        // local Postgres, and each worker holds a connection; at the development default of four,
        // parallel suites exhausted the server's connection slots (53300).
        builder.UseSetting("Pipeline:WorkerCount", "1");

        // Bounded pools for the same reason: two test processes, each with many hosts, plus the
        // Supabase services themselves, share the local server's 100 connection slots. Idle
        // pooled connections count against that limit; at Npgsql's default of 100 per pool the
        // suites exceeded it intermittently.
        builder.UseSetting(
            "ConnectionStrings:Postgres",
            "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Maximum Pool Size=15");
        builder.UseSetting(
            "ConnectionStrings:Hangfire",
            "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;SearchPath=hangfire;Maximum Pool Size=10");

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(TestTokens.SigningKey);

                    // Replaces only the key source. The handler would otherwise fetch the
                    // discovery document from the authority on the first request.
                    options.Configuration = configuration;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
        });
    }

    /// <summary>A client with no credentials.</summary>
    internal HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>A client authenticated as <paramref name="agentId"/>.</summary>
    internal HttpClient CreateClientFor(Guid agentId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.For(agentId));

        return client;
    }

    /// <summary>A client presenting an arbitrary, possibly invalid, token.</summary>
    internal HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }
}

/// <summary>
/// Mints Supabase-shaped access tokens signed with a per-run ES256 key — the same algorithm
/// the local stack's JWKS advertises.
/// </summary>
internal static class TestTokens
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal static SecurityKey SigningKey { get; } = new ECDsaSecurityKey(Key) { KeyId = "contract-tests" };

    /// <summary>A valid token for <paramref name="agentId"/>.</summary>
    internal static string For(Guid agentId) => Create(agentId);

    /// <summary>
    /// A token with overridable properties, for proving the validation the fixture claims to
    /// leave intact is in fact still applied.
    /// </summary>
    internal static string Create(
        Guid agentId,
        string issuer = ApiFactory.Issuer,
        string audience = ApiFactory.Audience,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var expiry = expires ?? now.AddMinutes(10);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,

            // An expired token must also have been issued before it expired, or the handler
            // rejects it for that reason instead of the one under test.
            IssuedAt = expiry < now ? expiry.AddMinutes(-10) : now,
            NotBefore = expiry < now ? expiry.AddMinutes(-10) : now,
            Expires = expiry,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", agentId.ToString()),
                new Claim("role", "authenticated"),
            ]),
            SigningCredentials = new SigningCredentials(
                signingKey ?? SigningKey,
                SecurityAlgorithms.EcdsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>A key the API has never seen, for forged-token tests.</summary>
    internal static SecurityKey CreateForeignKey() =>
        new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "foreign" };
}
