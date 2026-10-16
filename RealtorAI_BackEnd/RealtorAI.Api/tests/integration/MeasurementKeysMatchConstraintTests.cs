using System.Text.RegularExpressions;
using Modules.Listings;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T074 — the API's FR-007 key list and the database's are one list.
///
/// <para>
/// <see cref="ListingFieldService.MeasurementAndLegalKeys"/> lets the API answer an AI-provenanced
/// measurement with a 422 naming the field; <c>listing_fields_no_ai_measurements</c> refuses it
/// regardless. If a migration protected a new key and the API's list lagged, the database would
/// still refuse — but the agent would get a constraint error instead of a reason. This reads the
/// constraint as installed and compares.
/// </para>
/// </summary>
public sealed partial class MeasurementKeysMatchConstraintTests
{
    [Fact]
    public async Task TheApisProtectedKeys_AreExactlyTheConstraintsKeys()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var definition = await SupabaseDatabase.ScalarAsync<string>(
            connection,
            null,
            """
            select pg_get_constraintdef(oid) from pg_constraint
            where conname = 'listing_fields_no_ai_measurements'
            """);

        Assert.NotNull(definition);

        // The key list is the NOT IN (...) array; the provenance list follows it.
        var keyList = definition![..definition.IndexOf("provenance", StringComparison.Ordinal)];
        var keys = QuotedLiteral().Matches(keyList).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            ListingFieldService.MeasurementAndLegalKeys.Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex("'([A-Za-z]+)'")]
    private static partial Regex QuotedLiteral();
}
