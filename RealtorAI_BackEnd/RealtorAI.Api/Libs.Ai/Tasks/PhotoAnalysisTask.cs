using System.Text.Json;

namespace Libs.Ai.Tasks;

/// <summary>
/// The <c>photo.analysis</c> request text (FR-009, VIII.4.c).
///
/// <para>
/// Defined here, beside <see cref="ListingContentTask"/>, rather than inside the pipeline step,
/// because two callers must send exactly the same thing: the pipeline, and the evaluation
/// harness measuring a vision model before it is enabled. A model evaluated with different
/// words beside the photo would have been measured on a question production never asks.
/// </para>
/// </summary>
public static class PhotoAnalysisTask
{
    /// <summary>
    /// The text beside the image: which photo this is, and nothing else — no address, no agent
    /// notes. The prompt states that this text is a reference, not an instruction.
    /// </summary>
    public static string BuildInput(Guid photoId) =>
        JsonSerializer.Serialize(new { photoId, request = "Analyse the attached photograph." });
}
