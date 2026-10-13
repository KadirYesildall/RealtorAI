using NpgsqlTypes;

namespace Api.Data;

// T009 — CLR mirrors of the 15 PostgreSQL enum types created by the SQL migrations.
//
// Every member carries an explicit [PgName]. Npgsql's default snake_case translator would
// produce the right label for most of these, but not reliably for acronym-leading members
// such as AiCall or MlsRule, and a silent mistranslation surfaces as a runtime cast error on
// a code path that may be rarely exercised. Spelling the label out makes the mapping
// reviewable against the migration that created the type, which is the file that owns it.
//
// The labels below were read from the live database (pg_enum) rather than from the migration
// source, so they reflect what is actually installed.

/// <summary>public.membership_status — 0003_brokerages.sql</summary>
public enum MembershipStatus
{
    [PgName("pending")] Pending,
    [PgName("approved")] Approved,
    [PgName("rejected")] Rejected,
    [PgName("revoked")] Revoked,
}

/// <summary>public.membership_role — 0003_brokerages.sql</summary>
public enum MembershipRole
{
    [PgName("agent")] Agent,
    [PgName("broker_admin")] BrokerAdmin,
}

/// <summary>public.tone — 0003_brokerages.sql</summary>
public enum Tone
{
    [PgName("professional")] Professional,
    [PgName("warm")] Warm,
    [PgName("luxury")] Luxury,
    [PgName("concise")] Concise,
}

/// <summary>
/// public.provenance — 0005_listings.sql. FR-007: the two AI values are rejected by a check
/// constraint on measurement and legal RESO keys, so a value here is not freely assignable.
/// </summary>
public enum Provenance
{
    [PgName("agent_entered")] AgentEntered,
    [PgName("public_record")] PublicRecord,
    [PgName("ai_detected_from_photo")] AiDetectedFromPhoto,
    [PgName("ai_suggested")] AiSuggested,
}

/// <summary>public.listing_status — 0005_listings.sql</summary>
public enum ListingStatus
{
    [PgName("draft")] Draft,
    [PgName("analyzing")] Analyzing,
    [PgName("ready_for_review")] ReadyForReview,
    [PgName("approved")] Approved,
    [PgName("archived")] Archived,
}

/// <summary>
/// public.alteration_type — 0006_media.sql. Describes an alteration the AGENT performed with
/// their own tools and is disclosing (FR-020). It is not a menu of operations this system can
/// perform: Principle VIII.6 forbids generative imagery outright, so nothing in this codebase
/// may produce a photo with <c>virtual_staging</c>.
/// </summary>
public enum AlterationType
{
    [PgName("virtual_staging")] VirtualStaging,
    [PgName("other")] Other,
}

/// <summary>public.room_type — 0006_media.sql</summary>
public enum RoomType
{
    [PgName("kitchen")] Kitchen,
    [PgName("living_room")] LivingRoom,
    [PgName("primary_bedroom")] PrimaryBedroom,
    [PgName("bedroom")] Bedroom,
    [PgName("bathroom")] Bathroom,
    [PgName("exterior_front")] ExteriorFront,
    [PgName("backyard")] Backyard,
    [PgName("pool")] Pool,
    [PgName("garage")] Garage,
    [PgName("view")] View,
    [PgName("floor_plan")] FloorPlan,
    [PgName("other")] Other,
}

/// <summary>public.content_kind — 0007_content_compliance.sql</summary>
public enum ContentKind
{
    [PgName("public_remarks")] PublicRemarks,
    [PgName("private_remarks")] PrivateRemarks,
    [PgName("headline")] Headline,
}

/// <summary>public.content_status — 0007_content_compliance.sql</summary>
public enum ContentStatus
{
    [PgName("draft")] Draft,
    [PgName("approved")] Approved,
    [PgName("stale")] Stale,
}

/// <summary>public.check_type — 0007_content_compliance.sql</summary>
public enum CheckType
{
    [PgName("fair_housing")] FairHousing,
    [PgName("risky_claim")] RiskyClaim,
    [PgName("mls_rule")] MlsRule,
    [PgName("photo_integrity")] PhotoIntegrity,
    [PgName("advertising_disclosure")] AdvertisingDisclosure,
}

/// <summary>
/// public.finding_severity — 0007_content_compliance.sql. Only <c>warning</c> may be
/// acknowledged; a <c>blocking</c> finding has no override path (FR-022), enforced by a check
/// constraint rather than by application code alone.
/// </summary>
public enum FindingSeverity
{
    [PgName("blocking")] Blocking,
    [PgName("warning")] Warning,
}

/// <summary>public.finding_source — 0007_content_compliance.sql</summary>
public enum FindingSource
{
    [PgName("lexicon")] Lexicon,
    [PgName("judge")] Judge,
    [PgName("deterministic")] Deterministic,
}

/// <summary>public.finding_status — 0007_content_compliance.sql</summary>
public enum FindingStatus
{
    [PgName("open")] Open,
    [PgName("resolved")] Resolved,
    [PgName("acknowledged")] Acknowledged,
    [PgName("stale")] Stale,
}

/// <summary>
/// public.model_eval_status — 0008_ai_platform.sql. Principle VIII.4: a registry row may only
/// be enabled while this is <c>passed</c> and a license is recorded.
/// </summary>
public enum ModelEvalStatus
{
    [PgName("unverified")] Unverified,
    [PgName("passed")] Passed,
    [PgName("failed")] Failed,
}

/// <summary>
/// public.audit_action — 0009_audit.sql. FR-049: this is the closed set of auditable actions.
/// Adding a member here without the matching migration will fail at runtime on first write,
/// which is the intended direction — the database owns the vocabulary.
/// </summary>
public enum AuditAction
{
    [PgName("ai_call")] AiCall,
    [PgName("field_edit")] FieldEdit,
    [PgName("field_confirm")] FieldConfirm,
    [PgName("photo_override")] PhotoOverride,
    [PgName("photo_reorder")] PhotoReorder,
    [PgName("photo_delete")] PhotoDelete,
    [PgName("finding_raised")] FindingRaised,
    [PgName("finding_acknowledged")] FindingAcknowledged,
    [PgName("content_generated")] ContentGenerated,
    [PgName("content_edited")] ContentEdited,
    [PgName("approval_granted")] ApprovalGranted,
    [PgName("approval_revoked")] ApprovalRevoked,
    [PgName("approval_rejected_hash_mismatch")] ApprovalRejectedHashMismatch,
    [PgName("membership_requested")] MembershipRequested,
    [PgName("membership_decided")] MembershipDecided,
    [PgName("registry_changed")] RegistryChanged,
    [PgName("listing_soft_deleted")] ListingSoftDeleted,
    [PgName("originals_purged")] OriginalsPurged,
}
