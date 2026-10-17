using System.Text.Json;
using System.Text.Json.Nodes;

namespace Libs.Ai.Tasks;

/// <summary>One fact the model may draw on and must cite by <paramref name="FieldId"/>.</summary>
/// <param name="Value">The field's stored JSON value.</param>
/// <param name="Provenance">The contract spelling, e.g. <c>agent_entered</c>.</param>
public sealed record ContentFact(Guid FieldId, string Key, JsonNode? Value, string Provenance, bool Confirmed);

/// <summary>The listing's basic details, as the agent entered them.</summary>
public sealed record ContentBasics(
    string City,
    string State,
    string PropertyType,
    decimal? ListPrice,
    int? Bedrooms,
    decimal? Bathrooms,
    string? AgentNotes);

/// <summary>What one analysed photo showed: its room and visible features.</summary>
public sealed record PhotoObservation(string RoomType, IReadOnlyList<string> Features);

/// <summary>Everything one <c>listing.content</c> call is given.</summary>
/// <param name="RunId">
/// The pipeline run asking. Part of the input so every run is a different request: the response
/// cache keys on the input, and an agent asking for new words must not get the old ones back
/// from it.
/// </param>
public sealed record ListingContentInput(
    Guid ListingId,
    Guid ActorId,
    Guid RunId,
    ContentBasics Basics,
    IReadOnlyList<ContentFact> Facts,
    IReadOnlyList<PhotoObservation> Photos);

/// <summary>One sentence of a variant, with the field ids it says it rests on.</summary>
/// <param name="SupportingFieldIds">
/// As the model gave them. Ids that are not well-formed are dropped here, and ids that are not
/// facts of this listing are judged by grounding.
/// </param>
public sealed record GeneratedSentence(string Text, IReadOnlyList<Guid> SupportingFieldIds, bool CitesMalformedId);

/// <summary>One alternative of the requested kind, sentence by sentence.</summary>
public sealed record GeneratedVariant(IReadOnlyList<GeneratedSentence> Sentences);

/// <summary>A validated text-mode answer and where it came from (VIII.5).</summary>
public sealed record GeneratedText(IReadOnlyList<GeneratedVariant> Variants, AiResult Call);

/// <summary>A validated structured-mode answer: proposed values per enumerated field.</summary>
public sealed record GeneratedStructure(IReadOnlyDictionary<string, IReadOnlyList<string>> Values, AiResult Call);

/// <summary>
/// T110 — the <c>listing.content</c> task: the request it sends and the answer it reads back
/// (FR-012, FR-013, FR-014).
///
/// <para>
/// <b>Two modes, one prompt.</b> Structured mode proposes values for the enumerated RESO fields
/// from the photo observations, choosing among the values it is given. Text mode writes the
/// requested number of alternatives of one kind, to a character limit, in a tone. Both go
/// through <see cref="IAiService"/>, so the model, budget, validation and retry are resolved
/// there and nowhere else.
/// </para>
/// <para>
/// <b>What it does not do.</b> It does not filter values or sentences. It hands back what the
/// model said, already schema-checked, and the content module decides what survives. That way
/// the rules about which claims are allowed are in one place, which is not a prompt.
/// </para>
/// <para>
/// <b>What the model is given.</b> The basic details, the facts with their provenance, and what
/// the photos showed. The street address is never sent: public remarks must not carry it, and
/// a model that never sees it cannot put it in (VIII.4.c).
/// </para>
/// </summary>
public static class ListingContentTask
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Text mode: <paramref name="variantCount"/> alternatives of <paramref name="kind"/>.</summary>
    /// <exception cref="ModelUnavailableException">No model is enabled for <c>listing.content</c>.</exception>
    /// <exception cref="AiBudgetExceededException">The call would exceed the listing's budget.</exception>
    /// <exception cref="StructuredOutputException">The answer failed validation twice.</exception>
    public static async Task<GeneratedText> GenerateListingContentAsync(
        this IAiService ai,
        ListingContentInput input,
        string kind,
        string tone,
        int variantCount,
        int maxCharacters,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(variantCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);

        var request = Request(input, "text");
        request["kind"] = kind;
        request["tone"] = tone;
        request["variantCount"] = variantCount;
        request["maxCharacters"] = maxCharacters;

        var result = await ai.ExecuteAsync(
            new AiRequest(AiTaskKey.ListingContent, input.ListingId, input.ActorId, request.ToJsonString(), ImageCount: 0, estimatedTokens),
            cancellationToken);

        var variants = (result.Document["variants"]?.AsArray() ?? [])
            .Take(variantCount)
            .Select(variant => new GeneratedVariant(variant!["sentences"]!.AsArray().Select(Sentence).ToList()))
            .ToList();

        return new GeneratedText(variants, result);
    }

    /// <summary>
    /// Structured mode: proposed values for each field of <paramref name="allowedValues"/>, from
    /// the photo observations.
    /// </summary>
    public static async Task<GeneratedStructure> ProposeStructuredFieldsAsync(
        this IAiService ai,
        ListingContentInput input,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allowedValues,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(allowedValues);

        var request = Request(input, "structured");
        request["allowedValues"] = JsonSerializer.SerializeToNode(allowedValues, Web);

        var result = await ai.ExecuteAsync(
            new AiRequest(AiTaskKey.ListingContent, input.ListingId, input.ActorId, request.ToJsonString(), ImageCount: 0, estimatedTokens),
            cancellationToken);

        var values = (result.Document["structured"]?.AsObject() ?? [])
            .ToDictionary(
                field => field.Key,
                field => (IReadOnlyList<string>)field.Value!.AsArray().Select(value => value!.GetValue<string>()).ToList(),
                StringComparer.Ordinal);

        return new GeneratedStructure(values, result);
    }

    private static JsonObject Request(ListingContentInput input, string mode) => new()
    {
        ["mode"] = mode,
        ["run"] = input.RunId.ToString("D"),
        ["listing"] = JsonSerializer.SerializeToNode(input.Basics, Web),
        ["facts"] = new JsonArray(input.Facts
            .Select(fact => (JsonNode)new JsonObject
            {
                ["fieldId"] = fact.FieldId.ToString("D"),
                ["key"] = fact.Key,
                ["value"] = fact.Value?.DeepClone(),
                ["provenance"] = fact.Provenance,
                ["confirmed"] = fact.Confirmed,
            })
            .ToArray()),
        ["photoObservations"] = JsonSerializer.SerializeToNode(input.Photos, Web),
    };

    private static GeneratedSentence Sentence(JsonNode? node)
    {
        var ids = new List<Guid>();
        var malformed = false;

        foreach (var id in node!["supportingFieldIds"]!.AsArray())
        {
            if (Guid.TryParse(id!.GetValue<string>(), out var parsed))
            {
                ids.Add(parsed);
            }
            else
            {
                malformed = true;
            }
        }

        return new GeneratedSentence(node["text"]!.GetValue<string>().Trim(), ids.Distinct().ToList(), malformed);
    }
}
