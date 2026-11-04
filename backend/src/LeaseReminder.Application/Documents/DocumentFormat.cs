namespace LeaseReminder.Application.Documents;

/// <summary>
/// A file format this system accepts for lease documents.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. Every format here has to be parsed by the extraction
/// pipeline, and every format accepted is attack surface, so the list is the
/// shortest one that covers how leases actually arrive: a generated PDF, a
/// Word document, or a photograph of a signed page.
/// </para>
/// <para>
/// The same four values are enforced by a CHECK constraint on
/// <c>lease_documents.detected_mime_type</c>. If one is added here it must be
/// added there too, or the insert fails at the last line of defence rather
/// than at the first.
/// </para>
/// </remarks>
public enum DocumentFormat
{
    Pdf,
    Docx,
    Jpeg,
    Png,
}

/// <summary>
/// Why a file was refused.
/// </summary>
/// <remarks>
/// These are reported to the uploader, so each one has to be true without
/// being useful to somebody probing the validator. None of them echoes file
/// content back.
/// </remarks>
public enum FileRejectionReason
{
    /// <summary>Zero bytes, or too few to carry a recognisable signature.</summary>
    Empty,

    /// <summary>Beyond the configured ceiling.</summary>
    TooLarge,

    /// <summary>
    /// The leading bytes match none of the accepted formats. This is the
    /// normal outcome for a renamed file: an executable called "lease.pdf"
    /// lands here, because the extension was never consulted.
    /// </summary>
    UnrecognisedFormat,

    /// <summary>
    /// The content is a recognised format, but not the one the client said it
    /// was uploading. Treated as a refusal rather than a silent correction:
    /// a client whose declared type disagrees with its own bytes is either
    /// broken or probing, and neither deserves the benefit of the doubt.
    /// </summary>
    DeclaredTypeMismatch,

    /// <summary>
    /// The container is the right shape at the first few bytes but does not
    /// hold what that format requires. A ZIP that is not a Word document is
    /// the common case.
    /// </summary>
    MalformedContainer,

    /// <summary>
    /// The archive is structured in a way that would cost disproportionate
    /// resources to expand: too many entries, too much expansion, or a path
    /// that tries to escape the extraction root.
    /// </summary>
    SuspiciousArchive,
}

/// <summary>
/// The verdict on one uploaded file.
/// </summary>
public sealed record FileInspectionResult
{
    private FileInspectionResult()
    {
    }

    /// <summary>Whether the file may be stored and queued for extraction.</summary>
    public bool IsAccepted { get; private init; }

    /// <summary>The format established from the content, when accepted.</summary>
    public DocumentFormat? Format { get; private init; }

    /// <summary>
    /// The media type established from the content. This is the value that
    /// belongs in <c>detected_mime_type</c>; the client's claim belongs in
    /// <c>declared_mime_type</c>, and the two are stored separately so a
    /// disagreement stays auditable after the fact.
    /// </summary>
    public string? DetectedMimeType { get; private init; }

    /// <summary>Why it was refused, when it was.</summary>
    public FileRejectionReason? RejectionReason { get; private init; }

    /// <summary>
    /// A sentence for the uploader. Describes the validator's decision, never
    /// the file's contents.
    /// </summary>
    public string? RejectionDetail { get; private init; }

    /// <summary>The canonical file extension, used to build the storage path.</summary>
    public string? Extension => Format switch
    {
        DocumentFormat.Pdf => "pdf",
        DocumentFormat.Docx => "docx",
        DocumentFormat.Jpeg => "jpg",
        DocumentFormat.Png => "png",
        _ => null,
    };

    public static FileInspectionResult Accepted(DocumentFormat format) => new()
    {
        IsAccepted = true,
        Format = format,
        DetectedMimeType = MimeTypeFor(format),
    };

    public static FileInspectionResult Rejected(FileRejectionReason reason, string detail) => new()
    {
        IsAccepted = false,
        RejectionReason = reason,
        RejectionDetail = detail,
    };

    /// <summary>
    /// The media type for a format.
    /// </summary>
    /// <remarks>
    /// JPEG is "image/jpeg", never "image/jpg". The latter is not registered
    /// and would fail the database's CHECK constraint.
    /// </remarks>
    public static string MimeTypeFor(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "application/pdf",
        DocumentFormat.Docx =>
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        DocumentFormat.Jpeg => "image/jpeg",
        DocumentFormat.Png => "image/png",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Maps a client-declared media type onto a format, or null if it is not
    /// one this system accepts.
    /// </summary>
    /// <remarks>
    /// Parameters after a semicolon are discarded: browsers send things like
    /// <c>text/plain; charset=utf-8</c>, and a lease upload may arrive as
    /// <c>application/pdf; qs=0.001</c> through a proxy.
    /// </remarks>
    public static DocumentFormat? FormatForDeclaredMimeType(string? declaredMimeType)
    {
        if (string.IsNullOrWhiteSpace(declaredMimeType))
        {
            return null;
        }

        var separator = declaredMimeType.IndexOf(';', StringComparison.Ordinal);

        var bare = (separator >= 0 ? declaredMimeType[..separator] : declaredMimeType)
            .Trim()
            .ToLowerInvariant();

        return bare switch
        {
            "application/pdf" => DocumentFormat.Pdf,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
                DocumentFormat.Docx,
            "image/jpeg" => DocumentFormat.Jpeg,
            "image/png" => DocumentFormat.Png,
            _ => null,
        };
    }
}
