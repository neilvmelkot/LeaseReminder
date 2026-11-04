using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

namespace LeaseReminder.Worker;

/// <summary>
/// A document waiting to be read.
/// </summary>
public sealed record ClaimedDocument
{
    public required Guid DocumentId { get; init; }

    public required Guid OrganizationId { get; init; }

    public required string StorageBucket { get; init; }

    public required string StoragePath { get; init; }

    public string? DetectedMimeType { get; init; }

    public required string OriginalFilename { get; init; }

    public long FileSizeBytes { get; init; }
}

/// <summary>
/// The document half of the worker's database surface.
/// </summary>
/// <remarks>
/// Same discipline as the rest: every method calls a SECURITY DEFINER function
/// from db/migrations/0019. The worker role holds no table privileges, which a
/// verification block in 0010 asserts and which the migration in 0019 was
/// caught violating when it first granted SELECT here.
/// </remarks>
public sealed partial class WorkerDatabase
{
    /// <summary>
    /// Claims documents to extract.
    /// </summary>
    /// <remarks>
    /// Uses FOR UPDATE SKIP LOCKED inside the function, so two workers never
    /// take the same document. That matters more here than on most queues:
    /// extracting twice means paying twice and producing two extractions for
    /// one lease, which a reviewer then has to reconcile.
    /// </remarks>
    public async Task<IReadOnlyList<ClaimedDocument>> ClaimDocumentsForExtractionAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT document_id, organization_id, storage_bucket, storage_path,
                   detected_mime_type, original_filename, file_size_bytes
            FROM app.claim_documents_for_extraction($1)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

        var documents = new List<ClaimedDocument>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ClaimedDocument
            {
                DocumentId = reader.GetGuid(0),
                OrganizationId = reader.GetGuid(1),
                StorageBucket = reader.GetString(2),
                StoragePath = reader.GetString(3),
                DetectedMimeType = reader.IsDBNull(4) ? null : reader.GetString(4),
                OriginalFilename = reader.GetString(5),
                FileSizeBytes = reader.GetInt64(6),
            });
        }

        return documents;
    }

    /// <summary>
    /// Writes an extraction run, its fields, and its issues in one transaction.
    /// </summary>
    /// <remarks>
    /// One call rather than forty-odd, because the database function commits
    /// them together. A half-written extraction would show a reviewer a
    /// partial lease and invite them to approve it.
    /// </remarks>
    public async Task<Guid> RecordExtractionResultAsync(
        Guid documentId,
        string extractionVersion,
        string provider,
        string? model,
        string? promptVersion,
        string? rawJson,
        string fieldsJson,
        string issuesJson,
        int? pageCount,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT app.record_extraction_result($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = documentId });
        command.Parameters.Add(new NpgsqlParameter { Value = extractionVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = provider });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)model ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)promptVersion ?? DBNull.Value,
        });

        // jsonb, explicitly. Without the type the driver sends text and the
        // function's jsonb parameters reject it.
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = (object?)rawJson ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = fieldsJson,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = issuesJson,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)pageCount ?? DBNull.Value,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is Guid id ? id : Guid.Empty;
    }

    /// <summary>
    /// Records that a document could not be read.
    /// </summary>
    /// <param name="isRetryable">
    /// True returns the document to the queue; false stops and waits for a
    /// person. The distinction matters: without it a rate limit would
    /// permanently fail every document it touched.
    /// </param>
    public async Task FailExtractionAsync(
        Guid documentId,
        string reason,
        bool isRetryable,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.fail_extraction($1, $2, $3)");

        command.Parameters.Add(new NpgsqlParameter { Value = documentId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        command.Parameters.Add(new NpgsqlParameter { Value = isRetryable });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
