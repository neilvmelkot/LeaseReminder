namespace LeaseReminder.Application.Documents;

/// <summary>
/// What the extraction pipeline hands to a model.
/// </summary>
/// <param name="Content">
/// The document's bytes, when it is to be sent whole. Used for anything the
/// text extractors could not read: a scanned PDF, a photograph of a signed
/// page, or a PDF whose text layer is missing.
/// </param>
/// <param name="ContentMediaType">The media type of <paramref name="Content"/>.</param>
/// <param name="RecoveredText">
/// Text already pulled out of the file locally, when there was any worth
/// having. Preferred over sending the file when it exists: it is far cheaper
/// per call, and it lets this application see exactly what the model was
/// shown.
/// </param>
/// <param name="PageCount">Pages, when known. Used to reject impossible citations.</param>
public sealed record ExtractionRequest(
    ReadOnlyMemory<byte>? Content,
    string? ContentMediaType,
    string? RecoveredText,
    int? PageCount);

/// <summary>
/// What came back, before parsing.
/// </summary>
/// <param name="IsSuccess">Whether a response was obtained at all.</param>
/// <param name="RawJson">The model's structured output, verbatim.</param>
/// <param name="Model">Which model answered. Recorded for reproducibility.</param>
/// <param name="Error">
/// An operator-safe description of what went wrong. Never contains document
/// content, and never contains the API key.
/// </param>
/// <param name="IsRetryable">
/// Whether trying again later could plausibly succeed. A rate limit is; a
/// malformed request is not.
/// </param>
public sealed record ExtractionResponse(
    bool IsSuccess,
    string? RawJson,
    string? Model,
    string? Error,
    bool IsRetryable)
{
    public static ExtractionResponse Success(string rawJson, string model) =>
        new(true, rawJson, model, null, false);

    public static ExtractionResponse Failure(string error, string model, bool isRetryable) =>
        new(false, null, model, error, isRetryable);
}

/// <summary>
/// Sends a lease document to a model and returns its structured answer.
/// </summary>
/// <remarks>
/// <para>
/// An interface for two reasons. The pipeline can be tested without a network
/// call or an API key, which matters because the interesting cases are the
/// failures. And the provider is replaceable: free-tier model availability
/// changes, and the extraction pipeline should not have to.
/// </para>
/// <para>
/// Implementations are given no capability beyond returning text. They do not
/// receive a database connection, a mail sender, or any credential other than
/// their own, which is what makes section 4.D's "do not give the AI direct
/// database write access" a structural fact here rather than a policy.
/// </para>
/// </remarks>
public interface ILeaseExtractor
{
    /// <summary>The provider's name, recorded on each extraction run.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether this extractor is configured well enough to be called.
    /// </summary>
    /// <remarks>
    /// Checked before a document is queued, so an unconfigured deployment
    /// reports "extraction is not set up" on upload rather than accepting the
    /// file and failing invisibly in a worker minutes later.
    /// </remarks>
    bool IsConfigured { get; }

    Task<ExtractionResponse> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);
}
