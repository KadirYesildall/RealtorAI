using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using RealtorAI.Tests.Contract.Infrastructure;
using static RealtorAI.Tests.Contract.Infrastructure.ComplianceTesting;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T125 — a blocking finding cannot be acknowledged at the API or in the database (FR-022,
/// NON-NEGOTIABLE).
///
/// <para>
/// Two layers, because either alone has a way round it: an API bug, or a direct write that
/// skips the API. The database half runs as the most privileged role there is — the check
/// constraint is a property of the data, so not even the owner can write the forbidden state.
/// </para>
/// </summary>
public sealed class BlockingFindingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public BlockingFindingTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ABlockingFinding_CannotBeAcknowledged_ThroughTheApi_OrInTheDatabase()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "blocking", ViolatingRemarks);
        var finding = IdOf(Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families"));

        using (var client = factory.CreateClientFor(agent))
        {
            var response = await client.PostAsJsonAsync(Acknowledge(listing, finding), new JsonObject { ["reason"] = "I have checked this carefully." });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.ExecuteAsync(
            """
            update public.compliance_findings
            set status = 'acknowledged', acknowledged_reason = 'Bypassing the API.', acknowledged_by = $2,
                acknowledged_at = timezone('utc', now())
            where id = $1
            """,
            finding,
            agent));

        Assert.Equal("findings_acknowledge_warning_only", exception.ConstraintName);
        Assert.Equal("open", await TestDatabase.ScalarAsync<string>("select status::text from public.compliance_findings where id = $1", finding));
    }

    /// <summary>Nor can its severity be lowered to make it acknowledgeable: no route changes severity.</summary>
    [Fact]
    public async Task NoEndpoint_ChangesAFindingsSeverity()
    {
        var (agent, listing, _) = await ListingWithRemarksAsync(factory, "blocking-severity", ViolatingRemarks);
        var finding = IdOf(Rule(await CheckAsync(factory, agent, listing), "fair_housing.familial_status.perfect_for_families"));
        using var client = factory.CreateClientFor(agent);

        foreach (var method in new[] { HttpMethod.Patch, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, $"{Compliance(listing)}/{finding}")
            {
                Content = JsonContent.Create(new JsonObject { ["severity"] = "warning" }),
            };

            Assert.True((await client.SendAsync(request)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        }

        Assert.Equal("blocking", await TestDatabase.ScalarAsync<string>("select severity::text from public.compliance_findings where id = $1", finding));
    }
}
