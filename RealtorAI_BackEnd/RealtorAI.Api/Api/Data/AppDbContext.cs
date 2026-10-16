using System.Globalization;
using System.Text;
using Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Api.Data;

/// <summary>
/// T009 — EF Core model over the Supabase-owned schema.
///
/// <para>
/// <b>EF migrations are disabled, by design and by enforcement.</b> Research R14: the SQL files
/// in <c>RealtorAI_BackEnd/supabase/migrations/</c> own all DDL. They have to, because the
/// guarantees this feature depends on are things EF cannot express — row-level security
/// policies, <c>SECURITY DEFINER</c> helper functions, per-column GRANTs, partial unique
/// indexes, statement-level triggers, and the check constraints that make states like
/// "enabled but unlicensed model" unrepresentable. If EF were allowed to generate schema, a
/// scaffolded migration would quietly drop every one of them and the build would still pass.
/// </para>
///
/// <para>
/// Three things keep that from happening rather than just asking nicely:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <c>Microsoft.EntityFrameworkCore.Design</c> is not referenced, so
///       <c>dotnet ef migrations add</c> cannot run against this project at all.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="MigrationsAreOwnedBySqlMigrator"/> replaces EF's <c>IMigrator</c>, so
///       <c>Database.Migrate()</c> throws with an explanation instead of mutating the schema.
///     </description>
///   </item>
///   <item>
///     <description>
///       SchemaDriftTests (T033) compares this model against the live database, so the model
///       drifting away from the SQL is a test failure rather than a production surprise.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// This context connects with a privileged role that <b>bypasses RLS</b>. That is precisely why
/// FR-063 requires every endpoint to authorize independently: the database is a second line of
/// defence here, not the only one. Nothing in this class should be read as "the database will
/// catch it".
/// </para>
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Brokerage> Brokerages => Set<Brokerage>();

    public DbSet<BrokerageMembership> BrokerageMemberships => Set<BrokerageMembership>();

    public DbSet<BrokerageSettings> BrokerageSettings => Set<BrokerageSettings>();

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Listing> Listings => Set<Listing>();

    public DbSet<ListingField> ListingFields => Set<ListingField>();

    public DbSet<MlsRuleProfile> MlsRuleProfiles => Set<MlsRuleProfile>();

    public DbSet<Photo> Photos => Set<Photo>();

    public DbSet<PhotoAnalysis> PhotoAnalyses => Set<PhotoAnalysis>();

    public DbSet<PhotoEmbedding> PhotoEmbeddings => Set<PhotoEmbedding>();

    public DbSet<ContentDraft> ContentDrafts => Set<ContentDraft>();

    public DbSet<ComplianceFinding> ComplianceFindings => Set<ComplianceFinding>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<AiModelRegistryEntry> AiModelRegistry => Set<AiModelRegistryEntry>();

    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();

    public DbSet<AiUsage> AiUsage => Set<AiUsage>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

    public DbSet<PipelineStepState> PipelineStepStates => Set<PipelineStepState>();

    public DbSet<PipelinePhotoState> PipelinePhotoStates => Set<PipelinePhotoState>();

    /// <summary>
    /// Npgsql provider configuration shared by the host and by tests, so a test never runs
    /// against a differently configured model than production does.
    /// </summary>
    public static void ConfigureNpgsql(
        Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var mapping in PostgresEnumMappings.All)
        {
            builder.MapEnum(mapping.ClrType, mapping.PgName, "public", mapping.Translator());
        }

        // pgvector 0.8.2 is installed by 0002_pgvector.sql; this teaches EF the vector type.
        builder.UseVector();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        // ───────────────── Column naming ─────────────────
        //
        // Applied FIRST, so any explicit HasColumnName added later in this method wins over
        // the derived name. The schema is uniformly snake_case; SchemaDriftTests (T033) proves
        // this derivation matches every column that actually exists rather than assuming it.
        ApplySnakeCaseColumnNames(modelBuilder);

        // ───────────────── Table names ─────────────────
        //
        // Spelled out rather than pluralised by convention. The real names are irregular
        // enough that no convention gets them all right — photo_analyses, brokerage_settings,
        // ai_usage, and ai_model_registry each break a different rule — and a wrong table name
        // is the kind of error that only shows up at runtime.
        modelBuilder.Entity<Brokerage>().ToTable("brokerages");
        modelBuilder.Entity<BrokerageMembership>().ToTable("brokerage_memberships");
        modelBuilder.Entity<BrokerageSettings>().ToTable("brokerage_settings");
        modelBuilder.Entity<Profile>().ToTable("profiles");
        modelBuilder.Entity<Listing>().ToTable("listings");
        modelBuilder.Entity<ListingField>().ToTable("listing_fields");
        modelBuilder.Entity<MlsRuleProfile>().ToTable("mls_rule_profiles");
        modelBuilder.Entity<Photo>().ToTable("photos");
        modelBuilder.Entity<PhotoAnalysis>().ToTable("photo_analyses");
        modelBuilder.Entity<PhotoEmbedding>().ToTable("photo_embeddings");
        modelBuilder.Entity<ContentDraft>().ToTable("content_drafts");
        modelBuilder.Entity<ComplianceFinding>().ToTable("compliance_findings");
        modelBuilder.Entity<Approval>().ToTable("approvals");
        modelBuilder.Entity<AiModelRegistryEntry>().ToTable("ai_model_registry");
        modelBuilder.Entity<PromptVersion>().ToTable("prompt_versions");
        modelBuilder.Entity<AiUsage>().ToTable("ai_usage");
        modelBuilder.Entity<AuditEvent>().ToTable("audit_events");
        modelBuilder.Entity<PipelineRun>().ToTable("pipeline_runs");
        modelBuilder.Entity<PipelineStepState>().ToTable("pipeline_step_states");
        modelBuilder.Entity<PipelinePhotoState>().ToTable("pipeline_photo_states");

        // ───────────────── Non-default primary keys ─────────────────
        modelBuilder.Entity<BrokerageSettings>().HasKey(e => e.BrokerageId);
        modelBuilder.Entity<PhotoEmbedding>().HasKey(e => e.PhotoId);
        modelBuilder.Entity<MlsRuleProfile>().HasKey(e => e.Id);
        modelBuilder.Entity<PipelineStepState>().HasKey(e => new { e.RunId, e.Step });
        modelBuilder.Entity<PipelinePhotoState>().HasKey(e => new { e.RunId, e.PhotoId });

        // mls_rule_profiles.id is a human-readable text key ("crmls"), supplied by the
        // migration that records a verified rulebook. EF must not try to generate it.
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.Id).ValueGeneratedNever();

        // ───────────────── Column types the convention cannot infer ─────────────────
        ConfigureJsonbColumns(modelBuilder);
        ConfigureFixedWidthAndPrecision(modelBuilder);
        ConfigureDatabaseClockDefaults(modelBuilder);

        modelBuilder.Entity<PhotoEmbedding>()
            .Property(e => e.Embedding)
            .HasColumnType("vector(768)");

    }

    /// <summary>
    /// Last-resort in-process guards. Each of these is already enforced by the database; they
    /// are repeated here so a mistake fails with a sentence explaining the rule instead of a
    /// raw <c>PostgresException</c> from a trigger, and so it fails before a round trip.
    /// <para>
    /// These are not the enforcement. Deleting this method must not make any prohibited write
    /// succeed — the integration tests in tests/integration verify the database refuses them
    /// independently of this code.
    /// </para>
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAgainstProhibitedWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAgainstProhibitedWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static void ConfigureJsonbColumns(ModelBuilder modelBuilder)
    {
        // jsonb, mapped as string (see Entities/BrokerageEntities.cs for why). Without an
        // explicit column type EF would map these as `text` and the provider would refuse the
        // parameter, so each one is listed.
        modelBuilder.Entity<Brokerage>().Property(e => e.RequiredDisclaimers).HasColumnType("jsonb");
        modelBuilder.Entity<Brokerage>().Property(e => e.BusinessCalendar).HasColumnType("jsonb");
        modelBuilder.Entity<ListingField>().Property(e => e.Value).HasColumnType("jsonb");
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.ForbiddenInPublicRemarks).HasColumnType("jsonb");
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.RequiredFields).HasColumnType("jsonb");
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.AllowedAlterationTypes).HasColumnType("jsonb");
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.DisclosureLabels).HasColumnType("jsonb");
        modelBuilder.Entity<MlsRuleProfile>().Property(e => e.EnumeratedFieldLookups).HasColumnType("jsonb");
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.FeatureTags).HasColumnType("jsonb");
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.QualityFlags).HasColumnType("jsonb");
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.ContentFlags).HasColumnType("jsonb");
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.RawOutput).HasColumnType("jsonb");
        modelBuilder.Entity<ContentDraft>().Property(e => e.SupportingFieldIds).HasColumnType("jsonb");
        modelBuilder.Entity<AiModelRegistryEntry>().Property(e => e.Parameters).HasColumnType("jsonb");
        modelBuilder.Entity<AiModelRegistryEntry>().Property(e => e.EvalResult).HasColumnType("jsonb");
        modelBuilder.Entity<PromptVersion>().Property(e => e.OutputSchema).HasColumnType("jsonb");
        modelBuilder.Entity<AuditEvent>().Property(e => e.BeforeValue).HasColumnType("jsonb");
        modelBuilder.Entity<AuditEvent>().Property(e => e.AfterValue).HasColumnType("jsonb");
        modelBuilder.Entity<Photo>().Property(e => e.FeatureTagsOverride).HasColumnType("jsonb");
    }

    /// <summary>
    /// The SQL expression every timestamp default in the migrations uses.
    /// </summary>
    private const string DatabaseClock = "timezone('utc'::text, now())";

    /// <summary>
    /// Timestamps the database stamps on insert (research R31).
    ///
    /// <para>
    /// Declaring the default is what stops EF sending the property's CLR value. Without it EF
    /// writes <c>default(DateTimeOffset)</c>, which Npgsql stores as <c>-infinity</c>, and the
    /// database's <c>now()</c> never applies — which is how every audit row the API wrote
    /// before this existed ended up unable to say when it happened. With it, EF omits the
    /// column while the property holds its default and reads the stamped value back.
    /// </para>
    /// <para>
    /// One clock for every record is the point (see <c>EfAuditSink</c>): application servers
    /// disagree about the time, and a trail ordered by a mix of their clocks can show effects
    /// before causes. <c>ColumnDefaultsTests</c> fails if a migration adds a <c>now()</c>
    /// default that is not listed here.
    /// </para>
    /// </summary>
    private static void ConfigureDatabaseClockDefaults(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Brokerage>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<BrokerageMembership>().Property(e => e.RequestedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Profile>().Property(e => e.UpdatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Profile>().Property(e => e.AcceptedTermsAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Listing>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Listing>().Property(e => e.UpdatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Photo>().Property(e => e.UploadedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<ContentDraft>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<ComplianceFinding>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<Approval>().Property(e => e.ApprovedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<PromptVersion>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<AiUsage>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<AuditEvent>().Property(e => e.CreatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<PipelineRun>().Property(e => e.StartedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<PipelineStepState>().Property(e => e.UpdatedAt).HasDefaultValueSql(DatabaseClock);
        modelBuilder.Entity<PipelinePhotoState>().Property(e => e.UpdatedAt).HasDefaultValueSql(DatabaseClock);
    }

    private static void ConfigureFixedWidthAndPrecision(ModelBuilder modelBuilder)
    {
        // character(2) state codes. Declared so a three-letter value fails on write rather
        // than being silently accepted into a model that thinks the column is unbounded text.
        modelBuilder.Entity<Brokerage>().Property(e => e.State).HasColumnType("character(2)");
        modelBuilder.Entity<Listing>().Property(e => e.State).HasColumnType("character(2)");
        modelBuilder.Entity<Profile>().Property(e => e.LicenseState).HasColumnType("character(2)");

        modelBuilder.Entity<Profile>().Property(e => e.PreferredLanguage).HasMaxLength(5);
        modelBuilder.Entity<Profile>().Property(e => e.SubscriptionTier).HasMaxLength(20);

        // Money and measurements. Precision is declared rather than defaulted because these
        // are the values FR-007 forbids AI from originating — a silent rounding change in a
        // price or a bathroom count is exactly the class of error that principle exists for.
        modelBuilder.Entity<Listing>().Property(e => e.ListPrice).HasPrecision(12, 2);
        modelBuilder.Entity<Listing>().Property(e => e.Bathrooms).HasPrecision(3, 1);
        modelBuilder.Entity<AiUsage>().Property(e => e.CostUsd).HasPrecision(10, 4);
        modelBuilder.Entity<PhotoAnalysis>().Property(e => e.RoomTypeConfidence).HasPrecision(4, 3);
    }

    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (char.IsUpper(current))
            {
                // Underscore only at a lower-to-upper or digit-to-upper boundary, so an
                // acronym stays together: ChecksumSha256 -> checksum_sha256, not
                // checksum_s_h_a256.
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLower(current, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private void GuardAgainstProhibitedWrites()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                // FR-049. The database enforces this with a statement-level trigger that
                // raises for any UPDATE or DELETE, including from a superuser.
                case AuditEvent when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw new InvalidOperationException(
                        "audit_events is append-only (FR-049). An audit record cannot be "
                        + "modified or deleted. Record a corrective event instead.");

                // FR-065. listings has no hard-delete path: audit_events.listing_id
                // references it with NO ACTION, so the database refuses the DELETE anyway.
                case Listing when entry.State is EntityState.Deleted:
                    throw new InvalidOperationException(
                        "listings cannot be hard-deleted (FR-065). Set deleted_at and the "
                        + "archived status; originals are purged out of band after 3 years.");

                // FR-064. profiles.platform_admin has no UPDATE grant and a trigger rejecting
                // any change; this catches the attempt before it reaches either.
                case Profile when entry.State is EntityState.Modified
                    && entry.Property(nameof(Profile.PlatformAdmin)).IsModified:
                    throw new InvalidOperationException(
                        "profiles.platform_admin is not settable through the application "
                        + "(FR-064). It is granted out of band by a database operator.");

                default:
                    break;
            }
        }
    }
}

/// <summary>
/// Replacement for EF's <c>IMigrator</c> that refuses to do anything.
/// <para>
/// Registered via <c>ReplaceService</c> so a call to <c>Database.Migrate()</c> — the one line
/// someone reasonably adds to startup to "make sure the schema is up to date" — fails loudly
/// with the reason instead of generating DDL that would drop the RLS policies, triggers, and
/// check constraints this feature's guarantees rest on.
/// </para>
/// </summary>
internal sealed class MigrationsAreOwnedBySqlMigrator : IMigrator
{
    private const string Explanation =
        "EF Core migrations are disabled in this project (research R14). All DDL lives in "
        + "RealtorAI_BackEnd/supabase/migrations/*.sql, which EF cannot express: RLS policies, "
        + "SECURITY DEFINER functions, per-column GRANTs, partial unique indexes, and "
        + "statement-level triggers would all be lost. Apply schema changes by adding a "
        + "numbered SQL migration and running `supabase db reset`.";

    public void Migrate(string? targetMigration = null)
        => throw new NotSupportedException(Explanation);

    public Task MigrateAsync(string? targetMigration = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(Explanation);

    public string GenerateScript(
        string? fromMigration = null,
        string? toMigration = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
        => throw new NotSupportedException(Explanation);

    /// <summary>
    /// Always false. EF asks this to warn about un-applied model changes; since EF does not own
    /// the schema, the honest answer is that it has no pending migrations to apply. Real
    /// model-versus-schema divergence is detected by SchemaDriftTests (T033), which compares
    /// against the live database rather than against a migration snapshot.
    /// </summary>
    public bool HasPendingModelChanges() => false;
}
