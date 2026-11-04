using LeaseReminder.Application.Documents;

using Xunit;

namespace LeaseReminder.UnitTests.Documents;

/// <summary>
/// Tests for parsing model output.
/// </summary>
/// <remarks>
/// The guiding assumption is that the model is unreliable but not malicious,
/// and that the document behind it may be either. So the cases here are the
/// ones a schema does not prevent: values that will not parse, citations that
/// cannot exist, the same field answered twice, and placeholders returned in
/// place of an omission.
/// </remarks>
public sealed class ExtractionResultParserTests
{
    // ---------------------------------------------------------------------
    // The ordinary path
    // ---------------------------------------------------------------------

    [Fact]
    public void Parses_a_well_formed_response()
    {
        var result = ExtractionResultParser.Parse(
            """
            {
              "document_kind": "residential_lease",
              "issues": [],
              "fields": [
                {
                  "name": "rent.amount",
                  "value": "2400.00",
                  "confidence": 0.96,
                  "source_page": 2,
                  "source_excerpt": "Tenant shall pay rent of $2,400.00 per month",
                  "provenance": "stated_in_document"
                }
              ]
            }
            """,
            pageCount: 10);

        var field = Assert.Single(result.Fields);

        Assert.Equal("rent.amount", field.FieldName);
        Assert.Equal("money", field.ValueType);
        Assert.Equal(2400.00m, field.NormalizedValue!.GetValue<decimal>());
        Assert.Equal(0.96m, field.Confidence);
        Assert.Equal(2, field.SourcePage);
        Assert.Empty(field.IssueCodes);
        Assert.False(field.NeedsAttention);
        Assert.Equal("residential_lease", result.DocumentKind);
    }

    // ---------------------------------------------------------------------
    // Values that will not parse
    // ---------------------------------------------------------------------

    /// <summary>
    /// The central behaviour: an unparseable value is kept as text with a null
    /// normalised form, not discarded. The reviewer needs to see what the
    /// model read in order to judge it.
    /// </summary>
    [Fact]
    public void Keeps_an_unparseable_date_as_text_for_review()
    {
        var result = Parse("term.start_date", "the 32nd of June 2026");

        var field = Assert.Single(result.Fields);

        Assert.Equal("the 32nd of June 2026", field.ExtractedValue);
        Assert.Null(field.NormalizedValue);
        Assert.Contains("unparseable_value", field.IssueCodes);
        Assert.True(field.NeedsAttention);
    }

    /// <summary>
    /// Ambiguous numeric dates are refused rather than guessed. 03/04/2026 is
    /// March in one country and April in another, and this value decides when
    /// somebody is told their rent is due.
    /// </summary>
    [Theory]
    [InlineData("03/04/2026")]
    [InlineData("4 March 2026")]
    [InlineData("March 4, 2026")]
    public void Refuses_to_guess_an_ambiguous_date(string written)
    {
        var result = Parse("term.start_date", written);

        Assert.Null(Assert.Single(result.Fields).NormalizedValue);
    }

    [Theory]
    [InlineData("2026-06-01")]
    [InlineData("2026/06/01")]
    [InlineData("  2026-06-01  ")]
    public void Accepts_unambiguous_date_formats(string written)
    {
        var result = Parse("term.start_date", written);

        Assert.Equal("2026-06-01", Assert.Single(result.Fields).NormalizedValue!.GetValue<string>());
    }

    /// <summary>
    /// Models add currency symbols and separators despite being told not to.
    /// Refusing those would flag most of a correct extraction.
    /// </summary>
    [Theory]
    [InlineData("$2,400.00", 2400.00)]
    [InlineData("2400", 2400)]
    [InlineData("£1,850.50", 1850.50)]
    [InlineData("USD 3,100.00", 3100.00)]
    public void Cleans_currency_formatting_from_an_amount(string written, decimal expected)
    {
        var result = Parse("rent.amount", written);

        Assert.Equal(expected, Assert.Single(result.Fields).NormalizedValue!.GetValue<decimal>());
    }

    [Fact]
    public void Refuses_a_negative_rent()
    {
        var result = Parse("rent.amount", "-500");

        var field = Assert.Single(result.Fields);

        Assert.Null(field.NormalizedValue);
        Assert.Contains("out_of_range", field.IssueCodes);
    }

