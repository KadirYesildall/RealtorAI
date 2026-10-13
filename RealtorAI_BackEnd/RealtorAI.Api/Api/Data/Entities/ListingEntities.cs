namespace Api.Data.Entities;

// T009 — entity types for 0005_listings.sql and 0010_mls_rule_profiles.sql. See
// BrokerageEntities.cs for the no-navigation and jsonb-as-string decisions that apply across
// this folder.

/// <summary>
/// public.listings.
/// <para>
/// <c>RowVersion</c> is an application-incremented integer, not a database-generated
/// concurrency token. The SQL migration owns it and the conditional-UPDATE helper (T040) bumps
/// it in the same statement that matches on it, so EF is deliberately NOT configured with
/// <c>IsRowVersion()</c>: that would make EF manufacture its own WHERE clause and throw
/// <c>DbUpdateConcurrencyException</c>, bypassing the <c>If-Match</c> contract in FR-066 that
/// has to be evaluated at the HTTP layer to answer 409 with problem details.
/// </para>
/// <para>
/// There is no hard delete. <c>audit_events.listing_id</c> references this table with NO
/// ACTION, so a DELETE is refused by the database once any audit row exists — which is
/// immediately, since creation itself is audited. <c>DeletedAt</c> plus the <c>archived</c>
/// status is the only removal path (FR-065).
/// </para>
/// </summary>
public sealed class Listing
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? BrokerageId { get; set; }
    public ListingStatus Status { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;

    /// <summary>Two-letter US state code; <c>character(2)</c> in the database.</summary>
    public string State { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public decimal? ListPrice { get; set; }
    public int? Bedrooms { get; set; }
    public decimal? Bathrooms { get; set; }
    public string? AgentNotes { get; set; }
    public string? TargetMlsId { get; set; }
    public Guid? CoverPhotoId { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// public.listing_fields — one RESO-keyed value per row, each carrying its own provenance.
/// <para>
/// Principle "never invent facts": <c>Provenance</c> is NOT NULL with no default, so a writer
/// must state where the value came from. A check constraint additionally rejects both AI
/// provenances on measurement and legal keys (FR-007), and <c>Confirmed</c> starts false so an
/// AI-derived value stays visibly unconfirmed until an agent affirms it.
/// </para>
/// </summary>
public sealed class ListingField
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string ResoFieldKey { get; set; } = string.Empty;

    /// <summary>jsonb. Typed per RESO field at the normalization boundary, not here.</summary>
    public string Value { get; set; } = "null";

    public Provenance Provenance { get; set; }
    public bool Confirmed { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? SourceNote { get; set; }
    public int RowVersion { get; set; }
}

/// <summary>
/// public.mls_rule_profiles — rules as data (research R2).
/// <para>
/// Every numeric limit is nullable ON PURPOSE. The seeded CRMLS row has them all NULL with
/// <c>VerifiedAt</c> NULL, so the compliance engine has no number to compare against and must
/// fail closed rather than validate against a plausible-looking guess. Filling these in is a
/// deliberate act of recording a verified rulebook, never a default.
/// </para>
/// </summary>
public sealed class MlsRuleProfile
{
    /// <summary>Text primary key, e.g. <c>crmls</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public int? PublicRemarksMaxChars { get; set; }
    public int? PrivateRemarksMaxChars { get; set; }
    public int? PhotoMinWidth { get; set; }
    public int? PhotoMinHeight { get; set; }
    public int? PhotoMinCount { get; set; }
    public int? PhotoMaxCount { get; set; }

    /// <summary>jsonb array.</summary>
    public string ForbiddenInPublicRemarks { get; set; } = "[]";

    /// <summary>jsonb array.</summary>
    public string RequiredFields { get; set; } = "[]";

    /// <summary>jsonb array.</summary>
    public string AllowedAlterationTypes { get; set; } = "[]";

    /// <summary>jsonb array.</summary>
    public string DisclosureLabels { get; set; } = "[]";

    /// <summary>jsonb object.</summary>
    public string EnumeratedFieldLookups { get; set; } = "{}";

    public string? RulebookVersion { get; set; }

    /// <summary>NULL means "no verified rulebook" — the engine must treat the profile as unusable.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
}
