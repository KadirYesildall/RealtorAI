namespace Libs.Ai;

/// <summary>
/// Whether a registry entry has passed the project's AI evaluation harness
/// (Constitution Principle VIII.4.b).
///
/// <para>
/// Owned by this library rather than by the persistence layer, because evaluation status is AI
/// platform vocabulary. The PostgreSQL <c>model_eval_status</c> enum is its storage
/// representation and the labels are declared in <c>Api.Data.PostgresEnumMappings</c> — the
/// same arrangement used for membership status and the audit action list (research R20), which
/// keeps one definition of each concept instead of a copy per side of the module boundary.
/// </para>
/// </summary>
public enum ModelEvalStatus
{
    /// <summary>
    /// The harness has not been run. The state every entry ships in, and the reason a fresh
    /// deployment dispatches to no model at all.
    /// </summary>
    Unverified,

    /// <summary>Passed. Required, together with a recorded license, before an entry may be enabled.</summary>
    Passed,

    /// <summary>
    /// Ran and failed. Distinct from <see cref="Unverified"/> so "we evaluated this and it was
    /// not good enough" is not confused with "we never looked".
    /// </summary>
    Failed,
}
