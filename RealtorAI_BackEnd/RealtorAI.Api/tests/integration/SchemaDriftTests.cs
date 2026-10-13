using System.Globalization;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// T033 — the EF Core model agrees with the schema the SQL migrations actually created.
///
/// <para>
/// This test is what makes "EF migrations are disabled" a workable arrangement rather than a
/// liability. Normally EF's own migration history detects a model that has drifted from the
/// database. Here the SQL files own all DDL (research R14), so EF has no snapshot to compare
/// against and a mismatch would otherwise surface as a runtime error on whichever endpoint
/// first touched the renamed column — possibly in production, possibly months later.
/// </para>
/// <para>
/// Drift is checked in both directions. A model column with no database column is the obvious
/// case; a database column absent from the model is the quieter one, and it is how a migration
/// that adds a NOT NULL column without a default breaks every insert the API makes.
/// </para>
/// </summary>
public sealed class SchemaDriftTests
{

    private sealed record Column(string Table, string Name, string Type, bool IsNullable);

    private static async Task<Dictionary<string, List<Column>>> ReadActualSchemaAsync()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        // format_type gives the canonical spelling with its modifier — numeric(12,2),
        // character(2), vector(768) — which is what EF's column types can be compared against.
        // information_schema.columns splits that across several columns and loses the enum
        // name entirely, reporting only "USER-DEFINED".
        await using var command = new NpgsqlCommand(
            """
            select c.relname,
                   a.attname,
                   format_type(a.atttypid, a.atttypmod),
                   not a.attnotnull
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relkind = 'r'
              and a.attnum > 0
              and not a.attisdropped
            order by c.relname, a.attnum
            """,
            connection);

