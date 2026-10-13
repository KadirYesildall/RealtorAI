using Api.Concurrency;

namespace RealtorAI.Tests.Unit;

/// <summary>
/// T041 — <c>If-Match</c> parsing (FR-066).
///
/// <para>
/// The rejection cases carry most of the weight here. Each one is a way a lenient parser would
/// quietly turn a conditional update into an unconditional one, which is the exact failure the
/// header exists to prevent — so "it parsed something" is not the property under test;
/// "it refuses anything that is not a specific version" is.
/// </para>
/// </summary>
public sealed class IfMatchTests
{
    [Theory]
    [InlineData("\"0\"", 0)]
    [InlineData("\"1\"", 1)]
    [InlineData("\"7\"", 7)]
    [InlineData("\"2147483647\"", int.MaxValue)]
    [InlineData("  \"42\"  ", 42)]
    public void TryParse_AcceptsAStrongQuotedInteger(string header, int expected)
    {
        var parsed = IfMatch.TryParse(header, out var rowVersion, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expected, rowVersion);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsAMissingHeader(string? header)
    {
        var parsed = IfMatch.TryParse(header, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }

    /// <summary>
    /// <c>*</c> means "any current representation". Accepting it would make the update
    /// unconditional and reintroduce the lost-update problem under a header that looks like it
    /// is preventing exactly that.
    /// </summary>
    [Fact]
    public void TryParse_RejectsWildcard()
    {
        var parsed = IfMatch.TryParse("*", out _, out var error);

        Assert.False(parsed);
        Assert.Contains("unconditional", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RFC 9110 forbids weak comparison for <c>If-Match</c>, and for a concurrency check
    /// "roughly this version" has no meaning.
    /// </summary>
    [Theory]
    [InlineData("W/\"7\"")]
    [InlineData("w/\"7\"")]
    public void TryParse_RejectsAWeakTag(string header)
    {
        // Lower-case w is checked separately: it is not a valid weak tag per the spec either,
        // so it must fail — but it must fail as a malformed tag rather than be silently
        // treated as a strong one whose value begins with "w/".
        var parsed = IfMatch.TryParse(header, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("7")]            // unquoted
    [InlineData("\"7")]          // unterminated
    [InlineData("7\"")]          // unopened
    [InlineData("\"\"")]         // empty tag
    [InlineData("\"abc\"")]      // not a number
    [InlineData("\"-1\"")]       // negative: row versions start at 1
    [InlineData("\"1.5\"")]      // not an integer
    [InlineData("\" 7 \"")]      // padded inside the quotes
    [InlineData("\"+7\"")]       // signed
    [InlineData("\"1,000\"")]    // grouped
    [InlineData("\"99999999999\"")] // overflows int
    public void TryParse_RejectsMalformedValues(string header)
    {
        var parsed = IfMatch.TryParse(header, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void ToETag_RoundTripsThroughTryParse()
    {
        // The two halves have to agree, or a client echoing back the ETag we advertised would
        // be rejected by our own parser.
        foreach (var version in new[] { 0, 1, 42, int.MaxValue })
        {
            var parsed = IfMatch.TryParse(IfMatch.ToETag(version), out var rowVersion, out var error);

            Assert.True(parsed, error);
            Assert.Equal(version, rowVersion);
        }
    }
}
