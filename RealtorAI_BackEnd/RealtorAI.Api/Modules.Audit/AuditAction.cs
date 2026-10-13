namespace Modules.Audit;

/// <summary>
/// The closed set of auditable actions (FR-049).
///
/// <para>
/// Owned by this module rather than by the persistence layer, because the audit vocabulary is a
/// domain concept: it is the list of things this product considers worth remembering about who
/// did what. The PostgreSQL <c>audit_action</c> enum in 0009_audit.sql is the storage
/// representation of this list, and the label for each member is declared in
/// <c>Api.Data.PostgresEnumMappings</c> — explicitly, so a rename here cannot quietly start
/// writing a different value.
/// </para>
/// <para>
/// This enum carries no persistence attributes and this module references no database package.
/// Adding a member here without the matching migration fails on first write, which is the
/// intended direction: the database owns what is storable, the domain owns what is meaningful.
/// </para>
/// </summary>
public enum AuditAction
{
    /// <summary>
    /// A model was invoked. Records task, model id, prompt version and hash, and references to
    /// the exact input and output — the evidence that makes an AI-derived value traceable.
    /// </summary>
    AiCall,

    /// <summary>An agent changed a listing field's value.</summary>
    FieldEdit,

    /// <summary>
    /// An agent affirmed a field. Distinct from an edit because confirmation is the act that
    /// moves responsibility for an AI-derived value onto a human.
    /// </summary>
    FieldConfirm,

    /// <summary>
    /// An agent overrode a model's conclusion about a photo. The original analysis is never
    /// modified, so the override and what it replaced both remain visible.
    /// </summary>
    PhotoOverride,

    PhotoReorder,

    PhotoDelete,

    /// <summary>A compliance check produced a finding.</summary>
    FindingRaised,

    /// <summary>
    /// A warning-severity finding was accepted with a recorded reason. Only warnings can reach
    /// this state; blocking findings have no override path (FR-022).
    /// </summary>
    FindingAcknowledged,

    ContentGenerated,

    ContentEdited,

    /// <summary>A human approved content for a channel (Principle VIII.7).</summary>
    ApprovalGranted,

    ApprovalRevoked,

    /// <summary>
    /// An approval was refused because the content hash the agent reviewed no longer matched
    /// what the server held (FR-066). Recorded rather than silently retried: a near-miss here
    /// means someone almost approved text they had not read.
    /// </summary>
    ApprovalRejectedHashMismatch,

    MembershipRequested,

    /// <summary>A broker admin approved or rejected a membership request (FR-062).</summary>
    MembershipDecided,

    /// <summary>
    /// A platform administrator changed the AI model registry or a prompt version. Every such
    /// change is audited with the acting administrator (FR-064), because enabling a model is
    /// the decision with the widest blast radius in the system.
    /// </summary>
    RegistryChanged,

    ListingSoftDeleted,

    /// <summary>
    /// The out-of-band retention job purged photo originals after the retention period
    /// (FR-065). The actor is null for this one: no user initiates it.
    /// </summary>
    OriginalsPurged,
}
