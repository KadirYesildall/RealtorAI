using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Api.Concurrency;

/// <summary>
/// Parses and formats the <c>If-Match</c> header carrying a row version (FR-066).
///
/// <para>
/// The wire format is an RFC 9110 entity tag — the version in double quotes, e.g.
/// <c>If-Match: "7"</c> — rather than a bare number. Using the standard form means
/// intermediaries and HTTP clients treat it as the conditional-request header it is, and it
/// leaves room for a future opaque tag without a breaking change.
/// </para>
/// <para>
/// A weak tag (<c>W/"7"</c>) is rejected rather than accepted leniently. Weak comparison is
/// explicitly not allowed for <c>If-Match</c> in RFC 9110, and for a concurrency check
/// "approximately this version" is meaningless: either the caller edited the state we have or
/// they did not.
/// </para>
/// <para>
/// <c>If-Match: *</c> is also rejected. It means "any current representation", which is the
/// opposite of what this guard is for — accepting it would turn every conditional update into
/// an unconditional one, silently reintroducing the lost-update problem the header exists to
/// prevent.
/// </para>
/// </summary>
public static class IfMatch
{
    /// <summary>
    /// Attempts to read a row version from a raw header value.
    /// </summary>
    /// <param name="headerValue">The raw <c>If-Match</c> value, or null when absent.</param>
    /// <param name="rowVersion">The parsed version when successful.</param>
    /// <param name="error">A caller-facing explanation when unsuccessful.</param>
    public static bool TryParse(
        string? headerValue,
        out int rowVersion,
        [NotNullWhen(false)] out string? error)
    {
        rowVersion = 0;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            error = "The If-Match header is required for this request so a concurrent edit "
                + "cannot be overwritten unseen. Send the rowVersion you read, as If-Match: \"7\".";
            return false;
        }

        var value = headerValue.Trim();

        if (value == "*")
        {
            error = "If-Match: * is not accepted. It would make the update unconditional, "
                + "which defeats the concurrency check. Send the specific rowVersion you read.";
            return false;
        }

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            error = "A weak entity tag is not valid for If-Match. Send a strong tag: If-Match: \"7\".";
            return false;
        }

        if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
        {
            error = $"If-Match must be a quoted entity tag, for example \"7\". Received: {headerValue}";
            return false;
        }

        var inner = value[1..^1];

        // NumberStyles.None rejects a leading sign, thousands separators and surrounding
        // whitespace. A row version is a plain non-negative integer; anything else is a client
        // bug worth reporting rather than coercing.
        if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"If-Match must contain a non-negative integer row version. Received: {headerValue}";
            return false;
        }

        rowVersion = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Formats a row version as an <c>ETag</c> value, so a response advertises the version a
    /// subsequent <c>If-Match</c> should carry.
    /// </summary>
    public static string ToETag(int rowVersion) =>
        "\"" + rowVersion.ToString(CultureInfo.InvariantCulture) + "\"";
}