    /// <summary>
    /// A due day outside 1..31 is not a day of the month. This is the one
    /// domain rule the parser knows, and it matters because the value feeds
    /// the schedule generator directly.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("45")]
    [InlineData("32")]
    public void Refuses_an_impossible_due_day(string written)
    {
        var result = Parse("rent.due_day", written);

        var field = Assert.Single(result.Fields);

        Assert.Null(field.NormalizedValue);
        Assert.Contains("out_of_range", field.IssueCodes);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("15")]
    [InlineData("31")]
    public void Accepts_a_valid_due_day(string written)
    {
        var result = Parse("rent.due_day", written);

        Assert.NotNull(Assert.Single(result.Fields).NormalizedValue);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("allowed", true)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData("prohibited", false)]
    public void Reads_the_ways_a_model_writes_a_boolean(string written, bool expected)
    {
        var result = Parse("obligations.pets_allowed", written);

        Assert.Equal(expected, Assert.Single(result.Fields).NormalizedValue!.GetValue<bool>());
    }

    [Fact]
    public void Refuses_a_boolean_it_cannot_read()
    {
        var result = Parse("obligations.pets_allowed", "with written consent of the landlord");

        Assert.Null(Assert.Single(result.Fields).NormalizedValue);
    }

    [Fact]
    public void Refuses_an_enum_value_outside_the_allowed_set()
    {
        var result = Parse("rent.frequency", "fortnightly");

        var field = Assert.Single(result.Fields);

        Assert.Null(field.NormalizedValue);
        Assert.Contains("unparseable_value", field.IssueCodes);
    }

    [Fact]
    public void Accepts_an_enum_value_regardless_of_casing()
    {
        var result = Parse("rent.frequency", "Monthly");

        Assert.Equal("monthly", Assert.Single(result.Fields).NormalizedValue!.GetValue<string>());
    }

    // ---------------------------------------------------------------------
    // Placeholders
    // ---------------------------------------------------------------------

    /// <summary>
    /// The model is told to omit absent fields and sometimes returns them with
    /// a placeholder instead. Writing "N/A" into a date column would be worse
    /// than returning nothing.
    /// </summary>
    [Theory]
    [InlineData("N/A")]
    [InlineData("null")]
    [InlineData("not specified")]
    [InlineData("Unknown")]
    [InlineData("-")]
    [InlineData("   ")]
    public void Treats_a_placeholder_as_an_absent_value(string placeholder)
    {
        var result = Parse("deposit.amount", placeholder);

        var field = Assert.Single(result.Fields);

        Assert.Null(field.ExtractedValue);
        Assert.Null(field.NormalizedValue);
    }

    // ---------------------------------------------------------------------
    // Citations
    // ---------------------------------------------------------------------

    /// <summary>
    /// A page number past the end of the document is not a citation, and a
    /// model inventing one is a model inventing other things.
    /// </summary>
    [Fact]
    public void Rejects_a_citation_past_the_end_of_the_document()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.9, "source_page": 99,
                  "source_excerpt": "rent of $2,400",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        var field = Assert.Single(result.Fields);

