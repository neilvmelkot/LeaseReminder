using System.Text.Json.Nodes;

namespace LeaseReminder.Application.Documents;

/// <summary>
/// One extracted lease term, after validation.
/// </summary>
/// <param name="FieldName">The catalogue name. Always one that exists.</param>
/// <param name="ValueType">Written to <c>lease_extracted_fields.value_type</c>.</param>
/// <param name="ExtractedValue">
/// The model's answer as text, for display and diffing. Kept even when
/// normalisation failed, because showing a reviewer what the model said is how
/// they judge whether it misread the clause or the clause is genuinely odd.
/// </param>
/// <param name="NormalizedValue">
/// The typed form. Null when the value could not be parsed, which the database
/// documents as meaning a human must supply it.
/// </param>
/// <param name="Confidence">Clamped to 0..1, or null if unusable.</param>
/// <param name="Provenance">Stated or calculated.</param>
/// <param name="SourcePage">1-based, within the document's page count.</param>
/// <param name="SourceExcerpt">Supporting quote, truncated to fit the column.</param>
/// <param name="IssueCodes">Why this field needs attention, if it does.</param>
public sealed record ExtractedField(
    string FieldName,
    string ValueType,
    string? ExtractedValue,
    JsonNode? NormalizedValue,
    decimal? Confidence,
    string Provenance,
    int? SourcePage,
    string? SourceExcerpt,
    IReadOnlyList<string> IssueCodes)
{
    /// <summary>
    /// Whether this field needs a human to look at it before approval.
    /// </summary>
    /// <remarks>
    /// Drives the review screen's default filter. Low confidence alone is
    /// enough: the threshold is a judgement about how much a reviewer's
    /// attention is worth, not a claim about correctness.
    /// </remarks>
    public bool NeedsAttention =>
        this.IssueCodes.Count > 0
        || this.NormalizedValue is null
        || this.Confidence is null or < LowConfidenceThreshold;

    /// <summary>
    /// Below this, a value is flagged regardless of whether it parsed.
    /// </summary>
    public const decimal LowConfidenceThreshold = 0.75m;
}

/// <summary>A problem spanning the document rather than one field.</summary>
public sealed record ExtractionIssue(string Code, string Detail, int? SourcePage);

/// <summary>
/// Everything one extraction run produced.
/// </summary>
public sealed record ExtractionResult(
    IReadOnlyList<ExtractedField> Fields,
    IReadOnlyList<ExtractionIssue> Issues,
    string DocumentKind)
{
    /// <summary>
    /// Fields the catalogue asked for that the document did not answer, and
    /// which reminders cannot be scheduled without.
    /// </summary>
    /// <remarks>
    /// Computed rather than reported by the model. Asking the model which
    /// fields it omitted invites it to fill them in; deriving the list from
    /// what actually came back cannot be talked out of the right answer.
    /// </remarks>
    public IReadOnlyList<LeaseField> MissingRequiredFields { get; } =
    [
        .. LeaseFieldCatalog.Fields
            .Where(candidate => candidate.Importance == LeaseFieldImportance.Required)
            .Where(candidate => !Fields.Any(extracted =>
                extracted.FieldName == candidate.Name
                && extracted.NormalizedValue is not null)),
    ];

    /// <summary>
    /// Whether a reviewer has anything to resolve before this can be approved.
    /// </summary>
    public bool RequiresReview =>
        this.MissingRequiredFields.Count > 0
        || this.Issues.Count > 0
        // Not named "field": inside a property accessor C# 14 binds that
        // identifier to the property's synthesized backing field.
        || this.Fields.Any(extracted => extracted.NeedsAttention);

    /// <summary>
    /// True if the model reports the file is not a lease.
    /// </summary>
    /// <remarks>
    /// Worth surfacing prominently rather than as one issue among many. The
    /// usual cause is an honest mistake: a utility bill or an inspection
    /// report uploaded into the wrong place. Telling the user that directly is
    /// more useful than presenting them with thirty empty fields.
    /// </remarks>
    public bool LooksLikeSomethingOtherThanALease =>
        string.Equals(this.DocumentKind, "other", StringComparison.Ordinal);
}
