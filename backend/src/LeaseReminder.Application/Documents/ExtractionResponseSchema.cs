using System.Text.Json.Nodes;

namespace LeaseReminder.Application.Documents;

/// <summary>
/// The response schema sent to the model.
/// </summary>
/// <remarks>
/// <para>
/// This is the second of the three structural defences described on
/// <see cref="ExtractionPrompt"/>, and the one that does the most work. A
/// schema with an enumerated list of field names means the model's output is
/// shape-constrained before it reaches any of this application's code: it
/// cannot return a field that was not asked for, cannot return prose where a
/// structure was expected, and cannot return anything at all outside the
/// envelope.
/// </para>
/// <para>
/// <b>Every value is typed as a string.</b> That looks like giving something
/// up, and it is deliberate. If dates were declared as a date type the model
/// would coerce a malformed date into something syntactically valid, and the
/// coercion would be invisible. Taking everything as text means this
/// application does its own parsing, in one place, with the failure recorded
/// as a reviewable issue rather than silently resolved. A date the model could
/// not read should reach a human as "could not read this", not as a confident
/// wrong date.
/// </para>
/// </remarks>
public static class ExtractionResponseSchema
{
    /// <summary>Issue codes the model may raise against the document.</summary>
    /// <remarks>
    /// Enumerated rather than free text so the review screen can group and
    /// explain them. <c>embedded_instructions</c> is the one that matters
    /// most: it is how an injection attempt gets surfaced to a human instead
    /// of being silently resisted.
    /// </remarks>
    public static IReadOnlyList<string> IssueCodes { get; } =
    [
        "embedded_instructions",
        "conflicting_clause",
        "illegible_text",
        "missing_signature",
        "incomplete_document",
        "ambiguous_term",
        "handwritten_amendment",
        "unsupported_language",
    ];

    /// <summary>Provenance values the model may assign.</summary>
    /// <remarks>
    /// A subset of the database's <c>app.value_provenance</c> enum. The other
    /// two members, <c>user_entered</c> and <c>default_applied</c>, describe
    /// things a person or this application did, so the model is never offered
    /// them: it has no way to know about either, and allowing it to claim one
    /// would let a model-supplied value masquerade as a human-supplied one.
    /// </remarks>
    public static IReadOnlyList<string> ProvenanceValues { get; } =
    [
        "stated_in_document",
        "inferred_by_calculation",
    ];

    /// <summary>
    /// Builds the schema, in the OpenAPI subset the Generative Language API
    /// accepts.
    /// </summary>
    /// <remarks>
    /// Type names are uppercase, which is what that API requires; the usual
    /// lowercase JSON Schema spelling is rejected.
    /// </remarks>
    public static JsonObject Build() => new()
    {
        ["type"] = "OBJECT",
        ["properties"] = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["description"] =
                    "One entry per lease term found. Omit terms the document does not state.",
                ["items"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] = "Which lease term this is.",
                            // The constraint that stops invented field names.
                            ["enum"] = ToJsonArray(LeaseFieldCatalog.FieldNames),
                        },
                        ["value"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] =
                                "The value as written, in the format given for its type.",
                        },
                        ["confidence"] = new JsonObject
                        {
                            ["type"] = "NUMBER",
                            ["description"] =
                                "How legible and unambiguous the supporting text was, "
                                + "from 0 to 1.",
                        },
                        ["source_page"] = new JsonObject
                        {
                            ["type"] = "INTEGER",
                            ["description"] = "The page of the file this came from.",
                        },
                        ["source_excerpt"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] =
                                "A short verbatim quote supporting the value, copied "
                                + "exactly, at most 300 characters.",
                        },
                        ["provenance"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["enum"] = ToJsonArray(ProvenanceValues),
                        },
                    },
                    ["required"] = new JsonArray("name", "value", "confidence", "provenance"),
                },
            },
            ["issues"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["description"] =
                    "Problems with the document as a whole, or ones spanning several "
                    + "terms. Return an empty array if there are none.",
                ["items"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["code"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["enum"] = ToJsonArray(IssueCodes),
                        },
                        ["detail"] = new JsonObject
                        {
                            ["type"] = "STRING",
                            ["description"] =
                                "What is wrong, in one or two sentences a property "
                                + "manager would understand.",
                        },
                        ["source_page"] = new JsonObject
                        {
                            ["type"] = "INTEGER",
                        },
                    },
                    ["required"] = new JsonArray("code", "detail"),
                },
            },
            ["document_kind"] = new JsonObject
            {
                ["type"] = "STRING",
                ["enum"] = new JsonArray(
                    "residential_lease",
                    "commercial_lease",
                    "lease_amendment",
                    "lease_renewal",
                    "other"),
                ["description"] =
                    "What kind of document this is. 'other' if it is not a lease at all.",
            },
        },
        ["required"] = new JsonArray("fields", "issues", "document_kind"),
    };

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
