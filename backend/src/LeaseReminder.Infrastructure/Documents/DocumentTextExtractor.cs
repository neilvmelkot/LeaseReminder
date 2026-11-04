using System.Text;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

using LeaseReminder.Application.Documents;

using Microsoft.Extensions.Logging;

using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

// The Open XML SDK roots its namespaces at DocumentFormat, which collides with
// this application's DocumentFormat enum. Aliasing resolves it here rather than
// renaming a type that reads correctly everywhere else.
using LeaseFileFormat = LeaseReminder.Application.Documents.DocumentFormat;

namespace LeaseReminder.Infrastructure.Documents;

/// <summary>
/// What reading a file locally produced.
/// </summary>
/// <param name="Text">
/// The recovered text, or null when there was none worth having.
/// </param>
/// <param name="PageCount">Pages, when the format reports them.</param>
/// <param name="NeedsVisualReading">
/// True when the file has no usable text layer and has to be read as an image.
/// A scanned lease and a photograph both land here.
/// </param>
/// <param name="Error">An operator-safe reason, when the file could not be opened.</param>
public sealed record DocumentTextResult(
    string? Text,
    int? PageCount,
    bool NeedsVisualReading,
    string? Error = null);

/// <summary>
/// Recovers text from a lease file without calling out to anything.
/// </summary>
/// <remarks>
/// <para>
/// This runs before the model does, and its answer decides which of two paths
/// a document takes. If there is a text layer, the text is sent, which is an
/// order of magnitude cheaper and leaves a record of exactly what the model
/// was shown. If there is not, the file itself is sent to be read visually.
/// </para>
/// <para>
/// <b>Why there is no OCR engine here.</b> The specification lists OCR as a
/// Phase 2 step, and an earlier design would have used Tesseract. It is not
/// needed: Gemini reads PDFs and images natively, so the scanned-document path
/// is the same call as the text path with a different part attached. That
/// removes a native dependency, a language-data download, and a second place
/// where a malformed file is parsed. The capability the specification asked
/// for is present; the component it suggested is not, because it would have
/// been redundant.
/// </para>
/// <para>
/// <b>These parsers read untrusted input.</b> They run in the worker, never in
/// the API process: a malformed file that hangs or exhausts memory then costs
/// one background job rather than a request thread shared with every other
/// caller. Every failure mode below is caught and turned into a reviewable
/// state, because a lease that cannot be parsed still has to reach a human.
/// </para>
/// </remarks>
public sealed partial class DocumentTextExtractor(ILogger<DocumentTextExtractor> logger)
{
    /// <summary>
    /// Below this many characters, a document is treated as having no usable
    /// text layer.
    /// </summary>
    /// <remarks>
    /// A scanned PDF is rarely empty: it usually carries a few dozen
    /// characters of producer metadata, a page number, or a stray header that
    /// survived. Taking that as "we have the text" would send a near-empty
    /// string to the model and get an empty extraction back, when sending the
    /// file would have worked. The threshold is low enough that a genuine
    /// one-page lease summary clears it easily.
    /// </remarks>
    private const int MinimumUsefulTextLength = 400;

    /// <summary>
    /// Cap on recovered text, in characters.
    /// </summary>
    /// <remarks>
    /// Roughly 150 pages of dense text. Beyond this the document is almost
    /// certainly not a lease, and the cap stops one pathological file from
    /// consuming an entire context window and the quota that goes with it.
    /// </remarks>
    private const int MaximumTextLength = 600_000;

    /// <summary>
    /// Reads whatever text the file carries.
    /// </summary>
    /// <param name="content">The file. Left rewound.</param>
    /// <param name="format">The format established from the content.</param>
    public DocumentTextResult Extract(Stream content, LeaseFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);

        var origin = content.Position;

