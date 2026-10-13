using Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T038 — no endpoint may be anonymous by accident (FR-063).
///
/// <para>
/// This is the test the policy registry exists for. Forgetting
/// <c>RequireAuthorization()</c> on a new route produces a working, anonymous endpoint: no
/// compiler error, no runtime error, no failing test, and nothing visible in a diff except a
/// line that is not there. On endpoints serving listing data and audit trails, that is the
/// highest-consequence mistake available for the smallest possible slip.
/// </para>
/// <para>
/// Rather than reviewing for it, this enumerates the application's real endpoints from
/// <see cref="EndpointDataSource"/> — the routing table the server will actually serve, not a
/// list someone maintains — and requires each to be either covered by an authorization policy
/// or named in <see cref="PolicyRegistry.DeliberatelyAnonymousEndpoints"/> with a reason.
/// </para>
/// </summary>
public sealed class EveryEndpointHasPolicyTests : IClassFixture<EveryEndpointHasPolicyTests.Factory>
{
    private readonly Factory factory;

    public EveryEndpointHasPolicyTests(Factory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// Boots the real composition root. Using the actual <c>Program</c> matters: a hand-built
    /// test host could register different endpoints than production, and then this test would
    /// be checking something nobody deploys.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Development supplies the verified local Supabase authority and connection
            // string from appsettings.Development.json. Nothing here opens a connection —
            // only the routing table is inspected — but the host must still build.
            builder.UseEnvironment("Development");
        }
    }

    private List<RouteEndpoint> RouteEndpoints()
    {
        using var scope = factory.Services.CreateScope();

        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        return dataSource.Endpoints.OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void TheApplicationExposesEndpoints()
    {
        // Guards the test itself. If the host ever started registering no endpoints, every
        // assertion below would pass vacuously and the suite would report a guarantee it was
        // no longer checking.
        Assert.NotEmpty(RouteEndpoints());
    }

    [Fact]
    public void EveryEndpointIsEitherAuthorizedOrDeliberatelyAnonymous()
    {
        var problems = new List<string>();

        foreach (var endpoint in RouteEndpoints())
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                ?? endpoint.DisplayName
                ?? endpoint.RoutePattern.RawText
                ?? "<unnamed>";

            var hasAuthorizeData = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var isDeclaredAnonymous =
                PolicyRegistry.DeliberatelyAnonymousEndpoints.ContainsKey(name);

            if (isDeclaredAnonymous)
            {
                // A declared exception must actually be anonymous. If someone later adds
                // authorization to it, the stale entry should be removed rather than left to
                // imply the endpoint is open when it is not.
                if (!allowsAnonymous)
                {
                    problems.Add(
                        $"'{name}' is listed in DeliberatelyAnonymousEndpoints but does not "
                        + "allow anonymous access. Remove the entry.");
                }

                continue;
            }

            if (allowsAnonymous)
            {
                problems.Add(
                    $"'{name}' ({endpoint.RoutePattern.RawText}) allows anonymous access but is "
                    + "not declared in PolicyRegistry.DeliberatelyAnonymousEndpoints. If that "
                    + "is intended, add it there with the reason; otherwise add "
                    + "RequireAuthorization.");
                continue;
            }

            if (!hasAuthorizeData)
            {
                problems.Add(
                    $"'{name}' ({endpoint.RoutePattern.RawText}) has no authorization policy. "
                    + $"Add .RequireAuthorization(PolicyRegistry.{nameof(PolicyRegistry.Authenticated)}) "
                    + "or another policy — FR-063 requires every endpoint to authorize "
                    + "independently, because the API connects with a role that bypasses RLS.");
            }
        }

        Assert.True(
            problems.Count == 0,
            "Endpoints without an explicit authorization posture:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Every declared exception must correspond to an endpoint that exists. A stale entry is a
    /// standing permission for a future endpoint to be born anonymous under a name someone
    /// already justified.
    /// </summary>
    [Fact]
    public void EveryDeclaredAnonymousEndpointStillExists()
    {
        var names = RouteEndpoints()
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        var stale = PolicyRegistry.DeliberatelyAnonymousEndpoints.Keys
            .Where(name => !names.Contains(name))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These endpoints are declared anonymous but no longer exist: "
            + string.Join(", ", stale)
            + ". Remove them, or a future endpoint reusing the name inherits the exemption.");
    }

    /// <summary>
    /// Each exception carries a real explanation. An empty or token reason defeats the purpose
    /// of requiring one.
    /// </summary>
    [Fact]
    public void EveryAnonymousExceptionHasASubstantiveReason()
    {
        foreach (var (name, reason) in PolicyRegistry.DeliberatelyAnonymousEndpoints)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(reason),
                $"'{name}' is exempt from authorization with no stated reason.");

            Assert.True(
                reason.Length >= 40,
                $"The reason given for '{name}' being anonymous is too short to be an "
                + $"explanation: '{reason}'");
        }
    }
}
