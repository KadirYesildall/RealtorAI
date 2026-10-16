using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace Api.Data;

/// <summary>
/// One CLR enum mapped to one PostgreSQL enum type.
/// </summary>
/// <param name="ClrType">The CLR enum.</param>
/// <param name="PgName">The PostgreSQL enum type name in <c>public</c>.</param>
/// <param name="ExplicitLabels">
/// CLR member name to database label, for enums that cannot carry <c>[PgName]</c>.
/// Null for enums declared in this assembly, which use the attribute instead.
/// </param>
internal sealed record PostgresEnumMapping(
    Type ClrType,
    string PgName,
    IReadOnlyDictionary<string, string>? ExplicitLabels)
{
    /// <summary>
    /// The database labels this mapping expects, whichever way they were declared. Used both to
    /// configure Npgsql and, by SchemaDriftTests, to compare against the live database — one
    /// source, so the test cannot agree with a mapping that is itself wrong.
    /// </summary>
    internal IReadOnlyList<string> ExpectedLabels()
    {
        if (ExplicitLabels is not null)
        {
            return ExplicitLabels.Values.ToList();
        }

        return ClrType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field =>
                field.GetCustomAttribute<PgNameAttribute>()?.PgName
                ?? throw new InvalidOperationException(
                    $"{ClrType.Name}.{field.Name} has no [PgName]. Enums in Api.Data must "
                    + "declare their database label explicitly."))
            .ToList();
    }

    // Created eagerly, with the mapping, so there is no window in which two requests race to
    // create two instances.
    private readonly INpgsqlNameTranslator? translator = ExplicitLabels is null
        ? null
        : new ExplicitEnumLabelTranslator(PgName, ExplicitLabels);

    /// <summary>
    /// The translator Npgsql needs, or null when <c>[PgName]</c> already supplies the labels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One instance per mapping, for the life of the process — this is load-bearing.</b>
    /// <c>AddDbContext</c> rebuilds the options for every scope, i.e. every request, and EF keys
    /// its internal service provider on those options, comparing each enum mapping's translator
    /// by reference. A fresh translator per call therefore made every request look like a new
    /// configuration: EF built a new internal service provider each time and, past twenty,
    /// raised <c>ManyServiceProvidersCreatedWarning</c> as an error — so the twenty-first
    /// database-backed request after startup, and every one after it, failed with a 500.
    /// </para>
    /// <para>
    /// Found by the first HTTP contract suite (T051), the first code to send more than a handful
    /// of database requests through one host. See research R26.
    /// </para>
    /// </remarks>
    internal INpgsqlNameTranslator? Translator() => translator;
}

/// <summary>
/// Maps CLR member names to database labels from an explicit table.
///
/// <para>
/// Exists so an enum owned by a domain module can be mapped to a PostgreSQL enum without that
/// module referencing Npgsql. The alternative — decorating the domain enum with
/// <c>[PgName]</c> — would pull the database driver into modules that have no business knowing
/// a database exists, and duplicating the enum on both sides of the boundary would invite the
/// two copies to drift.
/// </para>
/// <para>
/// Unknown members throw rather than falling back to a naming convention. A convention that
/// silently produced <c>ai_call</c> from <c>AiCall</c> would also silently produce something
/// wrong from a member it had not anticipated, and an audit action written under the wrong
/// label is a corrupted record rather than a cosmetic problem.
/// </para>
/// </summary>
internal sealed class ExplicitEnumLabelTranslator : INpgsqlNameTranslator
{
    private readonly string typeName;
    private readonly IReadOnlyDictionary<string, string> memberLabels;

    internal ExplicitEnumLabelTranslator(
        string typeName,
        IReadOnlyDictionary<string, string> memberLabels)
    {
        this.typeName = typeName;
        this.memberLabels = memberLabels;
    }

    public string TranslateTypeName(string clrName) => typeName;

