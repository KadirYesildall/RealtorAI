using System.Text.Json;

namespace Modules.Content;

/// <summary>
/// How a draft's claims are stored in <c>content_drafts.supporting_field_ids</c>: a JSON array
/// of <c>{ spanStart, spanEnd, fieldIds }</c>, one per sentence that cites a fact (research R35).
///
/// <para>
/// The column keeps its name and its jsonb type. What it holds is richer than a flat list of
/// ids, because a span is what the review screen highlights, and a flat list cannot say which
/// sentence rests on which fact. The flat list the contract reports is derived from it
/// (<see cref="GroundingService.SupportingFieldIds"/>).
/// </para>
/// </summary>
public static class ClaimStorage
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<Claim> claims) => JsonSerializer.Serialize(claims, Web);

    /// <summary>The stored claims; an empty list for a draft with none, or one written by hand.</summary>
    public static IReadOnlyList<Claim> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Claim>>(json, Web) ?? [];
}
