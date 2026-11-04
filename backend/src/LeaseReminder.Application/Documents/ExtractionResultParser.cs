using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeaseReminder.Application.Documents;

/// <summary>
/// Turns the model's JSON into validated fields.
/// </summary>
/// <remarks>
/// <para>
/// A response schema constrains the shape of what comes back; it does not
/// make the contents true, or even well-formed. A schema-valid response can
/// still carry "the 32nd of June" in a date, a confidence of 7, a page number
/// past the end of the document, an excerpt longer than the column allows, or
/// the same field twice with different answers.
/// </para>
/// <para>
/// So every value is parsed here, and a value that will not parse is kept as
/// text with its normalised form left null. That is the shape the database
/// documents: a null <c>normalized_value</c> means validation failed and a
/// human must supply it. Discarding the unparseable value instead would throw
/// away the most useful thing on the review screen, which is the chance to see
/// that the model read "June 32" and judge whether the document is unclear or
/// the model misread it.
/// </para>
/// <para>
/// Nothing here throws on bad input. A malformed response is a normal
/// operating condition for a language model, and the pipeline's job is to turn
/// it into something reviewable rather than to fail the document.
/// </para>
/// </remarks>
public static class ExtractionResultParser
{
    /// <summary>Matches the CHECK constraint on <c>source_excerpt</c>.</summary>
    private const int MaximumExcerptLength = 2_000;

    /// <summary>Matches the CHECK constraint on <c>extracted_value</c>.</summary>
    private const int MaximumValueLength = 4_000;

    /// <summary>Matches the CHECK constraint on <c>source_page</c>.</summary>
    private const int MaximumPageNumber = 400;

    /// <summary>Matches the CHECK constraint on <c>issue_note</c> and issue detail.</summary>
    private const int MaximumDetailLength = 2_000;

    /// <summary>Matches the CHECK constraint capping the issue_codes array.</summary>
    private const int MaximumIssueCodesPerField = 20;

    // Issue codes this parser raises itself, as opposed to ones the model
    // reports. Kept distinct so a reviewer can tell "the model flagged this"
    // from "we could not use what the model said".
    private const string UnparseableValueCode = "unparseable_value";
    private const string OutOfRangeCode = "out_of_range";
    private const string DuplicateFieldCode = "duplicate_answer";
    private const string MissingCitationCode = "missing_citation";

    /// <summary>
    /// Parses a model response.
    /// </summary>
    /// <param name="json">The raw response body's structured content.</param>
    /// <param name="pageCount">
    /// Pages in the document, used to reject impossible citations. Null when
    /// unknown, in which case only the absolute cap applies.
    /// </param>
    public static ExtractionResult Parse(string? json, int? pageCount)
    {
        JsonNode? root;

        try
        {
            root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is not JsonObject envelope)
        {
            return new ExtractionResult([], [], "other");
        }

        var fields = ParseFields(envelope["fields"] as JsonArray, pageCount);
        var issues = ParseIssues(envelope["issues"] as JsonArray, pageCount);
        var kind = (envelope["document_kind"]?.GetValue<string>() ?? "other").Trim();

        return new ExtractionResult(fields, issues, kind);
    }

    private static List<ExtractedField> ParseFields(JsonArray? array, int? pageCount)
    {
        var results = new List<ExtractedField>();

        if (array is null)
        {
            return results;
        }

        // The database has a unique constraint on (extraction_id, field_name),
        // so a repeated field would fail the insert and lose the whole run.
        // The first answer wins and the duplicate is recorded against it,
        // which keeps the conflict visible to the reviewer.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in array)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var name = ReadString(entry, "name");
            var field = LeaseFieldCatalog.Find(name);

            // A name outside the catalogue. The schema's enum should prevent
            // this, but "should" is not a guarantee when the producer is a
            // language model, and a name that fails the column's CHECK
            // constraint would fail the insert for every field in the run.
            if (field is null)
            {
                continue;
            }

            if (!seen.Add(field.Name))
            {
                duplicated.Add(field.Name);
                continue;
            }

            results.Add(BuildField(field, entry, pageCount));
        }

        // Fold duplicate notices back into the answers that were kept.
        for (var i = 0; i < results.Count; i++)
        {
            if (duplicated.Contains(results[i].FieldName))
            {
                results[i] = results[i] with
                {
                    IssueCodes = Append(results[i].IssueCodes, DuplicateFieldCode),
                };
            }
        }

