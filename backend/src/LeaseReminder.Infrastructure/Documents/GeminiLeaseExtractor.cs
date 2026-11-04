using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using LeaseReminder.Application.Documents;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Infrastructure.Documents;

/// <summary>
/// Extracts lease terms using Google's Generative Language API.
/// </summary>
/// <remarks>
/// <para>
/// Gemini reads PDFs and images natively, which removes the need for a
/// separate OCR stage: a scanned lease or a photograph of a signed page can be
/// sent as-is. Where a text layer does exist, the recovered text is sent
/// instead, because it is far cheaper and because it lets this application see
/// exactly what the model was shown.
/// </para>
/// <para>
/// <b>What this class is not allowed to do.</b> It holds one credential, its
/// own, and has no database connection, no mail sender, and no access to
/// anything else. Section 4.D's rules that the AI must not write to the
/// database, send email, or alter recipient settings are therefore not
/// enforced here; there is simply no mechanism by which it could.
/// </para>
/// <para>
/// <b>Logging.</b> Nothing logged from here contains document content or the
/// API key. Lease terms are the private data this whole system exists to
/// handle, and an operator debugging a failed extraction has no business
/// reading somebody's rent out of a log file. Failures are described by status
/// and shape only.
/// </para>
/// </remarks>
public sealed partial class GeminiLeaseExtractor(
    HttpClient httpClient,
    IOptions<GeminiOptions> options,
    ILogger<GeminiLeaseExtractor> logger) : ILeaseExtractor
{
    private readonly GeminiOptions options = options.Value;

    /// <inheritdoc />
    public string ProviderName => "google.generativelanguage";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(this.options.ApiKey);

    /// <inheritdoc />
    public async Task<ExtractionResponse> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = this.options.Model;

        if (!this.IsConfigured)
        {
            return ExtractionResponse.Failure(
                "Automatic extraction is not configured. Add a Google AI API key to enable it.",
                model,
                isRetryable: false);
        }

        JsonObject body;

        try
        {
            body = BuildRequestBody(request);
        }
        catch (InvalidOperationException exception)
        {
            // Nothing usable to send: no text and a file too large to inline.
            return ExtractionResponse.Failure(exception.Message, model, isRetryable: false);
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            // Relative, so the configured base address governs the host.
            new Uri(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent"),
                UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        // In the header, never the query string: a URL reaches access logs,
        // proxy logs, and exception traces, and a credential belongs in none
        // of them.
        message.Headers.Add("x-goog-api-key", this.options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(this.options.TimeoutSeconds));

        try
        {
            using var response = await httpClient
                .SendAsync(message, timeout.Token)
                .ConfigureAwait(false);

            return await ReadResponseAsync(response, model, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.LogTimedOut(this.options.TimeoutSeconds);

            return ExtractionResponse.Failure(
                "The extraction service did not respond in time.",
                model,
                isRetryable: true);
        }
        catch (HttpRequestException exception)
        {
            // The exception message can name the host but never the key, which
            // travels in a header rather than the URL.
            this.LogRequestFailed(exception.Message);

            return ExtractionResponse.Failure(
                "The extraction service could not be reached.",
                model,
                isRetryable: true);
        }
    }

    /// <summary>
    /// Builds the request envelope.
    /// </summary>
    private JsonObject BuildRequestBody(ExtractionRequest request)
    {
        var parts = new JsonArray();

        var hasText = !string.IsNullOrWhiteSpace(request.RecoveredText);

        // The instruction always comes first, so the document is read as
        // material the instruction has already framed as quoted data rather
        // than as something that arrives unannounced.
        parts.Add(new JsonObject
        {
            ["text"] = ExtractionPrompt.BuildUserPrompt(
                hasText ? ExtractionPrompt.SanitiseDocumentText(request.RecoveredText!) : null,
                request.PageCount,
                DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        });

        // Send the file itself only when there was no usable text. This is the
        // scanned-document and photograph path, and it is what replaces a
        // separate OCR stage.
        if (!hasText)
        {
            if (request.Content is not { } content
                || string.IsNullOrWhiteSpace(request.ContentMediaType))
            {
                throw new InvalidOperationException(
                    "No text could be read from this document and the file itself was not "
                    + "available to send. The lease terms will have to be entered by hand.");
            }

            if (content.Length > this.options.MaximumInlineBytes)
            {
                throw new InvalidOperationException(
                    "No text could be read from this document and the file is too large to "
                    + "send for reading. The lease terms will have to be entered by hand.");
            }

            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = request.ContentMediaType,
                    ["data"] = Convert.ToBase64String(content.Span),
                },
            });
        }

        return new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = ExtractionPrompt.SystemInstruction }),
            },
            ["contents"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = parts,
                }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = this.options.Temperature,
                ["maxOutputTokens"] = this.options.MaximumOutputTokens,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = ExtractionResponseSchema.Build(),
            },
        };
    }

    private async Task<ExtractionResponse> ReadResponseAsync(
        HttpResponseMessage response,
        string model,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            // 429 and 5xx are worth retrying; a 400 means the request itself
            // is wrong and will be wrong again. 403 usually means the key is
            // invalid or the API is not enabled on the project, which a retry
            // will not fix either.
            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            this.LogHttpFailure((int)response.StatusCode, retryable);

            return ExtractionResponse.Failure(
                DescribeHttpFailure(response.StatusCode),
                model,
                retryable);
        }

        JsonNode? payload;

        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return ExtractionResponse.Failure(
                "The extraction service returned a response that could not be read.",
                model,
                isRetryable: true);
        }

        if (payload is not JsonObject envelope)
        {
            return ExtractionResponse.Failure(
                "The extraction service returned an empty response.",
                model,
                isRetryable: true);
        }

        // A prompt can be refused outright, in which case there are no
        // candidates at all. A lease is unlikely to trigger this, but a
        // document uploaded to the wrong place might.
        if (envelope["promptFeedback"]?["blockReason"] is { } blocked)
        {
            this.LogBlocked(blocked.ToString());

            return ExtractionResponse.Failure(
                "The extraction service declined to read this document.",
                model,
                isRetryable: false);
        }

        if (envelope["candidates"] is not JsonArray { Count: > 0 } candidates
            || candidates[0] is not JsonObject candidate)
        {
            return ExtractionResponse.Failure(
                "The extraction service returned no result for this document.",
                model,
                isRetryable: true);
        }

        var finishReason = candidate["finishReason"]?.ToString();

        // MAX_TOKENS means the JSON is truncated and will not parse. Saying so
        // is more useful than letting the parser report an empty result,
        // because the fix is different: the cap needs raising, or the document
        // splitting.
        if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.Ordinal))
        {
            this.LogTruncated(this.options.MaximumOutputTokens);

            return ExtractionResponse.Failure(
                "The document produced more detail than one extraction can hold.",
                model,
                isRetryable: false);
        }

        if (finishReason is not null
            && !string.Equals(finishReason, "STOP", StringComparison.Ordinal))
        {
            this.LogUnexpectedFinish(finishReason);

            return ExtractionResponse.Failure(
                "The extraction service stopped before finishing this document.",
                model,
                isRetryable: true);
        }

        var text = candidate["content"]?["parts"] is JsonArray parts
            ? string.Concat(parts.Select(part => part?["text"]?.GetValue<string>() ?? string.Empty))
            : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return ExtractionResponse.Failure(
                "The extraction service returned nothing for this document.",
                model,
                isRetryable: true);
        }

        // The model actually used, which can differ from the one requested
        // when an alias resolves to a dated version. Recorded so a future
        // disagreement between two runs is diagnosable.
        var resolvedModel = envelope["modelVersion"]?.ToString() ?? model;

        // Resolved into a local rather than computed in the argument list, so
        // the logging call takes two plain values. Token usage is worth having
        // in the log: it is the only visibility into how much of a free-tier
        // quota a document consumed.
        var usage = envelope["usageMetadata"]?["totalTokenCount"];
        var totalTokens = usage is null ? "unknown" : usage.ToString();

        this.LogSucceeded(resolvedModel, totalTokens);

        return ExtractionResponse.Success(text, resolvedModel);
    }

    private static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests =>
            "The extraction service is rate limited. This document will be retried.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "The extraction service rejected the configured credentials.",
        HttpStatusCode.BadRequest =>
            "The extraction service rejected the request for this document.",
        HttpStatusCode.RequestEntityTooLarge =>
            "This document is too large for the extraction service.",
        _ => "The extraction service returned an error.",
    };

    // -----------------------------------------------------------------------
    // Logging. Nothing here carries document content or the API key.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "Lease extraction succeeded using {Model}, {TotalTokens} tokens.")]
    private partial void LogSucceeded(string model, string totalTokens);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Lease extraction failed with HTTP {StatusCode}. Retryable: {IsRetryable}.")]
    private partial void LogHttpFailure(int statusCode, bool isRetryable);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Warning,
        Message = "Lease extraction timed out after {TimeoutSeconds}s.")]
    private partial void LogTimedOut(int timeoutSeconds);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Warning,
        Message = "Lease extraction could not reach the service: {Reason}")]
    private partial void LogRequestFailed(string reason);

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Warning,
        Message = "Lease extraction was blocked before reading the document: {BlockReason}")]
    private partial void LogBlocked(string blockReason);

    [LoggerMessage(
        EventId = 3105,
        Level = LogLevel.Warning,
        Message = "Lease extraction hit the {MaximumOutputTokens}-token output cap and was truncated.")]
    private partial void LogTruncated(int maximumOutputTokens);

    [LoggerMessage(
        EventId = 3106,
        Level = LogLevel.Warning,
        Message = "Lease extraction stopped early: {FinishReason}")]
    private partial void LogUnexpectedFinish(string finishReason);
}
