namespace LeaseReminder.Infrastructure.Storage;

/// <summary>The outcome of a storage operation.</summary>
/// <param name="IsSuccess">Whether it worked.</param>
/// <param name="Error">An operator-safe reason. Never contains a credential.</param>
public sealed record StorageResult(bool IsSuccess, string? Error)
{
    public static StorageResult Success() => new(true, null);

    public static StorageResult Failure(string error) => new(false, error);
}

/// <summary>A time-limited URL granting access to one object.</summary>
/// <param name="Url">The URL. Treat as a bearer credential.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record SignedDocumentUrl(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Stores and retrieves lease documents.
/// </summary>
/// <remarks>
/// <para>
/// Section 4 requires that uploaded documents never live on the Render
/// container filesystem: containers are replaced without warning, and a
/// document that exists only on one is a document that will be lost. Every
/// implementation therefore writes to object storage.
/// </para>
/// <para>
/// Reading is deliberately split in two. The worker needs the bytes, so it
/// downloads them. A browser must never be handed the bytes through this
/// application, because that would mean proxying a 50 MB file through a
/// request thread; it gets a short-lived signed URL and fetches the object
/// directly.
/// </para>
/// </remarks>
public interface IDocumentStorage
{
    /// <summary>Whether storage is configured well enough to accept uploads.</summary>
    bool IsConfigured { get; }

    /// <summary>The bucket documents are written to.</summary>
    string BucketName { get; }

    /// <summary>
    /// Writes an object.
    /// </summary>
    /// <param name="storagePath">
    /// A server-generated key from <c>DocumentStoragePath.Build</c>. Never
    /// derived from a filename the user supplied.
    /// </param>
    Task<StorageResult> UploadAsync(
        string storagePath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an object into memory, for the extraction worker.
    /// </summary>
    /// <returns>The bytes, or null if the object could not be read.</returns>
    Task<ReadOnlyMemory<byte>?> DownloadAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a short-lived URL for one object.
    /// </summary>
    /// <remarks>
    /// The caller must already have established that the requester is entitled
    /// to this document. This method does not and cannot check: it holds the
    /// service-role key, which bypasses Row Level Security by design.
    /// </remarks>
    Task<SignedDocumentUrl?> CreateSignedUrlAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an object permanently.
    /// </summary>
    /// <remarks>
    /// Used by the retention and erasure procedures, which is why the database
    /// keeps a <c>purged_at</c> column separate from <c>deleted_at</c>: a row
    /// soft-deleted in the database is not the same as an object actually gone
    /// from the bucket, and section 11.7 needs to be able to show the second.
    /// </remarks>
    Task<StorageResult> DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default);
}
