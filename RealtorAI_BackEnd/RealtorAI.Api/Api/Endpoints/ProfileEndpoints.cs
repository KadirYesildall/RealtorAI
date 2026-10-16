using System.Security.Claims;
using System.Text.RegularExpressions;
using Api.Authentication;
using Api.Authorization;
using Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

internal sealed record CompleteProfileRequest(string? LicenseNumber, string? LicenseState);

internal sealed record ProfileDto(Guid Id, string? LicenseNumber, string? LicenseState, DateTimeOffset? ProfileCompletedAt);

/// <summary>
/// T079's backend — <c>POST /api/profile/complete</c> (FR-058).
///
/// <para>
/// The caller's own profile only: the profile id is the token's subject, and the body carries
/// nothing that could point the write anywhere else. The write is one statement that stamps
/// <c>profile_completed_at</c> with the database clock the first time and keeps it afterwards,
/// so correcting a mistyped license does not move the moment the agent could first create
/// listings.
/// </para>
/// </summary>
internal static partial class ProfileEndpoints
{
    /// <summary>License numbers vary by state; none approaches this.</summary>
    private const int MaxLicenseLength = 50;

    internal static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/profile/complete", CompleteAsync)
            .RequireAuthorization(PolicyRegistry.Authenticated)
            .WithTags("Brokerage")
            .WithName("CompleteProfile");

        return app;
    }

    private static async Task<Results<Ok<ProfileDto>, ProblemHttpResult, UnauthorizedHttpResult>> CompleteAsync(
        CompleteProfileRequest request,
        ClaimsPrincipal user,
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        if (user.GetAgentId() is not { } agentId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var license = request.LicenseNumber?.Trim();
        var state = request.LicenseState?.Trim();

        if (string.IsNullOrEmpty(license))
        {
            errors["licenseNumber"] = ["Enter your real estate license number."];
        }
        else if (license.Length > MaxLicenseLength)
        {
            errors["licenseNumber"] = [$"A license number is at most {MaxLicenseLength} characters."];
        }

        if (state is null || !StateCode().IsMatch(state))
        {
            errors["licenseState"] = ["Use the two-letter code of the state that issued your license, e.g. CA."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var updated = await context.Database.ExecuteSqlAsync(
            $"""
            update public.profiles
            set license_number = {license},
                license_state = {state},
                profile_completed_at = coalesce(profile_completed_at, timezone('utc', now())),
                updated_at = timezone('utc', now())
            where id = {agentId}
            """,
            cancellationToken);

        if (updated == 0)
        {
            // The token is valid but its subject has no profile — the account was removed.
            return TypedResults.Unauthorized();
        }

        var profile = await context.Profiles
            .AsNoTracking()
            .Where(candidate => candidate.Id == agentId)
            .Select(candidate => new ProfileDto(
                candidate.Id,
                candidate.LicenseNumber,
                candidate.LicenseState,
                candidate.ProfileCompletedAt))
            .SingleAsync(cancellationToken);

        return TypedResults.Ok(profile with { LicenseState = profile.LicenseState?.Trim() });
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex StateCode();
}
