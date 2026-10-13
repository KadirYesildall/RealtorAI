namespace Api.Data.Entities;

// T009 — entity types for 0003_brokerages.sql and 0004_profile_license.sql.
//
// Design note that applies to every file in this folder: these types carry NO navigation
// properties. Two reasons.
//
// 1. RLS. Every query runs against a database where row-level security decides visibility, so
//    a lazy or eager navigation can silently return an empty collection where the row exists
//    but is not visible to the caller. An absent navigation forces each module to write the
//    join it means, against the policy it has reasoned about.
// 2. Authorization. FR-063 requires every endpoint to authorize independently. Convenient
//    graph traversal (listing.Brokerage.Memberships) is exactly how an authorization check
//    ends up implicit. Scalar foreign keys keep the lookup explicit and testable.
//
// jsonb columns are mapped as string. EF can map JsonDocument, but it is IDisposable and
// awkward to change-track; the modules parse these with System.Text.Json at their boundary,
// where the shape is actually known.

/// <summary>public.brokerages</summary>
public sealed class Brokerage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LicenseNumber { get; set; } = string.Empty;

    /// <summary>Two-letter US state code; <c>character(2)</c> in the database.</summary>
    public string State { get; set; } = string.Empty;

    public string? LogoStorageKey { get; set; }

    /// <summary>jsonb array. Brokerage-mandated disclaimer strings (FR-026).</summary>
    public string RequiredDisclaimers { get; set; } = "[]";

    /// <summary>jsonb object.</summary>
    public string BusinessCalendar { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// public.brokerage_memberships — the row that decides broker-admin authority.
/// <para>
/// Status and role are read from here per request and never from a JWT claim; see
/// Api/Authentication/SupabaseJwtSetup.cs for why.
/// </para>
/// </summary>
public sealed class BrokerageMembership
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid BrokerageId { get; set; }
    public MembershipStatus Status { get; set; }
    public MembershipRole Role { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>public.brokerage_settings — one row per brokerage, keyed by the brokerage id.</summary>
public sealed class BrokerageSettings
{
    public Guid BrokerageId { get; set; }
    public string? TargetMlsId { get; set; }
    public Tone DefaultTone { get; set; }
    public int AiBudgetTokens { get; set; }
    public int AiBudgetImages { get; set; }
}

/// <summary>
/// public.profiles — pre-existing table from feature 001, extended by 0004_profile_license.sql.
/// <para>
/// <c>PlatformAdmin</c> is deliberately modelled as read-only state: 0004 revokes blanket
/// UPDATE on the table and re-grants it column by column, omitting this one, and a trigger
/// rejects any change to it (FR-064). The property has a setter only because EF requires one
/// to materialise the value; writing it through this context will be refused by the database.
/// </para>
/// </summary>
public sealed class Profile
{
    public Guid Id { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? FullName { get; set; }
    public string? CompanyName { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string SubscriptionTier { get; set; } = "free";
    public int ApiUsageCount { get; set; }
    public DateTimeOffset AcceptedTermsAt { get; set; }
    public string? LicenseNumber { get; set; }

    /// <summary>Two-letter US state code; <c>character(2)</c> in the database.</summary>
    public string? LicenseState { get; set; }

    public DateTimeOffset? ProfileCompletedAt { get; set; }

    /// <summary>Not settable through the API at any privilege level — see the type remarks.</summary>
    public bool PlatformAdmin { get; set; }
}
