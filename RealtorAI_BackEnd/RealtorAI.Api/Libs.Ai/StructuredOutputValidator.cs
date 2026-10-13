using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Libs.Ai;

/// <summary>Outcome of validating one model response against its declared schema.</summary>
/// <param name="IsValid">Whether the response satisfied the schema.</param>
/// <param name="Document">The parsed response, when valid.</param>
/// <param name="Errors">Human-readable validation failures, when invalid.</param>
public sealed record ValidationOutcome(bool IsValid, JsonNode? Document, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// The errors formatted for feeding back to the model on its one retry. Phrased as
    /// instructions because that is what it is used for.
    /// </summary>
    public string ToRetryFeedback() =>
        "Your previous response did not match the required schema. Fix exactly these problems "
        + "and return only the corrected JSON:\n- " + string.Join("\n- ", Errors);
}

/// <summary>Raised when a model response still fails validation after the single retry.</summary>
public sealed class StructuredOutputException : Exception
{
    public StructuredOutputException(string taskKey, IReadOnlyList<string> errors)
        : base($"The model response for '{taskKey}' did not match its schema after one retry: "
            + string.Join("; ", errors))
    {
        TaskKey = taskKey;
        Errors = errors;
    }

    public StructuredOutputException()
        : base("The model response did not match its schema.")
    {
        TaskKey = string.Empty;
        Errors = [];
    }

    public StructuredOutputException(string message)
        : base(message)
    {
        TaskKey = string.Empty;
        Errors = [];
    }

    public StructuredOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
        TaskKey = string.Empty;
        Errors = [];
    }

    public string TaskKey { get; }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// T047 — validates model output against the schema declared alongside its prompt (FR-015).
///
/// <para>
/// <b>Exactly one retry, then an explicit failure.</b> FR-015 specifies this precisely, and the
/// bound matters in both directions. One retry is worth having: models frequently fix a
/// malformed response when told what was wrong, so a single corrective attempt turns most
/// transient formatting failures into successes. More than one is not: a model that fails twice
/// on the same schema is usually wrong about the task rather than the formatting, and looping
/// would spend an agent's token budget (FR-055) discovering that slowly.
/// </para>
/// <para>
/// <b>And it fails loudly rather than degrading.</b> There is no "best effort" path that
/// accepts a partially valid response. An AI-derived listing field that skipped validation is
/// an unverified claim about someone's property entering a legal document, so the pipeline
/// records the failure and the field stays empty for the agent to fill in — which is the
/// designed degradation (SC-010), not an error state.
/// </para>
/// </summary>
public static class StructuredOutputValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        // Report every failure, not just the first. The retry feedback is only useful if it
        // lists all the problems — fixing one at a time would need more retries than FR-015
        // allows.
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// Validates <paramref name="responseText"/> against <paramref name="schemaJson"/>.
    /// </summary>
    public static ValidationOutcome Validate(string responseText, string schemaJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new ValidationOutcome(false, null, ["The response was empty."]);
        }

        JsonSchema schema;

        try
        {
            schema = JsonSchema.FromText(schemaJson);
        }
        catch (JsonException exception)
        {
            // Our schema, not the model's output — a deployment problem, not a model problem,
            // and retrying would not help.
            throw new StructuredOutputException(
                "The declared output schema is not valid JSON. This is a prompt-registry "
                + "problem, not a model problem.",
                exception);
        }

        JsonNode? document;

        try
        {
            document = JsonNode.Parse(ExtractJson(responseText));
        }
        catch (JsonException exception)
        {
            return new ValidationOutcome(
                false,
                null,
                [$"The response was not valid JSON: {exception.Message}"]);
        }

        if (document is null)
        {
            return new ValidationOutcome(false, null, ["The response parsed to JSON null."]);
        }

        // JsonSchema.Net evaluates a JsonElement, while callers want a JsonNode to navigate.
        // Serialising once to bridge the two costs a fraction of a millisecond next to the
        // model call that produced this response, and keeps the public shape convenient.
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(document), Options);

        if (result.IsValid)
        {
            return new ValidationOutcome(true, document, []);
        }

        return new ValidationOutcome(false, null, Describe(result));
    }

    /// <summary>
    /// Runs the attempt, and on failure re-runs it once with the validation errors appended.
    /// </summary>
    /// <param name="taskKey">For the exception message and the audit record.</param>
    /// <param name="schemaJson">The declared output schema.</param>
    /// <param name="attempt">
    /// Invoked with null on the first call, and with retry feedback on the second.
    /// </param>
    public static async Task<JsonNode> ValidateWithSingleRetryAsync(
        string taskKey,
        string schemaJson,
        Func<string?, CancellationToken, Task<string>> attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var first = Validate(await attempt(null, cancellationToken), schemaJson);

        if (first.IsValid)
        {
            return first.Document!;
        }

        // The one retry. The errors are passed back so the model is told what to fix rather
        // than simply asked again — re-asking an identical question tends to produce an
        // identical answer, which would make the retry pure cost.
        var second = Validate(
            await attempt(first.ToRetryFeedback(), cancellationToken),
            schemaJson);

        if (second.IsValid)
        {
            return second.Document!;
        }

        throw new StructuredOutputException(taskKey, [.. first.Errors, .. second.Errors]);
    }

    /// <summary>
    /// Pulls the JSON object or array out of a response that wrapped it in prose or a fenced
    /// code block.
    ///
    /// <para>
    /// Models do this despite instructions not to, and rejecting an otherwise correct response
    /// over a markdown fence would spend the single retry on formatting instead of substance.
    /// This only trims around the outermost braces or brackets — it never alters the JSON — so
    /// a genuinely malformed response still fails validation.
    /// </para>
    /// </summary>
    internal static string ExtractJson(string responseText)
    {
        var text = responseText.Trim();

        var firstObject = text.IndexOf('{', StringComparison.Ordinal);
        var firstArray = text.IndexOf('[', StringComparison.Ordinal);

        var start = (firstObject, firstArray) switch
        {
            (-1, -1) => -1,
            (-1, var array) => array,
            (var obj, -1) => obj,
            var (obj, array) => Math.Min(obj, array),
        };

        if (start < 0)
        {
            return text;
        }

        var closing = text[start] == '{' ? '}' : ']';
        var end = text.LastIndexOf(closing);

        return end > start ? text[start..(end + 1)] : text;
    }

    private static List<string> Describe(EvaluationResults results)
    {
        var messages = new List<string>();

        Collect(results, messages);

        if (messages.Count == 0)
        {
            messages.Add("The response did not match the schema, but no specific error was reported.");
        }

        return messages;
    }

    private static void Collect(EvaluationResults results, List<string> messages)
    {
        if (results.Errors is not null)
        {
            foreach (var (keyword, message) in results.Errors)
            {
                // The instance location is what makes the feedback actionable: "required: a
                // required property is missing" is far less useful than naming the path.
                var location = results.InstanceLocation.ToString();

                messages.Add(
                    string.IsNullOrEmpty(location)
                        ? $"{keyword}: {message}"
                        : $"{location} ({keyword}): {message}");
            }
        }

        // Nested results carry the failures from subschemas — anyOf branches, array item
        // evaluations — which is usually where the actionable detail is.
        if (results.Details is null)
        {
            return;
        }

        foreach (var detail in results.Details)
        {
            Collect(detail, messages);
        }
    }
}
