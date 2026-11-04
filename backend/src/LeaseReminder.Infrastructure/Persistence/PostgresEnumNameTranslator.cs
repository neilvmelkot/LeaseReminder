using System.Text;
using Npgsql;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// Translates a C# enum member name to the PostgreSQL enum label.
///
/// <para>
/// Npgsql's built-in snake-case translator is nearly right, but it mishandles
/// digits: <c>Every30Days</c> becomes <c>every30_days</c>, whereas the label in
/// db/migrations/0001 is <c>every_30_days</c>. That mismatch would not be a
/// compile error or a startup error. It would be a cast failure the first time
/// somebody saved a rent schedule with that frequency, which is the worst
/// possible moment to discover it.
/// </para>
///
/// <para>
/// So the rule is stated explicitly: insert an underscore at every boundary
/// between a letter and a digit, between a digit and a letter, and before an
/// upper-case letter that follows a lower-case one or a digit. Then lower-case
/// the result. <see cref="StartupEnumMappingCheck"/> verifies the output
/// against the live database at boot, so a future enum whose name does not
/// translate cleanly fails fast rather than at first use.
/// </para>
/// </summary>
public sealed class PostgresEnumNameTranslator : INpgsqlNameTranslator
{
    public static PostgresEnumNameTranslator Instance { get; } = new();

    /// <summary>Not used: table and column names come from the EF model.</summary>
    public string TranslateTypeName(string clrName) => ToSnakeCase(clrName);

    public string TranslateMemberName(string clrName) => ToSnakeCase(clrName);

    public static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (i > 0 && NeedsSeparatorBefore(name[i - 1], current))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static bool NeedsSeparatorBefore(char previous, char current)
    {
        // PascalCase boundary: ...yD... in PropertyDefault.
        if (char.IsUpper(current) && !char.IsUpper(previous))
        {
            return true;
        }

        // Letter followed by a digit: Every30 becomes every_30.
        if (char.IsAsciiDigit(current) && char.IsAsciiLetter(previous))
        {
            return true;
        }

        // Digit followed by a letter: 30Days becomes 30_days.
        if (char.IsAsciiLetter(current) && char.IsAsciiDigit(previous))
        {
            return true;
        }

        return false;
    }
}