        var byTable = new Dictionary<string, List<Column>>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);

            if (!byTable.TryGetValue(table, out var columns))
            {
                columns = [];
                byTable[table] = columns;
            }

            columns.Add(new Column(
                table,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return byTable;
    }

    [Fact]
    public async Task EveryMappedTableExists()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var actual = await ReadActualSchemaAsync();

        var missing = context.Model.GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(table => table is not null && !actual.ContainsKey(table))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The model maps tables that do not exist: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task EveryMappedColumnExists()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var actual = await ReadActualSchemaAsync();

        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();

            if (table is null || !actual.TryGetValue(table, out var actualColumns))
            {
                continue;
            }

            var actualNames = actualColumns
                .Select(column => column.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();

                if (!actualNames.Contains(columnName))
                {
                    problems.Add($"{table}.{columnName} (from {entityType.ClrType.Name}.{property.Name})");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Model columns with no matching database column:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public async Task NoDatabaseColumnIsMissingFromTheModel()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var actual = await ReadActualSchemaAsync();

        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();

            if (table is null || !actual.TryGetValue(table, out var actualColumns))
            {
                continue;
            }

            var mapped = entityType.GetProperties()
                .Select(property => property.GetColumnName())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var column in actualColumns.Where(column => !mapped.Contains(column.Name)))
            {
                problems.Add($"{table}.{column.Name} ({column.Type})");
            }
        }

        Assert.True(
            problems.Count == 0,
            "Database columns absent from the EF model — an insert through this context will "
            + "omit them, which fails if they are NOT NULL without a default:\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public async Task ColumnTypesAgree()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var actual = await ReadActualSchemaAsync();

        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();

            if (table is null || !actual.TryGetValue(table, out var actualColumns))
            {
                continue;
            }

            var actualByName = actualColumns.ToDictionary(
                column => column.Name,
                StringComparer.Ordinal);

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();

                if (!actualByName.TryGetValue(columnName, out var column))
                {
                    continue;
                }

                var modelType = Normalize(property.GetColumnType());
                var databaseType = Normalize(column.Type);

                if (!string.Equals(modelType, databaseType, StringComparison.Ordinal))
                {
                    problems.Add($"{table}.{columnName}: model={modelType} database={databaseType}");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Column type mismatches between the EF model and the database:\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public async Task ColumnNullabilityAgrees()
    {
        await using var context = SupabaseDatabase.CreateContext();
        var actual = await ReadActualSchemaAsync();

        var problems = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();

            if (table is null || !actual.TryGetValue(table, out var actualColumns))
            {
                continue;
            }

            var actualByName = actualColumns.ToDictionary(
                column => column.Name,
                StringComparer.Ordinal);

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();

                if (!actualByName.TryGetValue(columnName, out var column))
                {
                    continue;
                }

                // Only one direction is a defect worth failing on: the model believing a
                // column is NOT NULL when the database allows null means reads can produce a
                // null in a non-nullable CLR property, which throws far from the cause. The
                // reverse — a nullable model property over a NOT NULL column — is merely
                // conservative, and is legitimate where the database supplies a default.
                if (column.IsNullable && !property.IsNullable)
                {
                    problems.Add(
                        $"{table}.{columnName} is nullable in the database but "
                        + $"{entityType.ClrType.Name}.{property.Name} is not");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Nullability mismatches:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Every PostgreSQL enum the model maps exists, with exactly the labels the CLR enum
    /// declares. A label added in SQL but not in C# produces an unmapped-value exception the
    /// first time a row carries it; a label added in C# but not in SQL fails on write. Both are
    /// cheap to catch here.
    /// </summary>
    [Fact]
    public async Task MappedPostgresEnums_HaveExactlyTheExpectedLabels()
    {
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        var problems = new List<string>();

        foreach (var mapping in Api.Data.PostgresEnumMappings.All)
        {
            await using var command = new NpgsqlCommand(
                """
                select e.enumlabel
                from pg_enum e
                join pg_type t on t.oid = e.enumtypid
                join pg_namespace n on n.oid = t.typnamespace
                where n.nspname = 'public' and t.typname = $1
                order by e.enumsortorder
                """,
                connection);

            command.Parameters.AddWithValue(mapping.PgName);

            var databaseLabels = new List<string>();

            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    databaseLabels.Add(reader.GetString(0));
                }
            }

            if (databaseLabels.Count == 0)
            {
                problems.Add($"public.{mapping.PgName} does not exist (mapped from {mapping.ClrType.Name})");
                continue;
            }

            // Expected labels come from the same mapping Npgsql is configured with, so this
            // compares the database against what the application will actually send — not
            // against a second list that could be wrong in the same way.
            var expected = mapping.ExpectedLabels();

            var onlyInDatabase = databaseLabels.Except(expected, StringComparer.Ordinal).ToList();
            var onlyInClr = expected.Except(databaseLabels, StringComparer.Ordinal).ToList();

            if (onlyInDatabase.Count > 0)
            {
                problems.Add(
                    $"{mapping.PgName}: in database but not mapped from "
                    + $"{mapping.ClrType.Name}: {string.Join(", ", onlyInDatabase)}");
            }

            if (onlyInClr.Count > 0)
            {
                problems.Add(
                    $"{mapping.PgName}: mapped from {mapping.ClrType.Name} but absent from the "
                    + $"database: {string.Join(", ", onlyInClr)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// Every CLR member of every mapped enum resolves to a label. A member added to a
    /// domain-owned enum without a corresponding entry in the explicit label table would
    /// otherwise throw at runtime, on whichever write first carried that value — which for a
    /// rare audit action could be a long way from the change that caused it.
    /// </summary>
    [Fact]
    public void EveryMappedEnumMemberHasALabel()
    {
        var problems = new List<string>();

        foreach (var mapping in Api.Data.PostgresEnumMappings.All)
        {
            var clrMembers = Enum.GetNames(mapping.ClrType);

            IReadOnlyList<string> labels;

            try
            {
                labels = mapping.ExpectedLabels();
            }
            catch (InvalidOperationException exception)
            {
                problems.Add($"{mapping.ClrType.Name}: {exception.Message}");
                continue;
            }

            if (labels.Count != clrMembers.Length)
            {
                problems.Add(
                    $"{mapping.ClrType.Name} has {clrMembers.Length} members but "
                    + $"{labels.Count} labels are declared for {mapping.PgName}.");
            }

            if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Count)
            {
                problems.Add(
                    $"{mapping.PgName} has duplicate labels, so two CLR members would write "
                    + "the same database value.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// The migrator guard actually guards. Without this, the replacement could be dropped from
    /// the options registration and nothing would notice until EF generated DDL that silently
    /// removed the RLS policies and triggers the tests above depend on.
    /// </summary>
    [Fact]
    public async Task CallingMigrate_ThrowsInsteadOfGeneratingDdl()
    {
        await using var context = SupabaseDatabase.CreateContext();

        var exception = Assert.Throws<NotSupportedException>(() => context.Database.Migrate());

        Assert.Contains("supabase/migrations", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalises the two spellings of a type so only real differences are reported: EF may
    /// qualify an enum with its schema, and whitespace inside a modifier is not meaningful.
    /// </summary>
    private static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "<none>";
        }

        var normalized = type.Trim().ToLower(CultureInfo.InvariantCulture).Replace(" ,", ",", StringComparison.Ordinal);

        normalized = normalized.StartsWith("public.", StringComparison.Ordinal)
            ? normalized["public.".Length..]
            : normalized;

        return normalized.Replace(", ", ",", StringComparison.Ordinal);
    }
}
