using Npgsql;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// Verifies at boot that every C# enum matches the PostgreSQL enum it maps to.
///
/// <para>
/// An enum mismatch is a nasty class of bug in this system. Adding a value to
/// a C# enum and forgetting the migration, or renaming a label so the
/// translator no longer produces it, compiles cleanly, starts cleanly, and
/// serves most traffic correctly. It fails only when somebody saves the one
/// unusual value, which for something like
/// <c>ShortMonthFallback.ManualException</c> might be months later and will
/// surface as an opaque cast error far from the cause.
/// </para>
///
/// <para>
/// A single catalogue query at startup turns that into a failed deployment
/// with a message naming the enum and the missing label. The check is read
/// only and costs one round trip.
/// </para>
/// </summary>
public static class StartupEnumMappingCheck
{
    public static async Task VerifyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var databaseLabels = await ReadDatabaseLabelsAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);

        var problems = new List<string>();

        foreach (var (clrType, postgresTypeName) in NpgsqlDataSourceFactory.MappedEnums)
        {
            if (!databaseLabels.TryGetValue(postgresTypeName, out var labels))
            {
                problems.Add(
                    $"PostgreSQL type {postgresTypeName} does not exist, but {clrType.Name} " +
                    "is mapped to it. Has the migration been applied?");
                continue;
            }

            var expected = Enum.GetNames(clrType)
                .Select(PostgresEnumNameTranslator.ToSnakeCase)
                .ToHashSet(StringComparer.Ordinal);

            var missingInDatabase = expected.Except(labels, StringComparer.Ordinal).ToList();

            if (missingInDatabase.Count > 0)
            {
                problems.Add(
                    $"{clrType.Name} has values with no label in {postgresTypeName}: " +
                    $"{string.Join(", ", missingInDatabase)}. " +
                    "Add them with ALTER TYPE in a migration.");
            }

            // The reverse direction is a warning rather than an error in
            // principle, but in this codebase the two are written together, so
            // a label the application cannot produce is a real omission: some
            // code path will read a row it cannot map.
            var missingInCode = labels.Except(expected, StringComparer.Ordinal).ToList();

            if (missingInCode.Count > 0)
            {
                problems.Add(
                    $"{postgresTypeName} has labels absent from {clrType.Name}: " +
                    $"{string.Join(", ", missingInCode)}. " +
                    "Reading a row with one of these would fail to map.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Enum mappings disagree between the application and the database:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadDatabaseLabelsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname || '.' || t.typname AS type_name,
                   e.enumlabel                   AS label
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE n.nspname = 'app'
            """;

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var typeName = reader.GetString(0);
            var label = reader.GetString(1);

            if (!result.TryGetValue(typeName, out var labels))
            {
                labels = new HashSet<string>(StringComparer.Ordinal);
                result[typeName] = labels;
            }

            labels.Add(label);
        }

        return result;
    }
}
