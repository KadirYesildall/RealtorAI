using System.Globalization;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace RealtorAI.Tests.Integration;

/// <summary>
/// Every column default in the database agrees with the CLR default of the property mapped to
/// it.
///
/// <para>
/// <b>Written in response to a real bug.</b> <c>listings.row_version</c> defaults to 1 in SQL,
/// but the CLR property defaulted to 0 — and EF always sends a value on insert rather than
/// omitting the column, so the database default never applied. Every listing created through
/// the context started at version 0 while the schema said 1, which made the first conditional
/// update fail with a version conflict against a version that had never existed.
/// </para>
/// <para>
/// The same latent fault sat on <c>brokerage_settings.ai_budget_tokens</c> (200000 in SQL,
/// 0 in CLR), where the symptom would have been worse and quieter: a new brokerage with a zero
/// AI budget, so the FR-055 pre-dispatch check would refuse every AI call and the feature would
/// appear broken for exactly the brokerages nobody had configured by hand.
/// </para>
/// <para>
/// So this test exists to catch the class, not the instance. It reads every defaulted column
/// from the live schema and compares it against a freshly constructed entity, which means a
/// future migration that adds a default — or changes one — fails here unless the entity is
/// updated with it.
/// </para>
/// </summary>
public sealed class ColumnDefaultsTests
{

    [Fact]
    public async Task EveryDefaultedColumnMatchesTheClrPropertyDefault()
    {
        await using var context = SupabaseDatabase.CreateContext();
        await using var connection = await SupabaseDatabase.OpenSuperuserAsync();

        // Server-generated defaults are excluded: an id or a timestamp is meant to come from
        // the database, and the application has nothing to agree with.
        await using var command = new NpgsqlCommand(
            """
            select table_name, column_name, column_default
            from information_schema.columns
            where table_schema = 'public'
              and column_default is not null
              and column_default !~ 'gen_random_uuid|now\(\)|nextval'
            order by table_name, ordinal_position
            """,
            connection);

        var defaults = new List<(string Table, string Column, string Default)>();

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                defaults.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.NotEmpty(defaults);

        // table -> entity type, from the model itself rather than a second hand-kept list.
        var entityTypesByTable = context.Model.GetEntityTypes()
            .Where(entityType => entityType.GetTableName() is not null)
            .ToDictionary(entityType => entityType.GetTableName()!, StringComparer.Ordinal);

        var problems = new List<string>();
        var checkedCount = 0;

        foreach (var (table, column, sqlDefault) in defaults)
        {
            if (!entityTypesByTable.TryGetValue(table, out var entityType))
            {
                continue;
            }

            var property = entityType.GetProperties()
                .SingleOrDefault(candidate => candidate.GetColumnName() == column);

            if (property is null)
            {
                problems.Add($"{table}.{column} has a default but is not mapped to any property.");
                continue;
            }

            var instance = Activator.CreateInstance(entityType.ClrType)
                ?? throw new InvalidOperationException($"Could not construct {entityType.ClrType}.");

            var clrValue = property.PropertyInfo?.GetValue(instance);

            if (!TryDescribeSqlDefault(sqlDefault, out var expected))
            {
                // An unparsed default is reported rather than skipped: silently ignoring a
                // shape this test does not understand is how the next instance of this bug
                // would slip through.
                problems.Add(
                    $"{table}.{column}: could not interpret the SQL default '{sqlDefault}'. "
                    + "Extend TryDescribeSqlDefault so this column is actually checked.");
                continue;
            }

            var actual = DescribeClrValue(clrValue);
            checkedCount++;

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{table}.{column}: database default is {expected} but "
                    + $"{entityType.ClrType.Name}.{property.Name} defaults to {actual}. EF sends "
                    + "the CLR value on insert, so the database default never applies.");
            }
        }

        Assert.True(checkedCount > 30, $"Only {checkedCount} defaults were compared; expected most of the schema's.");
        Assert.True(problems.Count == 0, "Column default mismatches:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Normalises a PostgreSQL default expression into a comparable string.
    /// </summary>
    private static bool TryDescribeSqlDefault(string sqlDefault, out string described)
    {
        var value = sqlDefault.Trim();

        // Strip an explicit cast: 'draft'::listing_status, '{}'::jsonb, 'en'::character varying.
        var castIndex = value.LastIndexOf("::", StringComparison.Ordinal);

        if (castIndex > 0)
        {
            value = value[..castIndex].Trim();
        }

        if (value is "true" or "false")
        {
            described = value;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            described = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            // Doubled single quotes are PostgreSQL's escape inside a literal.
            described = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return true;
        }

        described = string.Empty;
        return false;
    }

    /// <summary>
    /// Renders a CLR default in the same vocabulary as <see cref="TryDescribeSqlDefault"/>.
    /// Enums are rendered as their database label, so an enum whose first member is not the
    /// column's default is reported rather than passing because both happen to be ordinal zero.
    /// </summary>
    private static string DescribeClrValue(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        string text => text,
        Enum enumValue => DescribeEnum(enumValue),
        _ => value.ToString() ?? "null",
    };

    private static string DescribeEnum(Enum value)
    {
        var mapping = PostgresEnumMappings.All
            .SingleOrDefault(candidate => candidate.ClrType == value.GetType());

        if (mapping is null)
        {
            return value.ToString();
        }

        var memberName = value.ToString();
        var index = Array.IndexOf(Enum.GetNames(mapping.ClrType), memberName);

        return index >= 0 ? mapping.ExpectedLabels()[index] : memberName;
    }
}