    public string TranslateMemberName(string clrName) =>
        memberLabels.TryGetValue(clrName, out var label)
            ? label
            : throw new InvalidOperationException(
                $"No database label is declared for '{clrName}' in the mapping for "
                + $"'{typeName}'. Add it to Api.Data.PostgresEnumMappings together with the "
                + "migration that adds the value to the PostgreSQL enum.");
}

/// <summary>
/// Every PostgreSQL enum this model maps.
///
/// <para>
/// Two kinds appear here. Enums whose values are purely a persistence concern are declared in
/// <c>Api/Data/DbEnums.cs</c> with <c>[PgName]</c>. Enums that are domain vocabulary —
/// membership status and role, and the audit action list — are declared in the module that
/// owns them, and their labels are listed below. That keeps one definition of each concept
/// while leaving the database spelling where the database work happens.
/// </para>
/// </summary>
internal static class PostgresEnumMappings
{
    internal static IReadOnlyList<PostgresEnumMapping> All { get; } =
    [
        // ── Domain-owned (declared in modules, labels declared here) ──
        new(typeof(Modules.Brokerage.MembershipStatus), "membership_status", new Dictionary<string, string>
        {
            ["Pending"] = "pending",
            ["Approved"] = "approved",
            ["Rejected"] = "rejected",
            ["Revoked"] = "revoked",
        }),

        new(typeof(Modules.Brokerage.MembershipRole), "membership_role", new Dictionary<string, string>
        {
            ["Agent"] = "agent",
            ["BrokerAdmin"] = "broker_admin",
        }),

        new(typeof(Libs.Ai.ModelEvalStatus), "model_eval_status", new Dictionary<string, string>
        {
            ["Unverified"] = "unverified",
            ["Passed"] = "passed",
            ["Failed"] = "failed",
        }),

        new(typeof(Modules.Audit.AuditAction), "audit_action", new Dictionary<string, string>
        {
            ["AiCall"] = "ai_call",
            ["FieldEdit"] = "field_edit",
            ["FieldConfirm"] = "field_confirm",
            ["PhotoOverride"] = "photo_override",
            ["PhotoReorder"] = "photo_reorder",
            ["PhotoDelete"] = "photo_delete",
            ["FindingRaised"] = "finding_raised",
            ["FindingAcknowledged"] = "finding_acknowledged",
            ["ContentGenerated"] = "content_generated",
            ["ContentEdited"] = "content_edited",
            ["ApprovalGranted"] = "approval_granted",
            ["ApprovalRevoked"] = "approval_revoked",
            ["ApprovalRejectedHashMismatch"] = "approval_rejected_hash_mismatch",
            ["MembershipRequested"] = "membership_requested",
            ["MembershipDecided"] = "membership_decided",
            ["RegistryChanged"] = "registry_changed",
            ["ListingSoftDeleted"] = "listing_soft_deleted",
            ["OriginalsPurged"] = "originals_purged",
        }),

        new(typeof(Modules.Listings.ListingStatus), "listing_status", new Dictionary<string, string>
        {
            ["Draft"] = "draft",
            ["Analyzing"] = "analyzing",
            ["ReadyForReview"] = "ready_for_review",
            ["Approved"] = "approved",
            ["Archived"] = "archived",
        }),

        new(typeof(Modules.Listings.Provenance), "provenance", new Dictionary<string, string>
        {
            ["AgentEntered"] = "agent_entered",
            ["PublicRecord"] = "public_record",
            ["AiDetectedFromPhoto"] = "ai_detected_from_photo",
            ["AiSuggested"] = "ai_suggested",
        }),

        // ── Persistence-owned (labels come from [PgName]) ──
        new(typeof(Tone), "tone", null),
        new(typeof(AlterationType), "alteration_type", null),
        new(typeof(RoomType), "room_type", null),
        new(typeof(ContentKind), "content_kind", null),
        new(typeof(ContentStatus), "content_status", null),
        new(typeof(CheckType), "check_type", null),
        new(typeof(FindingSeverity), "finding_severity", null),
        new(typeof(FindingSource), "finding_source", null),
        new(typeof(FindingStatus), "finding_status", null),

    ];
}
