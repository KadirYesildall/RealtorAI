using System.Net;
using Api.Data;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// Regression test for research R26: the host failed every database-backed request after the
/// twentieth.
///
/// <para>
/// The enum-label translator was constructed afresh each time the per-request
/// <c>DbContextOptions</c> were built, EF saw each request's options as a new configuration, and
/// after twenty internal service providers it raised <c>ManyServiceProvidersCreatedWarning</c>
/// as an error. No test caught it earlier because nothing had yet sent more than a few database
/// requests through one host. The count here is comfortably past the threshold.
/// </para>
/// </summary>
public sealed class SustainedDatabaseRequestsTests : IClassFixture<ApiFactory>
{
    private const int RequestCount = 30;

    private readonly ApiFactory factory;

    public SustainedDatabaseRequestsTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task TheHostServesMoreThanTwentyDatabaseRequests()
    {
        using var client = factory.CreateClientFor(Seed.PlatformAdmin);

        for (var request = 1; request <= RequestCount; request++)
        {
            // The platform-admin policy and the handler both query the database.
            var response = await client.GetAsync("/api/admin/ai/registry");

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Request {request} of {RequestCount} returned {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// The mechanism itself: building options twice must hand EF the same translator, or the two
    /// option sets are not equal and each costs a new internal service provider.
    /// </summary>
    [Fact]
    public void EnumTranslators_AreStableAcrossCalls()
    {
        foreach (var mapping in PostgresEnumMappings.All)
        {
            Assert.Same(mapping.Translator(), mapping.Translator());
        }
    }
}
