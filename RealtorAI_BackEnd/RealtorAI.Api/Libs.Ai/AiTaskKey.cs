namespace Libs.Ai;

/// <summary>
/// The logical AI capabilities this feature dispatches (Principle VIII.1).
///
/// <para>
/// Callers ask for a <i>task</i>, never for a model. "Classify this room" is a task;
/// "google/siglip2-base-patch16-224" is an implementation detail that lives in the
/// <c>ai_model_registry</c> table and nowhere else. That separation is the whole of VIII.1: a
/// model or provider change becomes a gated configuration change rather than a code change,
/// and no vendor name appears in the call site.
/// </para>
/// <para>
/// These four strings are also enforced by the <c>registry_task_key_known</c> check constraint
/// in 0008_ai_platform.sql, so a typo here cannot create a registry row that nothing dispatches
/// to.
/// </para>
/// </summary>
public static class AiTaskKey
{
    /// <summary>Room type, feature tags, content flags and alt text for one photo.</summary>
    public const string PhotoAnalysis = "photo.analysis";

    /// <summary>
    /// Image embedding for near-duplicate detection. Still disabled and unverified under
    /// VIII.4, so this task degrades to exact-checksum matching (SC-010).
    /// </summary>
    public const string PhotoEmbedding = "photo.embedding";

    /// <summary>Public remarks, private remarks and headline drafts.</summary>
    public const string ListingContent = "listing.content";

    /// <summary>
    /// Fair-housing and risky-claim review of drafted copy. A second opinion on top of the
    /// deterministic lexicon, never a replacement for it: a model that misses a violation must
    /// not be able to clear text the lexicon would have blocked.
    /// </summary>
    public const string ComplianceJudge = "compliance.judge";

    /// <summary>All known task keys, for validation and for the evaluation harness.</summary>
    public static IReadOnlyList<string> All { get; } =
        [PhotoAnalysis, PhotoEmbedding, ListingContent, ComplianceJudge];

    /// <summary>
    /// Rejects an unknown task key before it reaches the database, so the caller gets a usable
    /// message instead of a check-constraint violation.
    /// </summary>
    public static void EnsureKnown(string taskKey)
    {
        if (!All.Contains(taskKey, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{taskKey}' is not a known AI task. Known tasks: {string.Join(", ", All)}. "
                + "Adding one requires a migration extending the registry_task_key_known "
                + "constraint, a prompt with a versioned schema, and an evaluation run.",
                nameof(taskKey));
        }
    }
}