        Assert.Null(field.SourcePage);
        Assert.Contains("out_of_range", field.IssueCodes);
    }

    /// <summary>
    /// A value nobody can check against the document still needs checking, so
    /// it is flagged even though it parsed cleanly.
    /// </summary>
    [Fact]
    public void Flags_a_value_with_no_supporting_citation()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.99, "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        var field = Assert.Single(result.Fields);

        Assert.Contains("missing_citation", field.IssueCodes);
        Assert.True(field.NeedsAttention);
    }

    /// <summary>
    /// The column caps the excerpt at 2000 characters. A longer one would fail
    /// the insert and lose the whole run, so it is truncated rather than
    /// refused.
    /// </summary>
    [Fact]
    public void Truncates_an_excerpt_that_would_not_fit_the_column()
    {
        var result = ExtractionResultParser.Parse(
            Envelope($$"""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.9, "source_page": 1,
                  "source_excerpt": "{{new string('x', 5000)}}",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        Assert.Equal(2000, Assert.Single(result.Fields).SourceExcerpt!.Length);
    }

    // ---------------------------------------------------------------------
    // Confidence
    // ---------------------------------------------------------------------

    /// <summary>
    /// Some models report a percentage. Clamping 95 to 1 would turn a
    /// reasonable confidence into a maximal one, so it is rescaled instead.
    /// </summary>
    [Theory]
    [InlineData(95, 0.95)]
    [InlineData(100, 1.0)]
    [InlineData(50, 0.5)]
    public void Rescales_a_confidence_reported_as_a_percentage(double written, decimal expected)
    {
        var result = ExtractionResultParser.Parse(
            Envelope($$"""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": {{written.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "source_page": 1, "source_excerpt": "rent",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        Assert.Equal(expected, Assert.Single(result.Fields).Confidence);
    }

    [Fact]
    public void Discards_a_confidence_outside_any_sensible_range()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": -3, "source_page": 1, "source_excerpt": "rent",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        var field = Assert.Single(result.Fields);

        Assert.Null(field.Confidence);
        Assert.Contains("out_of_range", field.IssueCodes);
    }

    [Fact]
    public void Flags_a_low_confidence_value_for_attention()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.4, "source_page": 1, "source_excerpt": "rent",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        Assert.True(Assert.Single(result.Fields).NeedsAttention);
    }

    // ---------------------------------------------------------------------
    // Things the schema should have prevented
    // ---------------------------------------------------------------------

    /// <summary>
    /// A field name outside the catalogue would fail the column's CHECK
    /// constraint, which would lose every other field in the same run. The
    /// schema's enum should prevent it; this is what happens when it does not.
    /// </summary>
    [Fact]
    public void Discards_a_field_name_that_is_not_in_the_catalogue()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.secret_backdoor", "value": "yes",
                  "confidence": 1, "provenance": "stated_in_document"
                },
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.9, "source_page": 1, "source_excerpt": "rent",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        Assert.Equal("rent.amount", Assert.Single(result.Fields).FieldName);
    }

    /// <summary>
    /// The database has a unique constraint per (extraction, field). A
    /// repeated field would fail the insert, so the first answer is kept and
    /// the conflict is recorded against it rather than dropped silently.
    /// </summary>
    [Fact]
    public void Keeps_the_first_of_two_answers_and_records_the_conflict()
    {
        var result = ExtractionResultParser.Parse(
            Envelope("""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.9, "source_page": 1, "source_excerpt": "rent of 2400",
                  "provenance": "stated_in_document"
                },
                {
                  "name": "rent.amount", "value": "9999",
                  "confidence": 0.8, "source_page": 7, "source_excerpt": "rent of 9999",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

        var field = Assert.Single(result.Fields);

        Assert.Equal(2400m, field.NormalizedValue!.GetValue<decimal>());
        Assert.Contains("duplicate_answer", field.IssueCodes);
        Assert.True(field.NeedsAttention);
    }

    /// <summary>
    /// 'user_entered' describes something a person did. A model claiming it
    /// would disguise a machine suggestion as a human decision, which is
    /// exactly the confusion the approval requirement exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("user_entered")]
    [InlineData("default_applied")]
    [InlineData("made_it_up")]
    public void Refuses_a_provenance_the_model_may_not_claim(string claimed)
    {
        var result = ExtractionResultParser.Parse(
            Envelope($$"""
                {
                  "name": "rent.amount", "value": "2400",
                  "confidence": 0.9, "source_page": 1, "source_excerpt": "rent",
                  "provenance": "{{claimed}}"
                }
                """),
            pageCount: 10);

        Assert.Equal("stated_in_document", Assert.Single(result.Fields).Provenance);
    }

    // ---------------------------------------------------------------------
    // Malformed responses
    // ---------------------------------------------------------------------

    /// <summary>
    /// A malformed response is an ordinary operating condition for a language
    /// model. It must become a reviewable empty result, never an exception
    /// that fails the document.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"fields\": \"not an array\"}")]
    [InlineData("{\"fields\": [null, 42, \"text\"]}")]
    public void Turns_a_malformed_response_into_an_empty_result(string? json)
    {
        var result = ExtractionResultParser.Parse(json, pageCount: 10);

        Assert.Empty(result.Fields);
        Assert.Empty(result.Issues);
        Assert.True(result.RequiresReview);
    }

    // ---------------------------------------------------------------------
    // Issues and required fields
    // ---------------------------------------------------------------------

    /// <summary>
    /// The tripwire for prompt injection. A document instructing the model is
    /// reported to a human rather than silently resisted.
    /// </summary>
    [Fact]
    public void Surfaces_an_embedded_instruction_issue()
    {
        var result = ExtractionResultParser.Parse(
            """
            {
              "document_kind": "residential_lease",
              "fields": [],
              "issues": [
                {
                  "code": "embedded_instructions",
                  "detail": "Page 14 contains text addressed to an automated reader.",
                  "source_page": 14
                }
              ]
            }
            """,
            pageCount: 20);

        var issue = Assert.Single(result.Issues);

        Assert.Equal("embedded_instructions", issue.Code);
        Assert.Equal(14, issue.SourcePage);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void Discards_an_issue_code_outside_the_allowed_set()
    {
        var result = ExtractionResultParser.Parse(
            """
            {
              "document_kind": "residential_lease",
              "fields": [],
              "issues": [{ "code": "invented_code", "detail": "..." }]
            }
            """,
            pageCount: 10);

        Assert.Empty(result.Issues);
    }

    /// <summary>
    /// The missing-required list is derived from what came back, not asked of
    /// the model. A model asked which fields it omitted is being invited to
    /// fill them in.
    /// </summary>
    [Fact]
    public void Derives_the_missing_required_fields_from_what_was_returned()
    {
        var result = Parse("rent.amount", "2400");

        var missing = result.MissingRequiredFields.Select(field => field.Name).ToList();

        Assert.DoesNotContain("rent.amount", missing);
        Assert.Contains("term.start_date", missing);
        Assert.Contains("property.address", missing);
        Assert.True(result.RequiresReview);
    }

    /// <summary>
    /// A required field that came back but would not parse still counts as
    /// missing: there is no usable value behind it.
    /// </summary>
    [Fact]
    public void Counts_an_unparseable_required_field_as_missing()
    {
        var result = Parse("term.start_date", "sometime next spring");

        Assert.Contains(
            result.MissingRequiredFields,
            field => field.Name == "term.start_date");
    }

    [Fact]
    public void Reports_when_the_document_is_not_a_lease()
    {
        var result = ExtractionResultParser.Parse(
            """{"document_kind": "other", "fields": [], "issues": []}""",
            pageCount: 2);

        Assert.True(result.LooksLikeSomethingOtherThanALease);
    }

    // ---------------------------------------------------------------------
    // Catalogue consistency
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every catalogue name must satisfy the CHECK constraint on
    /// <c>lease_extracted_fields.field_name</c>, or the insert fails.
    /// </summary>
    [Fact]
    public void Every_catalogue_field_name_matches_the_database_constraint()
    {
        foreach (var field in LeaseFieldCatalog.Fields)
        {
            Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$", field.Name);
            Assert.True(field.Name.Length <= 120);
        }
    }

    [Fact]
    public void Catalogue_field_names_are_unique()
    {
        var names = LeaseFieldCatalog.Fields.Select(field => field.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every enum field must offer the values it will be validated against,
    /// otherwise every answer it gets is refused.
    /// </summary>
    [Fact]
    public void Every_enum_field_declares_its_allowed_values()
    {
        foreach (var field in LeaseFieldCatalog.Fields.Where(f => f.Type == LeaseFieldType.Enum))
        {
            Assert.NotNull(field.AllowedValues);
            Assert.NotEmpty(field.AllowedValues!);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ExtractionResult Parse(string fieldName, string value) =>
        ExtractionResultParser.Parse(
            Envelope($$"""
                {
                  "name": "{{fieldName}}",
                  "value": {{System.Text.Json.JsonSerializer.Serialize(value)}},
                  "confidence": 0.9,
                  "source_page": 1,
                  "source_excerpt": "supporting text",
                  "provenance": "stated_in_document"
                }
                """),
            pageCount: 10);

    private static string Envelope(string fields) =>
        $$"""
        {
          "document_kind": "residential_lease",
          "issues": [],
          "fields": [{{fields}}]
        }
        """;
}
