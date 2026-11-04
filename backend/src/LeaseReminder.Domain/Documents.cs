using NodaTime;

namespace LeaseReminder.Domain;

/// <summary>
/// An uploaded lease file.
/// </summary>
/// <remarks>
/// The row and the stored object are two things, and the two deletion columns
/// reflect that: <see cref="DeletedAt"/> records that a user removed the
/// document, <see cref="PurgedAt"/> records that the bytes are actually gone
/// from the bucket. Section 11.7 needs to be able to demonstrate the second,
/// and a single flag could not.
/// </remarks>
public class LeaseDocument
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Null until approval. A document may be uploaded before anybody has
    /// decided which lease it describes, or whether it describes one at all.
    /// </summary>
    public Guid? LeaseId { get; set; }

    public string StorageBucket { get; set; } = string.Empty;

    /// <summary>Server-generated. Never derived from the uploaded filename.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>
    /// What the user called the file.
    /// </summary>
    /// <remarks>
    /// Kept for display only. It is attacker-controlled text and is never used
    /// to build a path, which is what <see cref="StoragePath"/> is for.
    /// </remarks>
    public string OriginalFilename { get; set; } = string.Empty;

    /// <summary>
    /// The editable label, or null to fall back to the filename.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OriginalFilename"/> on purpose. That field is
    /// evidence of what arrived and is never modified; this one exists because
    /// "Scan_20260104_0001.pdf" is not a name anybody can read in a list.
    /// </remarks>
    public string? DisplayName { get; set; }

    /// <summary>What the client claimed the file was.</summary>
    public string DeclaredMimeType { get; set; } = string.Empty;

    /// <summary>What the file signature actually says. Both are kept so a
    /// disagreement stays auditable.</summary>
    public string? DetectedMimeType { get; set; }

    public long FileSizeBytes { get; set; }

    public byte[]? Sha256 { get; set; }

    public int? PageCount { get; set; }

    public DocumentProcessingStatus ProcessingStatus { get; set; } =
        DocumentProcessingStatus.Uploaded;

    public MalwareScanStatus MalwareScanStatus { get; set; } = MalwareScanStatus.Pending;

    public Instant? ScanCompletedAt { get; set; }

    /// <summary>Operator-safe reason. Never contains document content.</summary>
    public string? ProcessingError { get; set; }

    public Instant? ProcessingStartedAt { get; set; }

    public Instant? ProcessingFinishedAt { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public Instant UploadedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Soft delete, so the audit trail survives.</summary>
    public Instant? DeletedAt { get; set; }

    /// <summary>Set when the object itself has been removed from storage.</summary>
    public Instant? PurgedAt { get; set; }
}

/// <summary>
/// One extraction run against a document.
/// </summary>
public class LeaseExtraction
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid LeaseDocumentId { get; set; }

    /// <summary>The field catalogue version used.</summary>
    public string ExtractionVersion { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? PromptVersion { get; set; }

    /// <summary>
    /// The model's structured output, verbatim.
    /// </summary>
    /// <remarks>
    /// Data, never instructions. Kept so an extraction can be re-parsed after
    /// a parser fix without paying for the model call again.
    /// </remarks>
    public string? ExtractedJson { get; set; }

    public ExtractionReviewStatus ReviewStatus { get; set; } = ExtractionReviewStatus.Pending;

    public Guid? ReviewedByUserId { get; set; }

    public Instant? ReviewedAt { get; set; }

    public Instant? StartedAt { get; set; }

    public Instant? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public int? PagesProcessed { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>
/// One candidate lease term, as produced by an extraction.
/// </summary>
/// <remarks>
/// The reviewable unit. <see cref="ExtractedValue"/> is what the model said
/// and <see cref="CorrectedValue"/> is what a human decided; both are kept, so
/// a correction never erases the evidence of what was corrected.
/// </remarks>
public class LeaseExtractedField
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ExtractionId { get; set; }

    /// <summary>Dotted path from the field catalogue, e.g. "rent.amount".</summary>
    public string FieldName { get; set; } = string.Empty;

    public string ValueType { get; set; } = string.Empty;

    public string? ExtractedValue { get; set; }

    /// <summary>
    /// The typed value after validation, as JSON. Null means it failed
    /// validation and a human must supply it.
    /// </summary>
    public string? NormalizedValue { get; set; }

    /// <summary>
    /// The model's self-reported confidence.
    /// </summary>
    /// <remarks>
    /// Review guidance only. It is not evidence of correctness, and the UI is
    /// written so that nothing can be approved on the strength of it alone.
    /// </remarks>
    public decimal? Confidence { get; set; }

    public ValueProvenance Provenance { get; set; } = ValueProvenance.StatedInDocument;

    public int? SourcePage { get; set; }

    public string? SourceExcerpt { get; set; }

    public FieldReviewStatus ReviewStatus { get; set; } = FieldReviewStatus.Pending;

    public string? CorrectedValue { get; set; }

    public Guid? CorrectedByUserId { get; set; }

    public Instant? CorrectedAt { get; set; }

    public string[] IssueCodes { get; set; } = [];

    public string? IssueNote { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>
/// A problem with a document that is not attributable to one field.
/// </summary>
public class LeaseExtractionIssue
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ExtractionId { get; set; }

    public string IssueCode { get; set; } = string.Empty;

    /// <summary>
    /// "info", "warning", or "blocking".
    /// </summary>
    /// <remarks>
    /// A blocking issue must be resolved before the lease can be approved,
    /// which is the mechanism by which "the model found two contradictory rent
    /// clauses" stops a lease activating on the wrong one.
    /// </remarks>
    public string Severity { get; set; } = "warning";

    public string Message { get; set; } = string.Empty;

    /// <summary>Dotted field paths this issue touches.</summary>
    public string[] RelatedFields { get; set; } = [];

    /// <summary>
    /// Pages the issue refers to.
    /// </summary>
    /// <remarks>
    /// Plural: a contradiction is interesting precisely because it spans two
    /// places, and a reviewer needs both to judge it.
    /// </remarks>
    public int[] SourcePages { get; set; } = [];

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public string? ResolutionNote { get; set; }

    public Instant CreatedAt { get; set; }
}