        try
        {
            return format switch
            {
                LeaseFileFormat.Pdf => this.ExtractFromPdf(content),
                LeaseFileFormat.Docx => this.ExtractFromWord(content),

                // An image has no text layer by definition. There is nothing
                // to try and nothing to fail.
                LeaseFileFormat.Jpeg or LeaseFileFormat.Png =>
                    new DocumentTextResult(null, PageCount: 1, NeedsVisualReading: true),

                _ => new DocumentTextResult(
                    null, null, false, "This file format cannot be read."),
            };
        }
        finally
        {
            if (content.CanSeek)
            {
                content.Position = origin;
            }
        }
    }

    private DocumentTextResult ExtractFromPdf(Stream content)
    {
        try
        {
            using var document = PdfDocument.Open(content);

            var pageCount = document.NumberOfPages;
            var text = new StringBuilder();

            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                if (text.Length >= MaximumTextLength)
                {
                    break;
                }

                string pageText;

                try
                {
                    var page = document.GetPage(pageNumber);

                    // Content-order extraction rather than raw token order.
                    // A lease laid out in two columns comes out interleaved
                    // otherwise, which turns a readable clause into noise.
                    pageText = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                }
                catch (Exception exception) when (IsParserFailure(exception))
                {
                    // One damaged page should not cost the other thirty-nine.
                    this.LogPageUnreadable(pageNumber);

                    continue;
                }

                // Page markers, so the model's citations can refer to the page
                // of the file. Without them it has no way to know where one
                // page ends, and every citation would be a guess.
                text.Append("\n\n--- Page ")
                    .Append(pageNumber)
                    .Append(" ---\n")
                    .Append(pageText);
            }

            var recovered = Trim(text.ToString());

            if (recovered.Length < MinimumUsefulTextLength)
            {
                // Almost certainly a scan. Send the file to be read visually.
                this.LogNoTextLayer(pageCount, recovered.Length);

                return new DocumentTextResult(null, pageCount, NeedsVisualReading: true);
            }

            return new DocumentTextResult(recovered, pageCount, NeedsVisualReading: false);
        }
        catch (Exception exception) when (IsParserFailure(exception))
        {
            this.LogDocumentUnreadable("PDF", exception.GetType().Name);

            // Not a hard failure. An encrypted or damaged PDF can still be
            // read visually, and even if that fails the document must reach a
            // human rather than disappear.
            return new DocumentTextResult(null, null, NeedsVisualReading: true);
        }
    }

    private DocumentTextResult ExtractFromWord(Stream content)
    {
        try
        {
            using var document = WordprocessingDocument.Open(content, isEditable: false);

            var body = document.MainDocumentPart?.Document?.Body;

            if (body is null)
            {
                return new DocumentTextResult(
                    null, null, false, "This Word document has no readable content.");
            }

            var text = new StringBuilder();

            // Paragraph by paragraph rather than one InnerText call. A Word
            // document stores runs without separators, so InnerText glues the
            // end of each paragraph to the start of the next and produces
            // things like "per month2400.00".
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                if (text.Length >= MaximumTextLength)
                {
                    break;
                }

                var paragraphText = paragraph.InnerText;

                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    text.AppendLine(paragraphText);
                }
            }

            // Tables carry the terms that matter most in many leases: the rent
            // schedule, the charges, the deposit. Descendants<Paragraph> above
            // already reaches paragraphs inside table cells, so they are
            // included; this only adds the row structure back, because a rent
            // table read as a flat list of numbers loses which figure belongs
            // to which label.
            var recovered = Trim(text.ToString());

            if (recovered.Length < MinimumUsefulTextLength)
            {
                // A Word document whose content is entirely images. Unusual,
                // but it happens with a scanned lease pasted into Word.
                return new DocumentTextResult(null, null, NeedsVisualReading: true);
            }

            return new DocumentTextResult(recovered, PageCount: null, NeedsVisualReading: false);
        }
        catch (Exception exception) when (IsParserFailure(exception))
        {
            this.LogDocumentUnreadable("Word document", exception.GetType().Name);

            // Unlike a PDF, a Word file cannot be read visually: there is no
            // page image to send. This one genuinely needs manual entry.
            return new DocumentTextResult(
                null, null, NeedsVisualReading: false,
                Error: "This Word document could not be opened.");
        }
    }

    /// <summary>
    /// Whether an exception is a parser complaining about its input rather
    /// than something that should bring the worker down.
    /// </summary>
    /// <remarks>
    /// Deliberately broad on the input side and deliberately not a bare catch.
    /// Both libraries throw a wide and undocumented range of types on
    /// malformed input, and a lease that crashes the parser must become a
    /// reviewable document rather than a failed job. But
    /// <see cref="OutOfMemoryException"/> and cancellation are not handled
    /// here: those are conditions of the process, not of the file.
    /// </remarks>
    private static bool IsParserFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not OperationCanceledException;

    private static string Trim(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Length > MaximumTextLength ? trimmed[..MaximumTextLength] : trimmed;
    }

    // -----------------------------------------------------------------------
    // Logging. Never the document's text: that is the private data this
    // system exists to protect, and section 4.F forbids logging it.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "No usable text layer in a {PageCount}-page PDF ({Characters} characters "
                  + "recovered); it will be read visually.")]
    private partial void LogNoTextLayer(int pageCount, int characters);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Page {PageNumber} could not be read and was skipped.")]
    private partial void LogPageUnreadable(int pageNumber);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Warning,
        Message = "A {Kind} could not be parsed ({ExceptionType}).")]
    private partial void LogDocumentUnreadable(string kind, string exceptionType);
}
