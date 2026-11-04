using System.Text;
using System.Text.Encodings.Web;

namespace LeaseReminder.Application.Templates;

/// <summary>
/// Substitutes <c>{{variable}}</c> placeholders in an email template.
///
/// <para>Three properties matter more than the substitution itself:</para>
///
/// <list type="number">
///   <item>
///     <b>Values are escaped for the HTML body.</b> A tenant named
///     <c>Smith &amp; Sons &lt;legal&gt;</c> must not become markup, and a
///     value that came out of an AI extraction must never be able to introduce
///     a tag or an attribute (sections 4.D and 6.I). The text body is not
///     escaped, because escaping there would be a bug of its own.
///   </item>
///   <item>
///     <b>Substitution is single-pass.</b> A substituted value is never
///     re-scanned, so a value containing <c>{{rent_amount}}</c> stays literal
///     text. Without that, document-sourced content could reach other
///     variables, which is the email equivalent of template injection.
///   </item>
///   <item>
///     <b>Only allowlisted names are substituted.</b> An unknown placeholder
///     is removed and reported, never left in the output and never resolved
///     against anything the caller did not intend to expose.
///   </item>
/// </list>
/// </summary>
public static class TemplateRenderer
{
    /// <summary>
    /// The variables a template may use, exactly as listed in section 6.I,
    /// plus the four date fields the separate-dates requirement in 6.F needs.
    ///
    /// <para>
    /// This set is the contract. A template referencing anything else fails
    /// validation at save time rather than rendering a blank at send time.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedVariables = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "tenant_name",
        "landlord_name",
        "property_name",
        "property_address",
        "unit_number",
        "rent_amount",
        "currency",
        "due_date",
        "days_until_due",
        "lease_start_date",
        "lease_end_date",
        "sender_name",

        // Kept distinct from due_date on purpose: a grace-period end and a
        // late-fee date are different dates with different consequences.
        "grace_period_end",
        "late_fee_date",
    };

    /// <summary>Longest placeholder name accepted, as a cheap parser bound.</summary>
    private const int MaxVariableNameLength = 64;

    /// <summary>
    /// Renders a template body.
    /// </summary>
    /// <param name="body">Template text containing <c>{{name}}</c> placeholders.</param>
    /// <param name="values">
    /// Variable values. A name present with a null or empty value is treated as
    /// "known but blank", which is different from an unknown name.
    /// </param>
    /// <param name="escapeForHtml">
    /// True for the HTML body, false for the plain-text body.
    /// </param>
    public static TemplateRenderResult Render(
        string body,
        IReadOnlyDictionary<string, string?> values,
        bool escapeForHtml)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);

        var output = new StringBuilder(body.Length + 64);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var substituted = new SortedSet<string>(StringComparer.Ordinal);

        var index = 0;

        while (index < body.Length)
        {
            var open = body.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0)
            {
                output.Append(body, index, body.Length - index);
                break;
            }

            // Everything before the placeholder is literal template text,
            // authored by the application or by an administrator, so it is
            // emitted as-is.
            output.Append(body, index, open - index);

            var close = body.IndexOf("}}", open + 2, StringComparison.Ordinal);

            if (close < 0)
            {
                // An unterminated placeholder is literal text, not an error:
                // refusing to render a whole reminder over a stray brace would
                // be a worse outcome than showing the brace.
                output.Append(body, open, body.Length - open);
                break;
            }

            var rawName = body[(open + 2)..close];
            var name = rawName.Trim();

            if (!IsWellFormedName(name))
            {
                // Not a placeholder we recognise the shape of. Emit it
                // literally rather than guessing at an intent.
                output.Append(body, open, close + 2 - open);
                index = close + 2;
                continue;
            }

            if (!AllowedVariables.Contains(name))
            {
                // Dropped, not echoed. Echoing would put an attacker-chosen
                // string into the message, and resolving it would expose
                // whatever happened to be in the dictionary.
                unknown.Add(name);
                index = close + 2;
                continue;
            }

            if (!values.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            {
                missing.Add(name);
                index = close + 2;
                continue;
            }

            // The only place a value enters the output, and therefore the only
            // place escaping has to be correct.
            output.Append(escapeForHtml ? HtmlEncoder.Default.Encode(value) : value);
            substituted.Add(name);

            // index advances past the placeholder, never back into the text we
            // just wrote, so a substituted value is never re-scanned.
            index = close + 2;
        }

        return new TemplateRenderResult
        {
            Output = output.ToString(),
            SubstitutedVariables = substituted,
            UnknownVariables = unknown,
            MissingVariables = missing,
        };
    }

    /// <summary>
    /// Lists the variables a template references, for the editor's
    /// "unknown variable" warning and for email_templates.referenced_variables.
    /// </summary>
    public static TemplateVariableScan Scan(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var known = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < body.Length)
        {
            var open = body.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = body.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var name = body[(open + 2)..close].Trim();

            if (IsWellFormedName(name))
            {
                if (AllowedVariables.Contains(name))
                {
                    known.Add(name);
                }
                else
                {
                    unknown.Add(name);
                }
            }

            index = close + 2;
        }

        return new TemplateVariableScan
        {
            KnownVariables = known,
            UnknownVariables = unknown,
        };
    }

    /// <summary>
    /// A placeholder name must be a plain lower-case identifier. Anything with
    /// punctuation, a path, or an index is rejected outright: the renderer has
    /// no expression language, and refusing to parse one keeps it that way.
    /// </summary>
    private static bool IsWellFormedName(string name)
    {
        if (name.Length is 0 or > MaxVariableNameLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character)
                && !char.IsAsciiDigit(character)
                && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record TemplateRenderResult
{
    public required string Output { get; init; }

    public required IReadOnlyCollection<string> SubstitutedVariables { get; init; }

    /// <summary>
    /// Placeholders that are not in the allowlist. These were removed from the
    /// output. A non-empty set means the template needs fixing.
    /// </summary>
    public required IReadOnlyCollection<string> UnknownVariables { get; init; }

    /// <summary>
    /// Allowlisted placeholders with no value available. The editor warns
    /// about these before publishing (section 6.I).
    /// </summary>
    public required IReadOnlyCollection<string> MissingVariables { get; init; }

    public bool HasProblems => UnknownVariables.Count > 0 || MissingVariables.Count > 0;
}

public sealed record TemplateVariableScan
{
    public required IReadOnlyCollection<string> KnownVariables { get; init; }

    public required IReadOnlyCollection<string> UnknownVariables { get; init; }
}
