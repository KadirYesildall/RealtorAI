using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RealtorAI.Tests.Contract.Infrastructure;

namespace RealtorAI.Tests.Contract;

/// <summary>
/// T064 — <c>POST /api/profile/complete</c> (FR-058).
///
/// <para>
/// License number and state are what every listing is created under, and what an MLS submission
/// will later be made under, so the endpoint is strict about shape and writes only the caller's
/// own profile — the body carries no id to point it anywhere else.
/// </para>
/// </summary>
public sealed class ProfileCompleteTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/profile/complete";

    private readonly ApiFactory factory;

    public ProfileCompleteTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private static JsonObject Body(string licenseNumber = "DRE-01234567", string licenseState = "CA") => new()
    {
        ["licenseNumber"] = licenseNumber,
        ["licenseState"] = licenseState,
    };

    [Fact]
    public async Task Complete_Anonymous_Is401()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(Path, Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Complete_RecordsLicenseAndCompletionTime()
    {
        var agent = await TestAgents.CreateAsync("profile", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Path, Body("  DRE-01234567  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(agent, profile["id"]!.GetValue<Guid>());
        Assert.Equal("DRE-01234567", profile["licenseNumber"]!.GetValue<string>());
        Assert.Equal("CA", profile["licenseState"]!.GetValue<string>());
        Assert.NotNull(profile["profileCompletedAt"]);

        // The row is the evidence, not the response.
        Assert.Equal(
            "DRE-01234567",
            await TestDatabase.ScalarAsync<string>("select license_number from public.profiles where id = $1", agent));
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select profile_completed_at is not null from public.profiles where id = $1",
            agent));
    }

    /// <summary>
    /// Correcting a mistyped license is allowed, and does not move the moment the profile was
    /// first completed — that timestamp answers "when could this agent first create listings".
    /// </summary>
    [Fact]
    public async Task Complete_Again_UpdatesTheLicenseAndKeepsTheFirstCompletionTime()
    {
        var agent = await TestAgents.CreateAsync("profile-again", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var first = (await (await client.PostAsJsonAsync(Path, Body("DRE-11111111"))).Content.ReadFromJsonAsync<JsonObject>())!;
        var second = await client.PostAsJsonAsync(Path, Body("DRE-22222222", "NV"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var profile = (await second.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("DRE-22222222", profile["licenseNumber"]!.GetValue<string>());
        Assert.Equal("NV", profile["licenseState"]!.GetValue<string>());
        Assert.Equal(
            first["profileCompletedAt"]!.GetValue<DateTimeOffset>(),
            profile["profileCompletedAt"]!.GetValue<DateTimeOffset>());
    }

    [Theory]
    [InlineData("", "CA", "licenseNumber")]
    [InlineData("   ", "CA", "licenseNumber")]
    [InlineData("DRE-01234567", "", "licenseState")]
    [InlineData("DRE-01234567", "California", "licenseState")]
    [InlineData("DRE-01234567", "ca", "licenseState")]
    [InlineData("DRE-01234567", "C1", "licenseState")]
    public async Task Complete_WithAnInvalidField_Is422NamingIt(string licenseNumber, string licenseState, string field)
    {
        var agent = await TestAgents.CreateAsync("profile-invalid", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Path, Body(licenseNumber, licenseState));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]![field]);

        // Nothing was half-written.
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select profile_completed_at is null from public.profiles where id = $1",
            agent));
    }

    [Fact]
    public async Task Complete_WithALicenseNumberLongerThanAnyIssuer_Is422()
    {
        var agent = await TestAgents.CreateAsync("profile-long", completeProfile: false);
        using var client = factory.CreateClientFor(agent);

        var response = await client.PostAsJsonAsync(Path, Body(new string('9', 51)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>The body has no id, so a caller cannot complete — or overwrite — anyone else's profile.</summary>
    [Fact]
    public async Task Complete_WritesOnlyTheCallersProfile()
    {
        var caller = await TestAgents.CreateAsync("profile-caller", completeProfile: false);
        var bystander = await TestAgents.CreateAsync("profile-bystander", completeProfile: false);
        using var client = factory.CreateClientFor(caller);

        var body = Body();
        body["id"] = bystander;

        var response = await client.PostAsJsonAsync(Path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await TestDatabase.ScalarAsync<bool>(
            "select license_number is null and profile_completed_at is null from public.profiles where id = $1",
            bystander));
    }
}
