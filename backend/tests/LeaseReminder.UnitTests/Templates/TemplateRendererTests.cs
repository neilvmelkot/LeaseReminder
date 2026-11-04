using LeaseReminder.Application.Templates;
using Xunit;

namespace LeaseReminder.UnitTests.Templates;

/// <summary>
/// Covers the template variable validation item in section 9, and the email
/// template injection requirement in section 6.I.
///
/// The escaping tests matter most. Lease documents are untrusted input
/// (section 4.D), extracted values reach these variables, and the output is
/// HTML sent to a tenant. If a value can become markup, a document can inject
/// content into an email the landlord appears to have sent.
/// </summary>
public class TemplateRendererTests
{
    private static Dictionary<string, string?> Values(
        params (string Name, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    // =========================================================================
    // Substitution
    // =========================================================================

    [Fact]
    public void A_known_variable_is_substituted()
    {
        var result = TemplateRenderer.Render(
            "Hello {{tenant_name}}, rent is due.",
            Values(("tenant_name", "Dana Okafor")),
            escapeForHtml: false);

        Assert.Equal("Hello Dana Okafor, rent is due.", result.Output);
        Assert.Equal(["tenant_name"], result.SubstitutedVariables);
        Assert.False(result.HasProblems);
    }

    [Fact]
    public void Several_variables_are_substituted_in_one_pass()
    {
        var result = TemplateRenderer.Render(
            "{{property_name}} {{unit_number}}: {{currency}} {{rent_amount}} due {{due_date}}",
            Values(
                ("property_name", "Oak Street"),
                ("unit_number", "3B"),
                ("currency", "USD"),
                ("rent_amount", "2400.00"),
                ("due_date", "2026-04-01")),
            escapeForHtml: false);

        Assert.Equal("Oak Street 3B: USD 2400.00 due 2026-04-01", result.Output);
    }

    [Fact]
    public void Whitespace_inside_the_braces_is_tolerated()
    {
        var result = TemplateRenderer.Render(
            "Hello {{ tenant_name }}.",
            Values(("tenant_name", "Dana")),
            escapeForHtml: false);

        Assert.Equal("Hello Dana.", result.Output);
    }

    [Fact]
    public void Text_with_no_variables_passes_through_unchanged()
    {
        const string body = "Rent is due. Please pay on time.";

        var result = TemplateRenderer.Render(body, Values(), escapeForHtml: false);

        Assert.Equal(body, result.Output);
    }

    [Fact]
    public void An_empty_body_renders_empty()
    {
        var result = TemplateRenderer.Render(string.Empty, Values(), escapeForHtml: true);

        Assert.Equal(string.Empty, result.Output);
    }

    // =========================================================================
    // HTML escaping
    // =========================================================================

    [Fact]
    public void A_value_containing_markup_is_escaped_in_the_html_body()
    {
        var result = TemplateRenderer.Render(
            "<p>Hello {{tenant_name}}</p>",
            Values(("tenant_name", "<script>alert(1)</script>")),
            escapeForHtml: true);

        // The tag must not survive as a tag.
        Assert.DoesNotContain("<script>", result.Output, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", result.Output, StringComparison.Ordinal);

        // The template's own markup is untouched: it is authored content, not
        // a substituted value.
        Assert.StartsWith("<p>Hello ", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("</p>", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ampersand_in_a_real_name_is_escaped_rather_than_mangled()
    {
        var result = TemplateRenderer.Render(
            "<p>{{landlord_name}}</p>",
            Values(("landlord_name", "Smith & Sons")),
            escapeForHtml: true);

        Assert.Contains("Smith &amp; Sons", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_cannot_break_out_of_an_html_attribute()
    {
        var result = TemplateRenderer.Render(
            "<a title=\"{{property_name}}\">link</a>",
            Values(("property_name", "\" onmouseover=\"steal()")),
            escapeForHtml: true);

        // The injected quote must be encoded, so the attribute cannot be
        // closed early and a new one opened.
        Assert.DoesNotContain("onmouseover=\"steal()\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("&quot;", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_body_is_not_escaped()
    {
        // Escaping the plain-text part would show "&amp;" to the reader, which
        // is a bug of its own.
        var result = TemplateRenderer.Render(
            "Landlord: {{landlord_name}}",
            Values(("landlord_name", "Smith & Sons")),
            escapeForHtml: false);

        Assert.Equal("Landlord: Smith & Sons", result.Output);
    }

    [Theory]
    [InlineData("<b>bold</b>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("<!-- comment -->")]
    [InlineData("</p><script>x</script><p>")]
    [InlineData("'single quoted'")]
    public void No_value_can_introduce_an_html_tag(string hostileValue)
    {
        var result = TemplateRenderer.Render(
            "<p>{{tenant_name}}</p>",
            Values(("tenant_name", hostileValue)),
            escapeForHtml: true);

        // The only angle brackets left are the template's own.
        var withoutTemplateMarkup = result.Output
            .Replace("<p>", string.Empty, StringComparison.Ordinal)
            .Replace("</p>", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain('<', withoutTemplateMarkup);
        Assert.DoesNotContain('>', withoutTemplateMarkup);
    }

    // =========================================================================
    // Single-pass substitution
    // =========================================================================

    [Fact]
    public void A_substituted_value_is_not_rescanned_for_variables()
    {
        // A tenant name extracted from a hostile document contains what looks
        // like another placeholder. If the renderer re-scanned its own output,
        // document content would reach other variables: template injection.
        var result = TemplateRenderer.Render(
            "Hello {{tenant_name}}.",
            Values(
                ("tenant_name", "{{rent_amount}}"),
                ("rent_amount", "999999.00")),
            escapeForHtml: false);

        // The literal braces survive; the amount does not leak in.
        Assert.Equal("Hello {{rent_amount}}.", result.Output);
        Assert.DoesNotContain("999999", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_containing_its_own_name_does_not_recurse()
    {
        var result = TemplateRenderer.Render(
            "{{tenant_name}}",
            Values(("tenant_name", "{{tenant_name}}")),
            escapeForHtml: false);

        Assert.Equal("{{tenant_name}}", result.Output);
    }

    // =========================================================================
    // The allowlist
    // =========================================================================

    [Fact]
    public void An_unknown_variable_is_removed_and_reported()
    {
        var result = TemplateRenderer.Render(
            "Hello {{tenant_name}}, your {{secret_internal_id}} is ready.",
            Values(
                ("tenant_name", "Dana"),
                ("secret_internal_id", "should-never-appear")),
            escapeForHtml: false);

        // Present in the dictionary but not on the allowlist, so it is not
        // resolved. Dropping it beats echoing the placeholder back, which
        // would put an attacker-chosen string into the message.
        Assert.Equal("Hello Dana, your  is ready.", result.Output);
        Assert.DoesNotContain("should-never-appear", result.Output, StringComparison.Ordinal);
        Assert.Equal(["secret_internal_id"], result.UnknownVariables);
        Assert.True(result.HasProblems);
    }

    [Theory]
    [InlineData("{{a.b}}")]        // a property path
    [InlineData("{{a[0]}}")]       // an index
    [InlineData("{{__proto__}}")]  // leading underscore
    [InlineData("{{7*7}}")]        // an expression
    [InlineData("{{Tenant_Name}}")]// wrong case
    [InlineData("{{ }}")]          // empty
    public void A_malformed_placeholder_is_left_as_literal_text(string placeholder)
    {
        // The renderer has no expression language, and refusing to parse one
        // is what keeps it that way. A name it does not even recognise the
        // SHAPE of is emitted verbatim rather than evaluated or resolved.
        var result = TemplateRenderer.Render(
            $"Value: {placeholder}", Values(), escapeForHtml: false);

        Assert.Equal($"Value: {placeholder}", result.Output);
    }

    [Theory]
    // These are well-formed names that simply are not on the allowlist. They
    // take the other path: dropped from the output and reported, so a hostile
    // template cannot probe for a variable that happens to resolve.
    [InlineData("constructor")]
    [InlineData("prototype")]
    [InlineData("password")]
    [InlineData("encrypted_refresh_token")]
    public void A_well_formed_but_unlisted_name_is_dropped_and_reported(string name)
    {
        var result = TemplateRenderer.Render(
            $"Value: {{{{{name}}}}}",
            // Present in the dictionary, to prove membership is what gates
            // substitution rather than mere absence of a value.
            Values((name, "must-not-appear")),
            escapeForHtml: false);

        Assert.Equal("Value: ", result.Output);
        Assert.DoesNotContain("must-not-appear", result.Output, StringComparison.Ordinal);
        Assert.Equal([name], result.UnknownVariables);
    }

    [Fact]
    public void A_very_long_placeholder_name_is_not_treated_as_a_variable()
    {
        var longName = new string('a', 500);

        var result = TemplateRenderer.Render(
            $"{{{{{longName}}}}}", Values(), escapeForHtml: false);

        Assert.Contains(longName, result.Output, StringComparison.Ordinal);
        Assert.Empty(result.UnknownVariables);
    }

    [Fact]
    public void An_unterminated_placeholder_does_not_lose_the_rest_of_the_body()
    {
        // A stray brace must not cost the reader the remainder of the message.
        var result = TemplateRenderer.Render(
            "Hello {{tenant_name}}, and then {{oops",
            Values(("tenant_name", "Dana")),
            escapeForHtml: false);

        Assert.Equal("Hello Dana, and then {{oops", result.Output);
    }

    // =========================================================================
    // Missing values
    // =========================================================================

    [Fact]
    public void An_allowlisted_variable_with_no_value_is_reported_as_missing()
    {
        var result = TemplateRenderer.Render(
            "Hello {{tenant_name}}, unit {{unit_number}}.",
            Values(("tenant_name", "Dana")),
            escapeForHtml: false);

        Assert.Equal("Hello Dana, unit .", result.Output);
        Assert.Equal(["unit_number"], result.MissingVariables);
        Assert.True(result.HasProblems);
    }

    [Fact]
    public void A_null_value_counts_as_missing_rather_than_printing_null()
    {
        var result = TemplateRenderer.Render(
            "Unit {{unit_number}}.",
            Values(("unit_number", null)),
            escapeForHtml: false);

        Assert.Equal("Unit .", result.Output);
        Assert.DoesNotContain("null", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["unit_number"], result.MissingVariables);
    }

    [Fact]
    public void A_single_family_property_with_no_unit_number_renders_cleanly()
    {
        // A real and common case: there is no unit. The reminder should read
        // naturally rather than showing a placeholder or the word null.
        var result = TemplateRenderer.Render(
            "Rent for {{property_name}}{{unit_number}} is due.",
            Values(("property_name", "12 Oak Street"), ("unit_number", null)),
            escapeForHtml: false);

        Assert.Equal("Rent for 12 Oak Street is due.", result.Output);
    }

    // =========================================================================
    // The variable scanner, used by the editor and at save time
    // =========================================================================

    [Fact]
    public void Scan_separates_known_from_unknown_variables()
    {
        var scan = TemplateRenderer.Scan(
            "{{tenant_name}} {{rent_amount}} {{not_a_real_variable}}");

        Assert.Equal(["rent_amount", "tenant_name"], scan.KnownVariables);
        Assert.Equal(["not_a_real_variable"], scan.UnknownVariables);
    }

    [Fact]
    public void Scan_deduplicates_repeated_variables()
    {
        var scan = TemplateRenderer.Scan("{{tenant_name}} and again {{tenant_name}}");

        Assert.Equal(["tenant_name"], scan.KnownVariables);
    }

    [Fact]
    public void Scan_finds_nothing_in_plain_text()
    {
        var scan = TemplateRenderer.Scan("No variables at all.");

        Assert.Empty(scan.KnownVariables);
        Assert.Empty(scan.UnknownVariables);
    }

    [Fact]
    public void The_allowlist_contains_every_variable_named_in_the_specification()
    {
        // Section 6.I lists these by name. A template editor that offered a
        // variable the renderer then dropped would be worse than useless.
        string[] fromSpecification =
        [
            "tenant_name", "landlord_name", "property_name", "property_address",
            "unit_number", "rent_amount", "due_date", "days_until_due",
            "lease_start_date", "lease_end_date", "sender_name",
        ];

        Assert.All(fromSpecification, name =>
            Assert.Contains(name, TemplateRenderer.AllowedVariables));
    }

    [Fact]
    public void The_allowlist_keeps_the_three_rent_dates_separate()
    {
        // Section 6.F requires the due date, the grace period end, and the
        // late fee date to stay distinct. A template must be able to say which
        // one it means.
        Assert.Contains("due_date", TemplateRenderer.AllowedVariables);
        Assert.Contains("grace_period_end", TemplateRenderer.AllowedVariables);
        Assert.Contains("late_fee_date", TemplateRenderer.AllowedVariables);
    }
}
