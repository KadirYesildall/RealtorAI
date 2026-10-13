using Libs.Ai;
using Modules.Audit;

namespace Api.Data.Entities;

// T009 — entity types for 0008_ai_platform.sql and 0009_audit.sql. See BrokerageEntities.cs
// for the no-navigation and jsonb-as-string decisions that apply across this folder.

/// <summary>
/// public.ai_model_registry — the single place a provider, model id, or endpoint may appear
/// (Principle VIII.1).
/// <para>
/// A check constraint enforces VIII.4 directly: <c>Enabled</c> implies <c>License</c> is
/// present and <c>EvalStatus</c> is <c>passed</c>. That makes "we turned on a model we had not
/// licensed or evaluated" unrepresentable rather than merely discouraged, and it holds against
/// a service-role connection as well as an API caller. A partial unique index additionally
/// allows at most one enabled row per <c>TaskKey</c>, so dispatch is never ambiguous.
/// </para>
/// <para>
/// All seven seeded rows are <c>enabled = false</c>, <c>eval_status = 'unverified'</c>. That is
/// the correct starting state, not an oversight: enabling one is an act of recording a license
/// and an evaluation result.
/// </para>
/// </summary>
public sealed class AiModelRegistryEntry
{
    public Guid Id { get; set; }

    /// <summary>Logical capability the caller asks for, e.g. <c>photo.room_type</c>.</summary>
    public string TaskKey { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? Endpoint { get; set; }

    /// <summary>jsonb object of provider call parameters.</summary>
    public string Parameters { get; set; } = "{}";

    /// <summary>Registry row to try when this one fails or trips its circuit breaker.</summary>
    public Guid? FallbackRegistryId { get; set; }

    /// <summary>SPDX-style license identifier; required before <c>Enabled</c> may be true.</summary>
    public string? License { get; set; }

    public ModelEvalStatus EvalStatus { get; set; }

    /// <summary>jsonb. Evaluation-harness output backing <c>EvalStatus</c>.</summary>
    public string? EvalResult { get; set; }

    public bool Enabled { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

/// <summary>
/// public.prompt_versions — versioned prompt plus the JSON Schema its output must satisfy
/// (Principle VIII.5).
/// <para>
/// <c>PromptHash</c> exists so prompt drift cannot go unnoticed: startup recomputes the hash of
/// each on-disk prompt file and fails if it disagrees with the active row (T046). Without that,
/// an edited prompt would quietly invalidate every audit record that cites its version.
/// </para>
/// </summary>
public sealed class PromptVersion
{
    public Guid Id { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public string PromptHash { get; set; } = string.Empty;

    /// <summary>jsonb. The schema used to validate model output before it is persisted.</summary>
    public string OutputSchema { get; set; } = "{}";

    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.ai_usage — per-call accounting behind the FR-055 budget check.
/// <para>
/// <c>CostUsd</c> is nullable because provider pricing has not been verified (research R16);
/// budgets are enforced in tokens and images, which are measured directly. A fabricated dollar
/// figure would be worse than none.
/// </para>
/// </summary>
public sealed class AiUsage
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public int ImageCount { get; set; }

    /// <summary>NULL until provider pricing is verified — see the type remarks.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>True when the result came from the input-hash cache and cost nothing.</summary>
    public bool CacheHit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.audit_events — append-only (FR-049).
/// <para>
/// Immutability is enforced in the database, not here: 0009 grants only SELECT and INSERT, and
/// a statement-level BEFORE UPDATE OR DELETE trigger raises unconditionally. The trigger is
/// what makes this hold against a superuser or service-role connection, which the grants alone
/// would not.
/// </para>
/// <para>
/// This entity is therefore mapped insert-only by convention. The writer (T042) never updates
/// an audit row, and attempting it would fail at the database rather than silently succeed.
/// </para>
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }

    /// <summary>
    /// Nullable, and referencing <c>listings</c> with NO ACTION. That combination is what makes
    /// a listing undeletable while any audit row cites it — the audit trail wins over cleanup.
    /// </summary>
    public Guid? ListingId { get; set; }

    public Guid? BrokerageId { get; set; }

    /// <summary>NULL only for system-initiated actions such as the out-of-band originals purge.</summary>
    public Guid? ActorId { get; set; }

    public AuditAction Action { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>jsonb. State before the change, for edits.</summary>
    public string? BeforeValue { get; set; }

    /// <summary>jsonb. State after the change.</summary>
    public string? AfterValue { get; set; }

    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public string? PromptHash { get; set; }

    /// <summary>Storage key of the exact input sent to a model, for <c>ai_call</c> events.</summary>
    public string? InputRef { get; set; }

    public string? OutputRef { get; set; }

    /// <summary>Hash tying this event to the payload it describes.</summary>
    public string? PayloadHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
