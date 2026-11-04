using System.IO.Compression;

namespace LeaseReminder.Application.Documents;

/// <summary>
/// Establishes what an uploaded file actually is, from its content.
/// </summary>
/// <remarks>
/// <para>
/// Section 4.C requires uploaded documents to be treated as untrusted input.
/// The first consequence is that nothing the client says about a file is
/// believed: not the filename, not the extension, not the Content-Type
/// header. All three are attacker-controlled. Only the bytes are evidence.
/// </para>
/// <para>
/// The declared type is still used, but only as a second opinion: a file
/// whose content disagrees with its own declaration is refused rather than
/// quietly reclassified. Silently accepting the content type would mean a
/// client could upload a PDF labelled as a PNG and watch which pipeline runs,
/// which is a free probe of the extraction path.
/// </para>
/// <para>
/// What this does NOT do is decide whether the file is safe to open. It
/// establishes the format so the right parser is chosen and so a renamed
/// executable never reaches one. Malware scanning is a separate state machine
/// on the same row (<c>malware_scan_status</c>), and the database refuses to
/// let a document reach extraction until that has returned clean or skipped.
/// </para>
/// </remarks>
public static class FileSignatureInspector
{
    /// <summary>
    /// The largest file accepted, matching the CHECK constraint on
    /// <c>lease_documents.file_size_bytes</c>. Kept in both places on purpose:
    /// here so the uploader gets a useful message, there so a code path that
    /// forgets to ask cannot write an oversized row.
    /// </summary>
    public const long MaximumFileSizeBytes = 52_428_800; // 50 MiB

    /// <summary>
    /// Enough bytes to identify every format handled here. The longest
    /// signature consulted is PNG's, at eight bytes; the rest is margin so
    /// this does not need revisiting for a format with a longer one.
    /// </summary>
    public const int SignatureLengthBytes = 32;

    // ----- Zip expansion limits (DOCX) --------------------------------------
    //
    // A DOCX is a ZIP, and a ZIP can be crafted to expand to far more than it
    // costs to send. These caps are generous for a real Word document and
    // cheap to check, because the central directory carries both the
    // compressed and uncompressed length of every entry: the archive can be
    // rejected without decompressing a single byte.

    /// <summary>A large Word document has tens of parts, not thousands.</summary>
    private const int MaximumArchiveEntries = 2_048;

    /// <summary>Total expanded size across all entries.</summary>
    private const long MaximumArchiveExpandedBytes = 209_715_200; // 200 MiB

    /// <summary>
    /// Per-entry expansion ratio. XML compresses extremely well, so this has
    /// to be loose; 200:1 still refuses the classic nested-zero archives,
    /// which run to six figures.
    /// </summary>
    private const long MaximumArchiveExpansionRatio = 200;

    /// <summary>Entries smaller than this are exempt from the ratio check.</summary>
    /// <remarks>
    /// A short, highly compressible part can exceed any ratio while being
    /// harmless: 60 bytes expanding to 20 KB is a 300:1 ratio and no threat.
    /// The absolute cap above is what bounds the real risk.
    /// </remarks>
    private const long RatioCheckFloorBytes = 4_096;

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    private static ReadOnlySpan<byte> ZipLocalFileHeader => [0x50, 0x4B, 0x03, 0x04];

    private static ReadOnlySpan<byte> ZipEmptyArchive => [0x50, 0x4B, 0x05, 0x06];

    private static ReadOnlySpan<byte> ZipSpannedArchive => [0x50, 0x4B, 0x07, 0x08];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Inspects a file and decides whether it may be stored.
    /// </summary>
    /// <param name="content">
    /// The uploaded bytes. Must be seekable and is left rewound to its
    /// starting position, so the caller can stream the same instance onward to
    /// storage without re-reading it from the network.
    /// </param>
    /// <param name="declaredMimeType">
    /// What the client said it was sending. Used only to detect disagreement.
    /// </param>
    public static async Task<FileInspectionResult> InspectAsync(
        Stream content,
        string? declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanSeek)
        {
            throw new ArgumentException(
                "The content stream must be seekable so the signature can be read without " +
                "consuming the upload. Buffer it to a temporary file first.",
                nameof(content));
        }

        var origin = content.Position;
        var length = content.Length - origin;

        if (length <= 0)
        {
            return FileInspectionResult.Rejected(
                FileRejectionReason.Empty,
                "The file is empty.");
        }

        if (length > MaximumFileSizeBytes)
        {
            return FileInspectionResult.Rejected(
                FileRejectionReason.TooLarge,
                $"The file is larger than the {MaximumFileSizeBytes / 1024 / 1024} MB limit.");
        }

        var signature = new byte[(int)Math.Min(SignatureLengthBytes, length)];
        await content.ReadExactlyAsync(signature, cancellationToken).ConfigureAwait(false);
        content.Position = origin;

        var detected = DetectFormat(signature);

        if (detected is null)
        {
            return FileInspectionResult.Rejected(
                FileRejectionReason.UnrecognisedFormat,
                "The file is not a PDF, Word document, JPEG, or PNG. Renaming a file does " +
                "not change what it is, and the contents are what is checked here.");
        }

        // A ZIP signature is necessary for a DOCX but nowhere near sufficient:
        // XLSX, PPTX, ODT, JAR and a plain folder archive all start the same
        // way. The container has to be opened to tell them apart.
        if (detected == DocumentFormat.Docx)
        {
            var archive = InspectOpenXmlArchive(content, origin);

            if (archive is not null)
            {
                return archive;
            }
        }

