namespace Modules.Audit;

/// <summary>
/// One thing that happened, as recorded in the audit trail (FR-049).
///
/// <para>
/// Most fields are optional because the eighteen actions carry genuinely different evidence: an
/// <see cref="AuditAction.AiCall"/> has a model, a prompt version and references to its exact
/// input and output, while a <see cref="AuditAction.MembershipDecided"/> has none of those and
/// an actor instead. Which fields are populated is itself part of the record — it says how the
/// event came about — so the type permits the sparse shapes rather than forcing placeholder
/// values that would later be indistinguishable from real ones.
/// </para>
/// <para>
/// <see cref="AuditWriter"/> validates the combinations that must hold, so "optional here" does
/// not mean "unchecked".
/// </para>
/// </summary>
public sealed record AuditEntry
{
    /// <summary>What happened.</summary>
    public required AuditAction Action { get; init; }

    /// <summary>
    /// Who did it, or null for a system-initiated action.
    ///
    /// <para>
    /// Null is permitted for exactly one action today —
    /// <see cref="AuditAction.OriginalsPurged"/>, which runs out of band with its own
    /// credential and has no user behind it. Every other action is attributed, and the writer
    /// rejects an unattributed one rather than storing an event nobody is accountable for.
    /// </para>
    /// </summary>
    public Guid? ActorId { get; init; }

    /// <summary>The listing this concerns, when it concerns one.</summary>
    public Guid? ListingId { get; init; }

    /// <summary>The brokerage this concerns, for membership and settings events.</summary>
    public Guid? BrokerageId { get; init; }

    /// <summary>
    /// What kind of thing was acted on — a listing field, a photo, a content draft, a registry
    /// row — paired with <see cref="TargetId"/>.
    /// </summary>
    public string? TargetType { get; init; }

    public Guid? TargetId { get; init; }

    /// <summary>
    /// State before the change, as JSON. Present for edits, so the trail shows what was
    /// replaced and not merely that something was.
    /// </summary>
    public string? BeforeValue { get; init; }

    /// <summary>State after the change, as JSON.</summary>
    public string? AfterValue { get; init; }

    /// <summary>
    /// Which registry model produced this, for AI-derived events. Recorded from the registry
    /// row rather than from configuration, so the trail names the model that actually ran.
    /// </summary>
    public string? ModelId { get; init; }

    public string? PromptVersion { get; init; }

    /// <summary>
    /// Hash of the prompt text as it was at call time (Principle VIII.5). Without it, an edited
    /// prompt would silently invalidate every past record that cited its version number.
    /// </summary>
    public string? PromptHash { get; init; }

    /// <summary>Storage key of the exact input sent to the model.</summary>
    public string? InputRef { get; init; }

    /// <summary>Storage key of the exact output received.</summary>
    public string? OutputRef { get; init; }
}
