using System.IO.Compression;
using System.Text;

using LeaseReminder.Application.Documents;

using Xunit;

namespace LeaseReminder.UnitTests.Documents;

/// <summary>
/// Tests for the upload validator.
/// </summary>
/// <remarks>
/// <para>
/// This is a security boundary, so the cases that matter are the hostile ones:
/// a file that lies about what it is, an archive that costs more to open than
/// to send, and a container that is the right shape but the wrong contents.
/// The happy path is the easy part and gets proportionally less attention
/// here.
/// </para>
/// </remarks>
public sealed class FileSignatureInspectorTests
{
    private const string PdfMime = "application/pdf";

    private const string DocxMime =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // ---------------------------------------------------------------------
    // Recognising what a file actually is
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Recognises_a_pdf_from_its_signature()
    {
        var result = await InspectAsync(MinimalPdf(), PdfMime);

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentFormat.Pdf, result.Format);
        Assert.Equal("application/pdf", result.DetectedMimeType);
        Assert.Equal("pdf", result.Extension);
    }

    [Fact]
    public async Task Recognises_a_png_from_its_signature()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };

        var result = await InspectAsync(png, "image/png");

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentFormat.Png, result.Format);
    }

    /// <summary>
    /// The fourth byte of a JPEG is an encoder-dependent marker, so only the
    /// first three are checked. A photograph of a signed lease page may come
    /// from any camera or scanner, and refusing one because it writes E1
    /// instead of E0 would be a bug that only shows up for some users.
    /// </summary>
    [Theory]
    [InlineData(0xE0)] // JFIF
    [InlineData(0xE1)] // Exif, which is what a phone camera writes
    [InlineData(0xDB)] // Quantisation table first, seen from some scanners
    [InlineData(0xEE)] // Adobe
    public async Task Recognises_a_jpeg_whatever_marker_follows(byte marker)
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, marker, 0x00, 0x10, 0x4A, 0x46 };

        var result = await InspectAsync(jpeg, "image/jpeg");

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentFormat.Jpeg, result.Format);
    }

    [Fact]
    public async Task Recognises_a_word_document()
    {
        var result = await InspectAsync(MinimalDocx(), DocxMime);

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentFormat.Docx, result.Format);
        Assert.Equal("docx", result.Extension);
    }

    // ---------------------------------------------------------------------
    // Refusing what it is not
    // ---------------------------------------------------------------------

    /// <summary>
    /// The central claim of the whole class: the extension is not consulted.
    /// </summary>
    [Fact]
    public async Task Refuses_an_executable_renamed_to_pdf()
    {
        // "MZ", the DOS header every Windows executable still starts with.
        var executable = Encoding.ASCII.GetBytes("MZ\x90\0\x03\0\0\0\x04\0\0\0\xFF\xFF\0\0");

        var result = await InspectAsync(executable, PdfMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.UnrecognisedFormat, result.RejectionReason);
    }

    [Fact]
    public async Task Refuses_a_shell_script_renamed_to_pdf()
    {
        var script = Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf /\n");

        var result = await InspectAsync(script, PdfMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.UnrecognisedFormat, result.RejectionReason);
    }

    /// <summary>
    /// A PDF whose signature sits a few bytes in is not a PDF as far as any
    /// parser is concerned, and accepting it would mean the signature check
    /// could be sidestepped with a prefix.
    /// </summary>
    [Fact]
    public async Task Refuses_a_pdf_signature_that_is_not_at_the_start()
    {
        var padded = Encoding.ASCII.GetBytes("JUNK%PDF-1.7\n");

        var result = await InspectAsync(padded, PdfMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.UnrecognisedFormat, result.RejectionReason);
    }

    /// <summary>
    /// Disagreement between content and declaration is refused outright. The
    /// alternative, trusting the content and ignoring the header, would let a
    /// client discover which parser runs for which bytes.
    /// </summary>
    [Fact]
    public async Task Refuses_a_pdf_declared_as_a_png()
    {
        var result = await InspectAsync(MinimalPdf(), "image/png");

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.DeclaredTypeMismatch, result.RejectionReason);
    }

    /// <summary>
    /// An unrecognised declaration is not itself a reason to refuse: the
    /// content is what decides. Browsers and proxies send
    /// "application/octet-stream" for a drag-and-dropped file often enough
    /// that treating it as a mismatch would reject ordinary uploads.
    /// </summary>
    [Fact]
    public async Task Accepts_a_real_pdf_declared_as_octet_stream()
    {
        var result = await InspectAsync(MinimalPdf(), "application/octet-stream");

        Assert.True(result.IsAccepted);
        Assert.Equal(DocumentFormat.Pdf, result.Format);
    }

    [Theory]
    [InlineData("application/pdf; charset=binary")]
    [InlineData("APPLICATION/PDF")]
    [InlineData("  application/pdf  ")]
    public async Task Tolerates_parameters_and_casing_in_the_declared_type(string declared)
    {
        var result = await InspectAsync(MinimalPdf(), declared);

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public async Task Refuses_an_empty_file()
    {
        var result = await InspectAsync([], PdfMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.Empty, result.RejectionReason);
    }

    // ---------------------------------------------------------------------
    // ZIP containers that are not Word documents
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every OPC format starts with the same four bytes, so the signature
    /// alone cannot tell a lease from a spreadsheet. The container has to be
    /// opened.
    /// </summary>
    [Fact]
    public async Task Refuses_a_spreadsheet_declared_as_a_word_document()
    {
        var xlsx = BuildZip(
            ("[Content_Types].xml", "<Types/>"),
            ("xl/workbook.xml", "<workbook/>"));

        var result = await InspectAsync(xlsx, DocxMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.MalformedContainer, result.RejectionReason);
    }

    [Fact]
    public async Task Refuses_a_plain_folder_archive()
    {
        var zip = BuildZip(("notes.txt", "nothing to see"));

        var result = await InspectAsync(zip, DocxMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.MalformedContainer, result.RejectionReason);
    }

    [Fact]
    public async Task Refuses_a_truncated_word_document()
    {
        var full = MinimalDocx();
        // Keep the local file header so it is still recognised as a ZIP, but
        // destroy the central directory at the end.
        var truncated = full[..(full.Length / 2)];

        var result = await InspectAsync(truncated, DocxMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.MalformedContainer, result.RejectionReason);
    }

    /// <summary>
    /// Zip-slip: an entry name that escapes the directory it is expanded
    /// into. The extraction pipeline joins these names onto a path, so a
    /// traversing entry is a write outside the intended root.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("word/../../../escape.xml")]
    public async Task Refuses_an_archive_entry_that_escapes_its_container(string hostileName)
    {
        var zip = BuildZip(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<document/>"),
            (hostileName, "payload"));

        var result = await InspectAsync(zip, DocxMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.SuspiciousArchive, result.RejectionReason);
    }

    /// <summary>
    /// A zip bomb. Highly compressible content is checked from the central
    /// directory's declared lengths, so this is refused without decompressing
    /// anything.
    /// </summary>
    [Fact]
    public async Task Refuses_an_archive_that_expands_enormously()
    {
        // 8 MB of zeroes compresses to a few kilobytes: a ratio far past the
        // 200:1 cap, on an entry well past the small-file floor.
        var bomb = BuildZip(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<document/>"),
            ("word/media/payload.bin", new string('\0', 8 * 1024 * 1024)));

        var result = await InspectAsync(bomb, DocxMime);

        Assert.False(result.IsAccepted);
        Assert.Equal(FileRejectionReason.SuspiciousArchive, result.RejectionReason);
    }

    /// <summary>
    /// The counterpart to the test above, and the reason the ratio check has a
    /// floor. A tiny part can exceed any ratio while being completely
    /// harmless, and a real Word document is full of them.
    /// </summary>
    [Fact]
    public async Task Accepts_a_document_with_small_highly_compressible_parts()
    {
        var document = BuildZip(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<document/>"),
            // ~2 KB of one repeated character: compresses to almost nothing,
            // so the ratio is extreme, but it is under the floor.
            ("word/styles.xml", new string(' ', 2000)));

        var result = await InspectAsync(document, DocxMime);

        Assert.True(result.IsAccepted);
    }

    // ---------------------------------------------------------------------
    // Stream handling
    // ---------------------------------------------------------------------

    /// <summary>
    /// The caller streams the same instance on to storage after inspection, so
    /// leaving the position anywhere but the start would upload a truncated
    /// file. The DOCX path is the one at risk, because ZipArchive moves the
    /// position around and would dispose the stream if not told otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Leaves_the_stream_rewound_and_open(bool useDocx)
    {
        var bytes = useDocx ? MinimalDocx() : MinimalPdf();
        using var stream = new MemoryStream(bytes);

        await FileSignatureInspector.InspectAsync(stream, useDocx ? DocxMime : PdfMime);

        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Equal(bytes.Length, stream.Length);
    }

    /// <summary>
    /// A non-seekable stream cannot be inspected and then re-read, so the
    /// mistake is refused loudly at the call site rather than producing a
    /// zero-byte upload at runtime.
    /// </summary>
    [Fact]
    public async Task Refuses_a_stream_it_cannot_rewind()
    {
        using var forwardOnly = new ForwardOnlyStream(MinimalPdf());

        await Assert.ThrowsAsync<ArgumentException>(
            () => FileSignatureInspector.InspectAsync(forwardOnly, PdfMime));
    }

    /// <summary>
    /// Inspection honours a non-zero starting position, so a file embedded in
    /// a larger buffer is read from where the caller left it.
    /// </summary>
    [Fact]
    public async Task Inspects_from_the_streams_current_position()
    {
        var prefixed = new byte[8].Concat(MinimalPdf()).ToArray();
        using var stream = new MemoryStream(prefixed) { Position = 8 };

        var result = await FileSignatureInspector.InspectAsync(stream, PdfMime);

        Assert.True(result.IsAccepted);
        Assert.Equal(8, stream.Position);
    }

    // ---------------------------------------------------------------------
    // Internal consistency
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every format must round-trip through its own media type, or a detected
    /// format would be written to a column whose CHECK constraint refuses it.
    /// </summary>
    [Theory]
    [InlineData(DocumentFormat.Pdf)]
    [InlineData(DocumentFormat.Docx)]
    [InlineData(DocumentFormat.Jpeg)]
    [InlineData(DocumentFormat.Png)]
    public void Every_format_round_trips_through_its_media_type(DocumentFormat format)
    {
        var mime = FileInspectionResult.MimeTypeFor(format);

        Assert.Equal(format, FileInspectionResult.FormatForDeclaredMimeType(mime));
        Assert.NotNull(FileInspectionResult.Accepted(format).Extension);
    }

    /// <summary>
    /// "image/jpg" is not a registered media type and is not in the database's
    /// allowlist. Accepting it here would produce a row the database refuses.
    /// </summary>
    [Theory]
    [InlineData("image/jpg")]
    [InlineData("application/msword")]
    [InlineData("text/html")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_recognise_types_outside_the_allowlist(string? mime)
    {
        Assert.Null(FileInspectionResult.FormatForDeclaredMimeType(mime));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<FileInspectionResult> InspectAsync(byte[] bytes, string declared)
    {
        using var stream = new MemoryStream(bytes);

        return await FileSignatureInspector.InspectAsync(stream, declared);
    }

    private static byte[] MinimalPdf() =>
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    private static byte[] MinimalDocx() => BuildZip(
        ("[Content_Types].xml",
            """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>"""),
        ("word/document.xml",
            """<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>"""));

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A stream that cannot be rewound, like a live request body.</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            this.inner.Read(buffer, offset, count);

        public override void Flush() => this.inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
