using AiEval;
using Modules.Content;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T158 — the content suite reads an MLS rule profile from the real schema and maps it as the host
/// does: verified means <c>verified_at</c> is set, limits are the profile's own, and the lookup
/// mapping parses. A unit test cannot show this, because the column names and types are the schema's.
/// </summary>
public sealed class EvalContentProfileTests : IAsyncLifetime
{
    private readonly string profileId = "TEST-EVAL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await SupabaseDatabase.ExecuteAsync(connection, null, "delete from public.mls_rule_profiles where id = $1", profileId);
    }

    private async Task InsertAsync(bool verified, int? publicLimit, int? privateLimit, string lookups)
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();
        await SupabaseDatabase.ExecuteAsync(
            connection,
            null,
            """
            insert into public.mls_rule_profiles
                (id, display_name, public_remarks_max_chars, private_remarks_max_chars, enumerated_field_lookups, verified_at)
            values ($1, 'Evaluation test profile', $2, $3, $4::jsonb, case when $5 then now() end)
            """,
            profileId,
            publicLimit,
            privateLimit,
            lookups,
            verified);
    }

    [Fact]
    public async Task AVerifiedProfile_IsReadWithItsLimitsAndLookups()
    {
        await InsertAsync(verified: true, 1499, 1000, """{"Flooring":{"only":["Carpet","Hardwood"]}}""");

        var profile = await ListingContentEval.LoadProfileAsync(SupabaseDatabase.ConnectionString, profileId, default);

        Assert.NotNull(profile);
        Assert.Equal(new ContentProfile(true, 1499, 1000), profile.Profile);
        Assert.Equal(["Carpet", "Hardwood"], profile.Lookups.Allowed("Flooring"));
        Assert.Null(ListingContentEval.WhyNotWritable(profileId, profile));
    }

    [Fact]
    public async Task AnUnverifiedProfile_IsNotWritable()
    {
        await InsertAsync(verified: false, 1499, 1000, "{}");

        var profile = await ListingContentEval.LoadProfileAsync(SupabaseDatabase.ConnectionString, profileId, default);

        Assert.NotNull(profile);
        Assert.False(profile.Profile.Verified);
        Assert.Contains("T160", ListingContentEval.WhyNotWritable(profileId, profile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownProfile_IsNull() =>
        Assert.Null(await ListingContentEval.LoadProfileAsync(SupabaseDatabase.ConnectionString, profileId, default));
}