        return results;
    }

    private static ExtractedField BuildField(LeaseField field, JsonObject entry, int? pageCount)
    {
        var issues = new List<string>();

        var rawValue = ReadString(entry, "value");

        // A model asked to omit absent fields sometimes returns them anyway
        // with a placeholder. Treating these as a value would put "N/A" in a
        // date column and "null" in an address.
        if (IsPlaceholder(rawValue))
        {
            rawValue = null;
        }

        var normalized = rawValue is null ? null : Normalise(field, rawValue, issues);

        var confidence = ReadConfidence(entry, issues);
        var page = ReadPage(entry, pageCount, issues);
        var excerpt = Truncate(ReadString(entry, "source_excerpt"), MaximumExcerptLength);

        // A value with nothing to check it against cannot be reviewed
        // efficiently, which makes it worth flagging even when it parsed
        // cleanly. The reviewer has to find the clause themselves.
        if (normalized is not null && (page is null || string.IsNullOrWhiteSpace(excerpt)))
        {
            issues.Add(MissingCitationCode);
        }

        var provenance = ReadString(entry, "provenance");

        if (provenance is null
            || !ExtractionResponseSchema.ProvenanceValues.Contains(
                provenance, StringComparer.Ordinal))
        {
            // The conservative default. Claiming something was stated when it
            // was calculated is the more misleading error of the two, but
            // claiming calculation when unknown would understate how much of
            // the document is being taken at face value, so the safer reading
            // is that we do not know how it was arrived at and it should be
            // checked. 'stated_in_document' is the database default and is
            // what an unmarked value means elsewhere.
            provenance = "stated_in_document";
        }

        return new ExtractedField(
            FieldName: field.Name,
            ValueType: field.ValueTypeName,
            ExtractedValue: Truncate(rawValue, MaximumValueLength),
            NormalizedValue: normalized,
            Confidence: confidence,
            Provenance: provenance,
            SourcePage: page,
            SourceExcerpt: excerpt,
            IssueCodes: issues.Count > MaximumIssueCodesPerField
                ? issues.Take(MaximumIssueCodesPerField).ToArray()
                : issues);
    }

    /// <summary>
    /// Converts the model's text into a typed value, or null if it will not
    /// convert.
    /// </summary>
    private static JsonValue? Normalise(LeaseField field, string raw, List<string> issues)
    {
        var value = raw.Trim();

        switch (field.Type)
        {
            case LeaseFieldType.Date:
            {
                // Exact formats only. Letting a general parser loose here
                // would make 03/04/2026 mean March in one culture and April
                // in another, silently, on a date that decides when somebody
                // is told their rent is due.
                string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd"];

                if (DateOnly.TryParseExact(
                        value, formats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                {
                    return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                issues.Add(UnparseableValueCode);

                return null;
            }

            case LeaseFieldType.Money:
            case LeaseFieldType.Fraction:
            {
                // Strip what a model adds despite being asked not to: a
                // currency symbol, thousands separators, a trailing percent.
                var cleaned = new string([.. value.Where(c =>
                    char.IsAsciiDigit(c) || c == '.' || c == '-')]);

                if (decimal.TryParse(
                        cleaned, NumberStyles.Number, CultureInfo.InvariantCulture,
                        out var number))
                {
                    if (field.Type == LeaseFieldType.Money && number < 0)
                    {
                        issues.Add(OutOfRangeCode);

                        return null;
                    }

                    return JsonValue.Create(number);
                }

                issues.Add(UnparseableValueCode);

                return null;
            }

            case LeaseFieldType.WholeNumber:
            case LeaseFieldType.DurationDays:
            {
                var cleaned = new string([.. value.Where(c => char.IsAsciiDigit(c) || c == '-')]);

                if (int.TryParse(
                        cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var number))
                {
                    if (number < 0)
                    {
                        issues.Add(OutOfRangeCode);

                        return null;
                    }

                    // The one field with a meaningful domain constraint this
                    // layer knows about. A due day of 0 or 45 is not a day.
                    if (field.Name == "rent.due_day" && number is < 1 or > 31)
                    {
                        issues.Add(OutOfRangeCode);

                        return null;
                    }

                    return JsonValue.Create(number);
                }

                issues.Add(UnparseableValueCode);

                return null;
            }

            case LeaseFieldType.Boolean:
            {
                if (bool.TryParse(value, out var flag))
                {
                    return JsonValue.Create(flag);
                }

                // Models write these despite the instruction.
                var normalised = value.ToLowerInvariant();

                if (normalised is "yes" or "y" or "1" or "permitted" or "allowed")
                {
                    return JsonValue.Create(true);
                }

                if (normalised is "no" or "n" or "0" or "prohibited" or "not allowed")
                {
                    return JsonValue.Create(false);
                }

                issues.Add(UnparseableValueCode);

                return null;
            }

            case LeaseFieldType.Enum:
            {
                var match = field.AllowedValues?.FirstOrDefault(
                    allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return JsonValue.Create(match);
                }

                issues.Add(UnparseableValueCode);

                return null;
            }

            case LeaseFieldType.ShortText:
            case LeaseFieldType.LongText:
            default:
                return value.Length == 0 ? null : JsonValue.Create(Truncate(value, MaximumValueLength));
        }
    }

    private static decimal? ReadConfidence(JsonObject entry, List<string> issues)
    {
        var node = entry["confidence"];

        if (node is null)
        {
            return null;
        }

        decimal value;

        try
        {
            value = node.GetValue<decimal>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            // Occasionally arrives as "0.9" or "high" despite the schema.
            if (!decimal.TryParse(
                    node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture,
                    out value))
            {
                return null;
            }
        }

        // Some models report a percentage. A confidence of 95 is not a
        // confidence of 1, and clamping it to 1 would turn a reasonable value
        // into a maximal one, so it is rescaled.
        if (value is > 1 and <= 100)
        {
            value /= 100;
        }

        if (value is < 0 or > 1)
        {
            issues.Add(OutOfRangeCode);

            return null;
        }

        // The column is numeric(5,4): four decimal places, which is far more
        // precision than a self-reported confidence carries anyway.
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    private static int? ReadPage(JsonObject entry, int? pageCount, List<string> issues)
    {
        var node = entry["source_page"];

        if (node is null)
        {
            return null;
        }

        int page;

        try
        {
            page = node.GetValue<int>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            if (!int.TryParse(
                    node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out page))
            {
                return null;
            }
        }

        // A citation past the end of the document is not a citation. It is
        // also a signal worth keeping: a model inventing page numbers is a
        // model inventing other things.
        var ceiling = Math.Min(pageCount ?? MaximumPageNumber, MaximumPageNumber);

        if (page < 1 || page > ceiling)
        {
            issues.Add(OutOfRangeCode);

            return null;
        }

        return page;
    }

    private static List<ExtractionIssue> ParseIssues(JsonArray? array, int? pageCount)
    {
        var issues = new List<ExtractionIssue>();

        if (array is null)
        {
            return issues;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var code = ReadString(entry, "code");

            if (code is null
                || !ExtractionResponseSchema.IssueCodes.Contains(code, StringComparer.Ordinal))
            {
                continue;
            }

            var detail = Truncate(ReadString(entry, "detail"), MaximumDetailLength)
                ?? "No detail was given.";

            var page = ReadPage(entry, pageCount, []);

            issues.Add(new ExtractionIssue(code, detail, page));
        }

        return issues;
    }

    private static string? ReadString(JsonObject entry, string property)
    {
        var node = entry[property];

        if (node is null)
        {
            return null;
        }

        string? value;

        try
        {
            value = node.GetValue<string>();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            // A number or boolean where a string was declared.
            value = node.ToString();
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Recognises the ways a model says "nothing here" when asked to omit the
    /// field entirely.
    /// </summary>
    private static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "null" or "n/a" or "na" or "none" or "not specified" or "not stated"
                or "unknown" or "unspecified" or "not applicable" or "-" or "--"
                or "not provided" or "not mentioned" or "tbd" => true,
            _ => false,
        };
    }

    private static string? Truncate(string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maximum ? value : value[..maximum];
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> existing, string code)
    {
        if (existing.Contains(code, StringComparer.Ordinal))
        {
            return existing;
        }

        return [.. existing, code];
    }
}
