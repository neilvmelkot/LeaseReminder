using System.Text.Json;
using System.Text.Json.Nodes;

using LeaseReminder.Application.Documents;
using LeaseReminder.Infrastructure.Documents;
using LeaseReminder.Infrastructure.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using LeaseFileFormat = LeaseReminder.Application.Documents.DocumentFormat;

namespace LeaseReminder.Worker;

/// <summary>
/// Reads uploaded lease documents and records what they say.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline, in order: claim a document, fetch it from storage, try to
/// recover its text locally, send either that text or the file itself to the
/// model, validate the answer, and write the whole result in one transaction.
/// </para>
/// <para>
/// <b>Nothing here activates anything.</b> A completed extraction leaves the
/// document at <c>review_ready</c> and the lease exactly where it was. The
/// only path onwards is a human approving it, which is section 13.C and is
/// enforced by the database rather than by this class remembering to be
/// careful.
/// </para>
/// <para>
/// <b>Every failure keeps the document.</b> A lease that could not be read is
/// still a lease somebody uploaded and needs to deal with, so each failure
/// path records an operator-safe reason and leaves the row visible. Nothing
/// here deletes or hides a document.
/// </para>
/// </remarks>
public sealed partial class DocumentExtractionService(
    WorkerDatabase database,
    IDocumentStorage storage,
    DocumentTextExtractor textExtractor,
    ILeaseExtractor leaseExtractor,
    ILogger<DocumentExtractionService> logger) : BackgroundService
{
    /// <summary>
    /// How many documents to take at once.
    /// </summary>
    /// <remarks>
    /// Small. Each one is a slow call to an external service, and a large
    /// batch would hold claims open for minutes while other workers idle.
    /// </remarks>
    private const int BatchSize = 3;

    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!leaseExtractor.IsConfigured)
        {
            // Not an error. A deployment without an extraction key works
            // perfectly well; documents upload, store, and wait for someone to
            // enter the terms by hand. Saying so once at startup is more
            // useful than failing a document every twenty seconds forever.
            this.LogExtractionDisabled();

            return;
        }

        if (!storage.IsConfigured)
        {
            this.LogStorageDisabled();

            return;
        }

        this.LogStarted(leaseExtractor.ProviderName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;

            try
            {
                var documents = await database
                    .ClaimDocumentsForExtractionAsync(BatchSize, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var document in documents)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await this.ProcessAsync(document, stoppingToken).ConfigureAwait(false);
                    processed++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The loop must survive anything one batch does. A document
                // left in 'extracting' is recovered by the stuck-document
                // sweep rather than by crashing the worker.
                this.LogBatchFailed(exception.GetType().Name, exception.Message);
            }

            try
            {
                await Task
                    .Delay(processed > 0 ? BusyInterval : IdleInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(ClaimedDocument document, CancellationToken cancellationToken)
    {
        this.LogProcessing(document.DocumentId);

        var content = await storage
            .DownloadAsync(document.StoragePath, cancellationToken)
            .ConfigureAwait(false);

        if (content is not { } bytes)
        {
            // Retryable: storage being briefly unreachable is not the
            // document's fault, and failing it permanently would mean a
            // transient outage silently cost a user their upload.
            await database.FailExtractionAsync(
                document.DocumentId,
                "The document could not be retrieved from storage.",
                isRetryable: true,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var format = FormatFor(document.DetectedMimeType);

        if (format is null)
        {
            await database.FailExtractionAsync(
                document.DocumentId,
                "This file type cannot be read automatically.",
                isRetryable: false,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        // Local text first. It is far cheaper than sending the file, and it
        // leaves a record of exactly what the model was shown.
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);

        var text = textExtractor.Extract(stream, format.Value);

        if (text.Error is not null && !text.NeedsVisualReading)
        {
            await database.FailExtractionAsync(
                document.DocumentId, text.Error, isRetryable: false, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var request = new ExtractionRequest(
            // The file is attached only when there was no usable text. This is
            // the scanned-lease and photograph path.
            Content: text.NeedsVisualReading ? bytes : null,
            ContentMediaType: text.NeedsVisualReading ? document.DetectedMimeType : null,
            RecoveredText: text.Text,
            PageCount: text.PageCount);

        var response = await leaseExtractor
            .ExtractAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            await database.FailExtractionAsync(
                document.DocumentId,
                response.Error ?? "The document could not be read.",
                response.IsRetryable,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var result = ExtractionResultParser.Parse(response.RawJson, text.PageCount);

        await database.RecordExtractionResultAsync(
            document.DocumentId,
            LeaseFieldCatalog.Version,
            leaseExtractor.ProviderName,
            response.Model,
            ExtractionPrompt.Version,
            response.RawJson,
            SerialiseFields(result),
            SerialiseIssues(result),
            text.PageCount,
            cancellationToken).ConfigureAwait(false);

        this.LogExtracted(
            document.DocumentId,
            result.Fields.Count,
            result.Issues.Count,
            result.MissingRequiredFields.Count);
    }

    /// <summary>
    /// Shapes the parsed fields for <c>app.record_extraction_result</c>.
    /// </summary>
    private static string SerialiseFields(ExtractionResult result)
    {
        var array = new JsonArray();

        foreach (var field in result.Fields)
        {
            var codes = new JsonArray();

            foreach (var code in field.IssueCodes)
            {
                codes.Add(code);
            }

            array.Add(new JsonObject
            {
                ["name"] = field.FieldName,
                ["value_type"] = field.ValueType,
                ["extracted_value"] = field.ExtractedValue,
                // DeepClone, because a JsonNode may not be attached to two
                // parents and the parsed value already belongs to its own tree.
                ["normalized_value"] = field.NormalizedValue?.DeepClone(),
                ["confidence"] = field.Confidence,
                ["provenance"] = field.Provenance,
                ["source_page"] = field.SourcePage,
                ["source_excerpt"] = field.SourceExcerpt,
                ["issue_codes"] = codes,
            });
        }

        return array.ToJsonString();
    }

    private static string SerialiseIssues(ExtractionResult result)
    {
        var array = new JsonArray();

        foreach (var issue in result.Issues)
        {
            var pages = new JsonArray();

            if (issue.SourcePage is { } page)
            {
                pages.Add(page);
            }

            array.Add(new JsonObject
            {
                ["code"] = issue.Code,
                ["severity"] = SeverityFor(issue.Code),
                ["message"] = issue.Detail,
                ["source_pages"] = pages,
            });
        }

        // Not an issue the model reported, but one this pipeline knows: the
        // document does not look like a lease. Worth blocking on, because
        // approving it would attach reminders to something that is not a
        // tenancy.
        if (result.LooksLikeSomethingOtherThanALease)
        {
            array.Add(new JsonObject
            {
                ["code"] = "incomplete_document",
                ["severity"] = "blocking",
                ["message"] =
                    "This does not appear to be a lease agreement. Check that the right "
                    + "file was uploaded before approving anything from it.",
                ["source_pages"] = new JsonArray(),
            });
        }

        return array.ToJsonString();
    }

    /// <summary>
    /// How much weight an issue carries.
    /// </summary>
    /// <remarks>
    /// Only two kinds block approval outright. A contradiction between clauses
    /// means the model had to choose, and approving without resolving it means
    /// a human never saw the choice. Embedded instructions mean the document
    /// tried to steer the reading of itself, which nobody should approve
    /// without looking at the page in question.
    /// </remarks>
    private static string SeverityFor(string code) => code switch
    {
        "conflicting_clause" or "embedded_instructions" => "blocking",
        "incomplete_document" or "unsupported_language" => "warning",
        _ => "info",
    };

    private static LeaseFileFormat? FormatFor(string? mimeType) => mimeType switch
    {
        "application/pdf" => LeaseFileFormat.Pdf,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
            LeaseFileFormat.Docx,
        "image/jpeg" => LeaseFileFormat.Jpeg,
        "image/png" => LeaseFileFormat.Png,
        _ => null,
    };

    // -----------------------------------------------------------------------
    // Logging. Document ids and counts only: never a lease term, never an
    // excerpt, never a filename.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Information,
        Message = "Document extraction started using {Provider}.")]
    private partial void LogStarted(string provider);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Document extraction is disabled: no extraction provider is configured. "
                  + "Uploads will be stored for manual entry.")]
    private partial void LogExtractionDisabled();

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Warning,
        Message = "Document extraction is disabled: document storage is not configured.")]
    private partial void LogStorageDisabled();

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Debug,
        Message = "Extracting document {DocumentId}.")]
    private partial void LogProcessing(Guid documentId);

    [LoggerMessage(
        EventId = 3404,
        Level = LogLevel.Information,
        Message = "Extracted document {DocumentId}: {FieldCount} field(s), {IssueCount} "
                  + "issue(s), {MissingCount} required field(s) not found.")]
    private partial void LogExtracted(
        Guid documentId, int fieldCount, int issueCount, int missingCount);

    [LoggerMessage(
        EventId = 3405,
        Level = LogLevel.Error,
        Message = "A document extraction batch failed ({ExceptionType}): {Reason}")]
    private partial void LogBatchFailed(string exceptionType, string reason);
}