        var declared = FileInspectionResult.FormatForDeclaredMimeType(declaredMimeType);

        if (declared is not null && declared != detected)
        {
            return FileInspectionResult.Rejected(
                FileRejectionReason.DeclaredTypeMismatch,
                $"The file was sent as {declaredMimeType} but its contents are a " +
                $"{DescribeFormat(detected.Value)}.");
        }

        return FileInspectionResult.Accepted(detected.Value);
    }

    /// <summary>
    /// Matches the leading bytes against each accepted format.
    /// </summary>
    /// <remarks>
    /// Order matters only in that no two signatures here overlap, which is
    /// checked by the tests rather than assumed.
    /// </remarks>
    private static DocumentFormat? DetectFormat(ReadOnlySpan<byte> signature)
    {
        if (signature.StartsWith(PdfSignature))
        {
            return DocumentFormat.Pdf;
        }

        if (signature.StartsWith(PngSignature))
        {
            return DocumentFormat.Png;
        }

        // JPEG's fourth byte is a marker that varies by encoder (E0 for JFIF,
        // E1 for Exif, DB for some cameras), so only the first three bytes are
        // a reliable signature.
        if (signature.StartsWith(JpegSignature))
        {
            return DocumentFormat.Jpeg;
        }

        // Any of the three ZIP record signatures. The empty and spanned
        // variants cannot be a valid DOCX, but they are recognised here so
        // they are reported as a malformed Word document rather than as an
        // unknown format, which is the more accurate message.
        if (signature.StartsWith(ZipLocalFileHeader)
            || signature.StartsWith(ZipEmptyArchive)
            || signature.StartsWith(ZipSpannedArchive))
        {
            return DocumentFormat.Docx;
        }

        return null;
    }

    /// <summary>
    /// Confirms a ZIP really is a WordprocessingML document, and that
    /// expanding it is affordable.
    /// </summary>
    /// <returns>A rejection, or null if the archive is acceptable.</returns>
    private static FileInspectionResult? InspectOpenXmlArchive(Stream content, long origin)
    {
        try
        {
            // leaveOpen, because the caller still needs this stream to upload
            // the file; ZipArchive would otherwise dispose it out from under
            // them.
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

            var entryCount = 0;
            long expandedBytes = 0;
            var hasContentTypes = false;
            var hasDocumentPart = false;

            foreach (var entry in archive.Entries)
            {
                entryCount++;

                if (entryCount > MaximumArchiveEntries)
                {
                    return FileInspectionResult.Rejected(
                        FileRejectionReason.SuspiciousArchive,
                        "The document contains far more internal parts than a Word file has.");
                }

                // Zip-slip. No legitimate OPC part name is absolute or walks
                // upwards, and the extraction pipeline joins these onto a
                // directory.
                if (entry.FullName.Contains("..", StringComparison.Ordinal)
                    || entry.FullName.StartsWith('/')
                    || entry.FullName.StartsWith('\\')
                    || entry.FullName.Contains(':', StringComparison.Ordinal))
                {
                    return FileInspectionResult.Rejected(
                        FileRejectionReason.SuspiciousArchive,
                        "The document contains an internal path that tries to escape its " +
                        "own container.");
                }

                expandedBytes += entry.Length;

                if (expandedBytes > MaximumArchiveExpandedBytes)
                {
                    return FileInspectionResult.Rejected(
                        FileRejectionReason.SuspiciousArchive,
                        "The document expands to far more than its size suggests.");
                }

                if (entry.CompressedLength > 0
                    && entry.Length > RatioCheckFloorBytes
                    && entry.Length / entry.CompressedLength > MaximumArchiveExpansionRatio)
                {
                    return FileInspectionResult.Rejected(
                        FileRejectionReason.SuspiciousArchive,
                        "The document expands to far more than its size suggests.");
                }

                // The two parts that make a ZIP a Word document. [Content_Types].xml
                // is required of every OPC package; word/document.xml is what
                // distinguishes WordprocessingML from a spreadsheet or a deck.
                if (entry.FullName.Equals("[Content_Types].xml", StringComparison.Ordinal))
                {
                    hasContentTypes = true;
                }
                else if (entry.FullName.Equals("word/document.xml", StringComparison.Ordinal))
                {
                    hasDocumentPart = true;
                }
            }

            if (!hasContentTypes || !hasDocumentPart)
            {
                return FileInspectionResult.Rejected(
                    FileRejectionReason.MalformedContainer,
                    "The file is a ZIP archive but not a Word document. A spreadsheet, a " +
                    "presentation, or a renamed folder archive will land here.");
            }

            return null;
        }
        catch (InvalidDataException)
        {
            // Truncated, or the central directory is corrupt. Either way the
            // extraction pipeline could not read it.
            return FileInspectionResult.Rejected(
                FileRejectionReason.MalformedContainer,
                "The Word document is damaged and could not be opened.");
        }
        finally
        {
            content.Position = origin;
        }
    }

    private static string DescribeFormat(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "PDF",
        DocumentFormat.Docx => "Word document",
        DocumentFormat.Jpeg => "JPEG image",
        DocumentFormat.Png => "PNG image",
        _ => "file of an unknown kind",
    };
}
