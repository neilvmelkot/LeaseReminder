using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Infrastructure.Storage;

/// <summary>
/// Stores lease documents in a private Supabase Storage bucket.
/// </summary>
/// <remarks>
/// <para>
/// Uses the Storage REST API directly rather than a client library. The four
/// operations needed here are one HTTP request each, and a direct call makes
/// the credential handling visible at the point it matters rather than buried
/// in a dependency.
/// </para>
/// <para>
/// <b>The service-role key.</b> This class is the only place it is used. It
/// bypasses Row Level Security, so authorization has already happened before
/// anything here is called: the API established that the caller may see the
/// document by reading its row through the RLS-constrained connection, and
/// only then asks for the bytes or a URL. Nothing here re-checks, and nothing
/// here could.
/// </para>
/// </remarks>
public sealed partial class SupabaseDocumentStorage(
    HttpClient httpClient,
    IOptions<SupabaseStorageOptions> options,
    ILogger<SupabaseDocumentStorage> logger) : IDocumentStorage
{
    private readonly SupabaseStorageOptions options = options.Value;

    /// <inheritdoc />
    public bool IsConfigured => this.options.IsConfigured;

    /// <inheritdoc />
    public string BucketName => this.options.Bucket;

    /// <inheritdoc />
    public async Task<StorageResult> UploadAsync(
        string storagePath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentNullException.ThrowIfNull(content);

        if (!this.IsConfigured)
        {
            return StorageResult.Failure("Document storage is not configured.");
        }

        using var request = this.BuildRequest(
            HttpMethod.Post,
            $"storage/v1/object/{Encode(this.options.Bucket)}/{Encode(storagePath)}");

        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = body;

        // Refuse to replace an object that already exists. Paths are random
        // GUIDs so a collision should be impossible, but "should be
        // impossible" and "silently overwrites a lease if it happens" is a bad
        // combination. x-upsert defaults to false; it is stated explicitly
        // because the default mattering is the whole point.
        request.Headers.Add("x-upsert", "false");

        return await this.SendAsync(request, "upload", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> DownloadAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (!this.IsConfigured)
        {
            return null;
        }

        using var request = this.BuildRequest(
            HttpMethod.Get,
            $"storage/v1/object/{Encode(this.options.Bucket)}/{Encode(storagePath)}");

        using var timeout = this.CreateTimeout(cancellationToken);

        try
        {
            using var response = await httpClient
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                this.LogOperationFailed("download", (int)response.StatusCode);

                return null;
            }

            return await response.Content
                .ReadAsByteArrayAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            this.LogTransportFailure("download", exception.GetType().Name);

            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SignedDocumentUrl?> CreateSignedUrlAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (!this.IsConfigured)
        {
            return null;
        }

        var lifetime = this.options.SignedUrlLifetimeSeconds;

        using var request = this.BuildRequest(
            HttpMethod.Post,
            $"storage/v1/object/sign/{Encode(this.options.Bucket)}/{Encode(storagePath)}");

        request.Content = JsonContent.Create(new JsonObject { ["expiresIn"] = lifetime });

        using var timeout = this.CreateTimeout(cancellationToken);

        try
        {
            using var response = await httpClient
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                this.LogOperationFailed("sign", (int)response.StatusCode);

                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<JsonNode>(timeout.Token)
                .ConfigureAwait(false);

            // Returned relative, e.g. "/object/sign/bucket/path?token=...".
            var relative = payload?["signedURL"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            var absolute = new Uri(
                this.options.ProjectUrl!,
                "/storage/v1" + (relative.StartsWith('/') ? relative : "/" + relative));

            return new SignedDocumentUrl(
                absolute,
                DateTimeOffset.UtcNow.AddSeconds(lifetime));
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            this.LogTransportFailure("sign", exception.GetType().Name);

            return null;
        }
    }

    /// <inheritdoc />
    public async Task<StorageResult> DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (!this.IsConfigured)
        {
            return StorageResult.Failure("Document storage is not configured.");
        }

        using var request = this.BuildRequest(
            HttpMethod.Delete,
            $"storage/v1/object/{Encode(this.options.Bucket)}/{Encode(storagePath)}");

        return await this.SendAsync(request, "delete", cancellationToken).ConfigureAwait(false);
    }

    private async Task<StorageResult> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeout = this.CreateTimeout(cancellationToken);

        try
        {
            using var response = await httpClient
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Success();
            }

            this.LogOperationFailed(operation, (int)response.StatusCode);

            return StorageResult.Failure(response.StatusCode switch
            {
                HttpStatusCode.Conflict =>
                    "A document already exists at that location.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Document storage rejected the configured credentials.",
                HttpStatusCode.NotFound =>
                    "The storage bucket does not exist.",
                HttpStatusCode.RequestEntityTooLarge =>
                    "The file is larger than storage will accept.",
                _ => "Document storage returned an error.",
            });
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            this.LogTransportFailure(operation, exception.GetType().Name);

            return StorageResult.Failure("Document storage could not be reached.");
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(this.options.ProjectUrl!, relativePath));

        // Both headers are required by the Storage API. The key never appears
        // in the URL, so it cannot reach an access log or an exception trace.
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", this.options.ServiceRoleKey);
        request.Headers.Add("apikey", this.options.ServiceRoleKey);

        return request;
    }

    /// <summary>
    /// Percent-encodes a path segment by segment.
    /// </summary>
    /// <remarks>
    /// The separators between segments must survive, so the whole path cannot
    /// simply be escaped. Paths reaching here are machine-generated and
    /// already match a strict pattern, so there is nothing hostile to encode;
    /// this is here so that remains true if the pattern is ever widened.
    /// </remarks>
    private static string Encode(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(this.options.TimeoutSeconds));

        return source;
    }

    /// <summary>
    /// Whether a failure is the network rather than the caller giving up.
    /// </summary>
    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            // A caller-initiated cancellation is not a storage failure and
            // must propagate; a timeout here is.
            return !cancellationToken.IsCancellationRequested;
        }

        return exception is HttpRequestException or System.Text.Json.JsonException;
    }

    // -----------------------------------------------------------------------
    // Logging. Never the path, which identifies a specific lease document, and
    // never the key.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Warning,
        Message = "Document storage {Operation} failed with HTTP {StatusCode}.")]
    private partial void LogOperationFailed(string operation, int statusCode);

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Document storage {Operation} could not complete ({ExceptionType}).")]
    private partial void LogTransportFailure(string operation, string exceptionType);
}

/// <summary>
/// The implementation used when no storage is configured.
/// </summary>
/// <remarks>
/// Mirrors the unconfigured email sender. A deployment without storage is a
/// valid state for everything except document upload, and the rest of the
/// application should work rather than fail at startup. Every method refuses
/// clearly, so the API can tell a user that uploads are unavailable instead of
/// accepting a file and losing it.
/// </remarks>
public sealed class UnconfiguredDocumentStorage : IDocumentStorage
{
    private const string Reason =
        "Document storage is not configured. Set the Supabase project URL, service-role "
        + "key, and bucket to enable uploads.";

    public bool IsConfigured => false;

    public string BucketName => "unconfigured";

    public Task<StorageResult> UploadAsync(
        string storagePath, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(StorageResult.Failure(Reason));

    public Task<ReadOnlyMemory<byte>?> DownloadAsync(
        string storagePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<ReadOnlyMemory<byte>?>(null);

    public Task<SignedDocumentUrl?> CreateSignedUrlAsync(
        string storagePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<SignedDocumentUrl?>(null);

    public Task<StorageResult> DeleteAsync(
        string storagePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(StorageResult.Failure(Reason));
}
