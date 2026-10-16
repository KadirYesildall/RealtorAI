using Pgvector;

namespace Api.Data.Entities;

// T009 — entity types for 0006_media.sql. See BrokerageEntities.cs for the no-navigation and
// jsonb-as-string decisions that apply across this folder.

/// <summary>
/// public.photos.
/// <para>
/// <c>OriginalStorageKey</c> points into the write-once bucket: 0011_storage_buckets.sql
/// grants INSERT and SELECT there and no UPDATE or DELETE to any application role, so the
/// original upload cannot be replaced or removed by this service at all (FR-001). Normalized
/// derivatives live under <c>NormalizedStorageKey</c> in a separate bucket; only non-generative
/// operations produce them (Principle VIII.6).
/// </para>
/// <para>
/// The three alteration columns move together or not at all. A check constraint requires that
/// <c>Altered</c> implies both <c>AlterationType</c> and <c>DisclosureLabel</c> are present
/// (FR-020, California AB 723), so a disclosed-but-unlabelled photo is not representable.
/// </para>
/// </summary>
public sealed class Photo
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string OriginalStorageKey { get; set; } = string.Empty;
    public string? NormalizedStorageKey { get; set; }

    /// <summary>
    /// Lower-case hex SHA-256 of the original bytes. Used for exact-duplicate rejection, which
    /// needs no model and therefore works even while the embedding model is disabled.
    /// </summary>
    public string ChecksumSha256 { get; set; } = string.Empty;

    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? Bytes { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? Caption { get; set; }
    public bool IsCover { get; set; }

    /// <summary>Declared by the agent about their own editing, never set by this system.</summary>
    public bool Altered { get; set; }

    public AlterationType? AlterationType { get; set; }
    public string? DisclosureLabel { get; set; }
    public Guid? PairedOriginalId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>
    /// FR-011: the agent's room type, winning over the analysis when set. The model's own value
    /// stays in <see cref="PhotoAnalysis.RoomType"/>, so the override and what it replaced are both
    /// on record.
    /// </summary>
    public RoomType? RoomTypeOverride { get; set; }

    /// <summary>jsonb array. FR-011: the agent's feature tags, replacing the model's when set.</summary>
    public string? FeatureTagsOverride { get; set; }
}

/// <summary>
/// public.photo_analyses — model output about one photo, kept separate from the photo itself.
/// <para>
/// Separate table rather than columns on <c>photos</c> because analysis is re-runnable and
/// model-versioned: <c>ModelId</c>, <c>PromptVersion</c>, and <c>PromptHash</c> record which
/// model and prompt produced the row (Principle VIII.5), so an output can always be traced to
/// its producer. <c>Failed</c> plus <c>FailureReason</c> make a failed analysis a recorded fact
/// rather than a missing row, which is what lets the UI say "analysis unavailable, enter by
/// hand" instead of silently showing nothing (SC-010).
/// </para>
/// </summary>
public sealed class PhotoAnalysis
{
    public Guid Id { get; set; }
    public Guid PhotoId { get; set; }
    public RoomType? RoomType { get; set; }
    public decimal? RoomTypeConfidence { get; set; }

    /// <summary>jsonb array.</summary>
    public string FeatureTags { get; set; } = "[]";

    /// <summary>jsonb array. Deterministic sidecar metrics: blurry, too_dark, and so on.</summary>
    public string QualityFlags { get; set; } = "[]";

    /// <summary>jsonb array. Faces, visible people, reflections, address plates.</summary>
    public string ContentFlags { get; set; } = "[]";

    public string? AltText { get; set; }
    public bool CoverRecommended { get; set; }
    public int? OrderRecommendation { get; set; }
    public string? RecommendationReason { get; set; }
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public string? PromptHash { get; set; }

    /// <summary>jsonb. The unmodified model response, kept for auditability (FR-049).</summary>
    public string? RawOutput { get; set; }

    public bool Failed { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.photo_embeddings — near-duplicate detection only.
/// <para>
/// <b>The 768 dimension is PROVISIONAL.</b> It was chosen from the candidate model's
/// documented output size, but the <c>photo.embedding</c> registry entry is still
/// <c>enabled = false</c> with <c>eval_status = 'unverified'</c> under Principle VIII.4, so no
/// embedding has actually been produced and measured. When the model is cleared, the writer
/// must compare the real vector length against this column and refuse a mismatch rather than
/// truncate or pad — a silently resized vector would make similarity scores meaningless while
/// still looking plausible. Until then <c>/embed</c> returns 503 and duplicate detection
/// degrades to the exact-checksum match on <c>photos</c>.
/// </para>
/// </summary>
public sealed class PhotoEmbedding
{
    /// <summary>Primary key — one embedding per photo, from one model at a time.</summary>
    public Guid PhotoId { get; set; }

    /// <summary>pgvector <c>vector(768)</c>; see the provisional-dimension warning above.</summary>
    public Vector Embedding { get; set; } = new(new float[768]);

    /// <summary>Which registry model produced this vector; vectors from different models are not comparable.</summary>
    public string ModelId { get; set; } = string.Empty;
}
