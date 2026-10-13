namespace Api.Data.Entities;

// T009 — entity types for 0007_content_compliance.sql. See BrokerageEntities.cs for the
// no-navigation and jsonb-as-string decisions that apply across this folder.

/// <summary>
/// public.content_drafts — one generated or edited piece of listing copy.
/// <para>
/// <c>SupportingFieldIds</c> is the provenance chain for prose: it lists the
/// <c>listing_fields</c> rows the draft is allowed to have drawn on, which is what makes
/// "never invent facts" checkable after the fact rather than merely instructed in a prompt.
/// </para>
/// </summary>
public sealed class ContentDraft
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public ContentKind Kind { get; set; }

    /// <summary>Zero-based index distinguishing alternative drafts of the same kind.</summary>
    public int VariantIndex { get; set; }

    public Tone? Tone { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>jsonb array of listing_field ids — see the type remarks.</summary>
    public string SupportingFieldIds { get; set; } = "[]";

    /// <summary>Monotonic edit counter, distinct from <c>RowVersion</c>, which guards writes.</summary>
    public int Version { get; set; }

    public ContentStatus Status { get; set; }
    public bool Selected { get; set; }
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public string? PromptHash { get; set; }

    /// <summary>Optimistic-concurrency token for <c>If-Match</c> (FR-066); see <see cref="Listing"/>.</summary>
    public int RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.compliance_findings — one problem found in a field, a draft, or a photo.
/// <para>
/// A check constraint allows the <c>acknowledged</c> status only when <c>Severity</c> is
/// <c>warning</c> (FR-022). There is deliberately no override path for a <c>blocking</c>
/// finding: fair-housing and MLS-rule violations are not the agent's risk alone to accept, so
/// the database refuses the state rather than leaving it to a UI that could be bypassed by
/// calling the API directly.
/// </para>
/// <para>
/// <c>LexiconVersion</c>, <c>ModelId</c>, and <c>PromptVersion</c> are all nullable because
/// the three sources populate different ones: a deterministic rule check has no model, and a
/// lexicon hit has no prompt. Which fields are set is itself evidence of how the finding was
/// produced.
/// </para>
/// </summary>
public sealed class ComplianceFinding
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }

    /// <summary>What the finding is about: a listing field, a content draft, or a photo.</summary>
    public string TargetType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }
    public CheckType CheckType { get; set; }
    public FindingSeverity Severity { get; set; }
    public FindingSource Source { get; set; }

    /// <summary>Character offset into the target text, when the finding points at a phrase.</summary>
    public int? SpanStart { get; set; }

    public int? SpanEnd { get; set; }
    public string? Phrase { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? SuggestedFix { get; set; }
    public FindingStatus Status { get; set; }
    public string? AcknowledgedReason { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? LexiconVersion { get; set; }
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.approvals — the human-in-the-loop record (Principle VIII.7, FR-023).
/// <para>
/// <c>ContentHash</c> is the whole point of this table. It records a hash of exactly what the
/// agent was shown, so approval binds to reviewed content rather than to a listing id. If the
/// content changed between render and approve, the hash will not match and the approval is
/// rejected and audited as <c>approval_rejected_hash_mismatch</c> — closing the race where a
/// background regeneration lands mid-review and the agent unknowingly approves text they
/// never read.
/// </para>
/// <para>
/// 0007 grants INSERT on this table to the owning agent only, and never to the service role: a
/// machine identity must not be able to manufacture a human approval.
/// </para>
/// </summary>
public sealed class Approval
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }

    /// <summary>Which downstream channel this approval authorises, e.g. MLS or marketing.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Hash of the content as rendered for review — see the type remarks.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public Guid ApprovedBy { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}
