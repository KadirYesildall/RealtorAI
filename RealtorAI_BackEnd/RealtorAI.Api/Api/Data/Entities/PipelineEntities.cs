namespace Api.Data.Entities;

// US2 — entity types for 0015_pipeline_runs.sql. See BrokerageEntities.cs for the
// no-navigation decision that applies across this folder.

/// <summary>
/// public.pipeline_runs — one run of a listing's pipeline: an analysis, or a regeneration of one
/// kind of copy. At most one is unfinished per listing
/// (a partial unique index), which is what makes starting the pipeline idempotent.
/// </summary>
public sealed class PipelineRun
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }

    /// <summary>The agent whose action started the run; every AI call it makes is audited as theirs.</summary>
    public Guid StartedBy { get; set; }

    public Tone? Tone { get; set; }
    public int? VariantCount { get; set; }

    /// <summary>The one kind a regeneration is for (0016); null for an analysis run, which generates every kind.</summary>
    public ContentKind? ContentKind { get; set; }

    /// <summary>The one item a compliance run checks (0017); null with its id for the whole listing.</summary>
    public string? ComplianceTargetType { get; set; }

    public Guid? ComplianceTargetId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// public.pipeline_step_states — where one step of a run stands. <c>State</c> holds the
/// contract's spelling (<c>queued</c>, <c>running</c>, <c>done</c>, <c>failed</c>,
/// <c>skipped</c>), constrained by a check in the migration.
/// </summary>
public sealed class PipelineStepState
{
    public Guid RunId { get; set; }
    public string Step { get; set; } = string.Empty;
    public int Position { get; set; }
    public string State { get; set; } = "queued";
    public string? FailureReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>public.pipeline_photo_states — where one photo stands in a run.</summary>
public sealed class PipelinePhotoState
{
    public Guid RunId { get; set; }
    public Guid PhotoId { get; set; }
    public string State { get; set; } = "queued";
    public string? FailureReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
