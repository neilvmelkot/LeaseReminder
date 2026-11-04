using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using LeaseReminder.Application.Email;
using LeaseReminder.Infrastructure.Email;
using LeaseReminder.Infrastructure.Security;

using Microsoft.Extensions.Logging;

namespace LeaseReminder.Worker;

/// <summary>
/// Sends through the Gmail API, from the organization's one connected mailbox.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no fallback sender.</b> Section 13.B: exactly one connected
/// account sends everything. If no account is connected, or the token no
/// longer works, this records a failure and the notification stays unsent. It
/// never falls back to another mailbox, and it never marks something sent that
/// was not, because a reminder recorded as delivered that nobody received is
/// the one outcome this system must not produce.
/// </para>
/// <para>
/// <b>Access tokens are cached in memory, refresh tokens are not held at
/// all.</b> The refresh token is read from the database, decrypted, used, and
/// dropped. The short-lived access token it yields is kept for its hour so a
/// batch of twenty reminders is one token exchange rather than twenty. Nothing
/// is written to disk and nothing survives a restart.
/// </para>
/// </remarks>
public sealed partial class GmailEmailSender(
    WorkerDatabase database,
    GoogleOAuthClient oauth,
    TokenProtectorAccessor protectorAccessor,
    HttpClient httpClient,
    ILogger<GmailEmailSender> logger) : IEmailSender
{
    private const string SendEndpoint =
        "https://gmail.googleapis.com/gmail/v1/users/me/messages/send";

    /// <summary>
    /// Cached access tokens, keyed by email account.
    /// </summary>
    /// <remarks>
    /// Concurrent because the dispatcher sends several messages at once. A
    /// race here costs one redundant token exchange, which is cheap; locking
    /// to avoid it would serialise the whole batch, which is not.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, CachedAccessToken> tokenCache = new();

    /// <summary>
    /// Whether sending is possible at all.
    /// </summary>
    /// <remarks>
    /// Configuration only: whether this deployment has a Google client and an
    /// encryption key. Whether any mailbox is actually connected is a
    /// per-organization question answered at send time.
    /// </remarks>
    public bool IsConfigured => oauth.IsConfigured && protectorAccessor.IsConfigured;

    public async Task<EmailSendOutcome> SendAsync(
        OutgoingEmail message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!this.IsConfigured)
        {
            return EmailSendOutcome.Permanent(
                "configuration",
                "Gmail sending is not configured. Add the Google OAuth client and the "
                + "token encryption key.");
        }

        var account = await database
            .ReadConnectedEmailAccountAsync(message.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            // The disconnect path ends here, and this is launch test 10: a
            // disconnected account cannot continue sending, because there is
            // no account to send from and no fallback.
            return EmailSendOutcome.Permanent(
                "configuration",
                "No Gmail account is connected, so there is nothing to send from.");
        }

        // The application's own daily cap, checked before Google's. Stopping
        // at the limit is a different thing from having Google start refusing:
        // the first is a decision, the second is an incident.
        var sentToday = await database
            .CountSendsTodayAsync(account.EmailAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (sentToday >= account.DailySendLimit)
        {
            return EmailSendOutcome.Transient(
                "quota",
                $"The daily sending limit of {account.DailySendLimit} has been reached. "
                + "Remaining reminders will go out tomorrow.");
        }

        var (accessToken, tokenError, tokenPermanent) = await this
            .GetAccessTokenAsync(account.EmailAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (accessToken is null)
        {
            return tokenPermanent
                ? EmailSendOutcome.Permanent("authorization", tokenError!)
                : EmailSendOutcome.Transient("transient", tokenError!);
        }

        // Built by MimeMessageBuilder in the application layer rather than
        // here, because header injection is the sharp edge of this format and
        // it needs tests that try to get past it. The display name is the
        // account's, not the caller's: Gmail will only accept the connected
        // mailbox as the sender anyway.
        var raw = MimeMessageBuilder.Build(
            account.EmailAddress,
            account.DisplayName,
            message.To,
            message.Cc,
            message.Subject,
            message.TextBody,
            message.HtmlBody,
            message.IdempotencyKey,
            $"----=_Part_{Guid.NewGuid():N}");

        using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["raw"] = raw }),
        };

        request.Headers.Authorization = new("Bearer", accessToken);

        try
        {
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var outcome = await this
                .InterpretAsync(response, account.EmailAccountId, cancellationToken)
                .ConfigureAwait(false);

            // The send record. Written whatever happened, because the log is
            // the evidence of what the provider actually accepted, and a log
            // that only records successes answers none of the questions it
            // would be consulted for.
            await database
                .RecordEmailLogAsync(
                    message.OrganizationId,
                    account.EmailAccountId,
                    account.EmailAddress,
                    message.To,
                    message.Subject,
                    outcome.Accepted ? "accepted" : "failed",
                    outcome.ErrorCategory,
                    outcome.ErrorMessage,
                    outcome.ProviderMessageId,
                    cancellationToken)
                .ConfigureAwait(false);

            return outcome;
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            this.LogTransportFailure(exception.GetType().Name);

            return EmailSendOutcome.Transient(
                "transient", "Gmail could not be reached.");
        }
    }

    /// <summary>
    /// Returns a usable access token, refreshing it when necessary.
    /// </summary>
    private async Task<(string? Token, string? Error, bool IsPermanent)> GetAccessTokenAsync(
        Guid emailAccountId,
        CancellationToken cancellationToken)
    {
        // A minute of margin, so a token does not expire between the check
        // and the send.
        if (this.tokenCache.TryGetValue(emailAccountId, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return (cached.Token, null, false);
        }

        var stored = await database
            .ReadEmailAccountTokenAsync(emailAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            this.tokenCache.TryRemove(emailAccountId, out _);

            return (
                null,
                "The mailbox is no longer connected.",
                true);
        }

        string refreshToken;

        try
        {
            // The additional authenticated data binds the ciphertext to this
            // account id and key version, so a token copied from one row into
            // another fails to decrypt rather than silently sending from the
            // wrong mailbox.
            refreshToken = protectorAccessor.Require().Unprotect(
                new ProtectedToken
                {
                    Ciphertext = stored.Ciphertext,
                    Nonce = stored.Nonce,
                    AuthenticationTag = stored.AuthenticationTag,
                    KeyVersion = stored.KeyVersion,
                },
                emailAccountId);
        }
        catch (Exception exception)
            when (exception is System.Security.Cryptography.CryptographicException
                      or InvalidOperationException
                      or KeyNotFoundException)
        {
            // Wrong key, rotated key not present, or tampered ciphertext. The
            // exception type only; its message can carry key material.
            this.LogDecryptFailed(exception.GetType().Name);

            return (
                null,
                "The stored Gmail credential could not be decrypted. Check that the "
                + "token encryption key matches the one used when it was stored.",
                true);
        }

        var (token, expiresAt, permanent, error) = await oauth
            .RefreshAccessTokenAsync(refreshToken, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return (null, error ?? "The Gmail credential could not be refreshed.", permanent);
        }

        this.tokenCache[emailAccountId] = new CachedAccessToken(token, expiresAt);

        return (token, null, false);
    }

    /// <summary>
    /// Turns a Gmail response into an outcome.
    /// </summary>
    /// <remarks>
    /// The distinction that matters is permanent versus transient, because it
    /// decides whether the queue retries. A 429 is a rate limit and will
    /// succeed later; a 403 usually means the scope was revoked and never
    /// will.
    /// </remarks>
    private async Task<EmailSendOutcome> InterpretAsync(
        HttpResponseMessage response,
        Guid emailAccountId,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            JsonObject? payload = null;

            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<JsonObject>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                // Accepted is accepted. A response body that will not parse
                // costs the message id, not the send.
            }

            return EmailSendOutcome.Success(payload?["id"]?.ToString());
        }

        var status = (int)response.StatusCode;

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                // The cached token is stale. Dropping it means the next
                // attempt refreshes rather than repeating the failure.
                this.tokenCache.TryRemove(emailAccountId, out _);

                this.LogSendFailed(status, "authorization");

                return EmailSendOutcome.Transient(
                    "authorization", "Gmail rejected the credential; it will be refreshed.");

            case HttpStatusCode.Forbidden:
                this.LogSendFailed(status, "authorization");

                return EmailSendOutcome.Permanent(
                    "authorization",
                    "Gmail refused the send. The sending permission may have been "
                    + "revoked. Reconnect the mailbox.");

            case HttpStatusCode.TooManyRequests:
                this.LogSendFailed(status, "quota");

                return EmailSendOutcome.Transient(
                    "quota", "Gmail is rate limiting. This will be retried.");

            case HttpStatusCode.BadRequest:
                this.LogSendFailed(status, "invalid_recipient");

                // Almost always a malformed recipient. Retrying an unchanged
                // malformed message just fails again.
                return EmailSendOutcome.Permanent(
                    "invalid_recipient",
                    "Gmail rejected the message. Check the recipient address.");

            default:
                this.LogSendFailed(status, "transient");

                return status >= 500
                    ? EmailSendOutcome.Transient(
                        "transient", "Gmail returned a server error. This will be retried.")
                    : EmailSendOutcome.Permanent(
                        "permanent", "Gmail refused the message.");
        }
    }

    private static bool IsTransport(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        return exception is HttpRequestException or System.Text.Json.JsonException;
    }

    private sealed record CachedAccessToken(string Token, DateTimeOffset ExpiresAt);

    // -----------------------------------------------------------------------
    // Logging. Never a token, a recipient, a subject, or a body.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3700,
        Level = LogLevel.Warning,
        Message = "Gmail send failed with HTTP {StatusCode}, categorised as {Category}.")]
    private partial void LogSendFailed(int statusCode, string category);

    [LoggerMessage(
        EventId = 3701,
        Level = LogLevel.Error,
        Message = "A stored Gmail credential could not be decrypted ({ExceptionType}). "
                  + "The encryption key may not match the one used to store it.")]
    private partial void LogDecryptFailed(string exceptionType);

    [LoggerMessage(
        EventId = 3702,
        Level = LogLevel.Warning,
        Message = "Gmail could not be reached ({ExceptionType}).")]
    private partial void LogTransportFailure(string exceptionType);
}
