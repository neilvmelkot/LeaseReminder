using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Infrastructure.Email;

/// <summary>What an authorization attempt needs to remember.</summary>
/// <param name="State">
/// The opaque value Google echoes back. Compared on return to prove the
/// callback belongs to a flow this server started.
/// </param>
/// <param name="CodeVerifier">The PKCE secret, never sent to Google until the exchange.</param>
/// <param name="EmailAccountId">The row the resulting token will be stored against.</param>
public sealed record AuthorizationAttempt(
    string State,
    string CodeVerifier,
    Guid EmailAccountId);

/// <summary>The result of exchanging an authorization code.</summary>
public sealed record GoogleTokens(
    string RefreshToken,
    string AccessToken,
    string GrantedScopes,
    DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// Talks to Google's OAuth endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>PKCE, even though this is a confidential client.</b> A web server with a
/// client secret does not strictly need Proof Key for Code Exchange, and its
/// absence would not be a finding in most reviews. It is used anyway because
/// the authorization code travels back through a browser redirect, which means
/// through the user's history, any proxy in between, and whatever logs the
/// redirect chain touched. PKCE makes a code captured from any of those
/// useless without the verifier, which never leaves this process.
/// </para>
/// <para>
/// <b>Nothing here is logged.</b> Not the code, not the verifier, not either
/// token, not the client secret. Section 4.B forbids it, and an OAuth flow is
/// precisely where a well-meaning debug line ends up printing a credential.
/// </para>
/// </remarks>
public sealed partial class GoogleOAuthClient(
    HttpClient httpClient,
    IOptions<GoogleOAuthOptions> options,
    ILogger<GoogleOAuthClient> logger)
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>Returns the address of the account that just consented.</summary>
    /// <remarks>
    /// <para>
    /// The OpenID userinfo endpoint, not Gmail's <c>users/me/profile</c>. That
    /// one was used here on the assumption that the send scope was enough to
    /// read it, and it is not: <c>users.getProfile</c> requires one of Gmail's
    /// READ scopes, so with <c>gmail.send</c> alone it answers 403. This
    /// method then returned null, the address was never written, and the
    /// mailbox showed as connected under the placeholder address the row was
    /// created with.
    /// </para>
    /// <para>
    /// Userinfo needs the <c>email</c> scope, which reveals which account
    /// consented and nothing else. It grants no access to mail, so the
    /// promise the settings page makes — send only, this cannot read your
    /// mail — is unchanged.
    /// </para>
    /// </remarks>
    private const string ProfileEndpoint =
        "https://openidconnect.googleapis.com/v1/userinfo";

    private readonly GoogleOAuthOptions options = options.Value;

    public bool IsConfigured => this.options.IsConfigured;

    /// <summary>
    /// Starts an authorization attempt.
    /// </summary>
    /// <returns>The URL to send the browser to, and what to remember.</returns>
    public (Uri AuthorizationUrl, AuthorizationAttempt Attempt) BeginAuthorization(
        Guid emailAccountId)
    {
        // 32 bytes each. The state proves the callback belongs to a flow this
        // server started; the verifier proves the exchange is being done by
        // whoever started it.
        var state = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var codeVerifier = ToBase64Url(RandomNumberGenerator.GetBytes(32));

        var challenge = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = this.options.ClientId,
            ["redirect_uri"] = this.options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = this.options.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",

            // offline, or no refresh token is issued at all and the
            // connection stops working within the hour.
            ["access_type"] = "offline",

            // Google issues a refresh token only on the FIRST consent for a
            // client and account. Without this, reconnecting an account that
            // was connected before returns an access token and no refresh
            // token, and the connection silently expires an hour later.
            ["prompt"] = "consent",

            // Narrows the account chooser and stops a user who is signed into
            // several Google accounts connecting the wrong one by reflex.
            ["include_granted_scopes"] = "false",
        };

        var url = new UriBuilder(AuthorizationEndpoint)
        {
            Query = string.Join(
                '&',
                query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        }.Uri;

        return (url, new AuthorizationAttempt(state, codeVerifier, emailAccountId));
    }

    /// <summary>
    /// Exchanges an authorization code for tokens.
    /// </summary>
    /// <returns>The tokens, or null with a reason when the exchange failed.</returns>
    public async Task<(GoogleTokens? Tokens, string? Error)> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = this.options.ClientId,
                ["client_secret"] = this.options.ClientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = this.options.RedirectUri,
            }),
        };

        try
        {
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || payload is null)
            {
                // Google's error field is a short machine code such as
                // invalid_grant. Safe to surface and genuinely diagnostic;
                // the description beside it can echo request contents, so it
                // is not.
                var error = payload?["error"]?.ToString() ?? "unknown_error";

                this.LogExchangeFailed((int)response.StatusCode, error);

                return (null, DescribeTokenError(error));
            }

            var refreshToken = payload["refresh_token"]?.ToString();

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                // Happens when the account has consented before and prompt
                // was not forced. Without a refresh token the connection
                // would work for an hour and then stop, which is far worse
                // than failing now.
                return (
                    null,
                    "Google did not return a long-lived token. Remove this application "
                    + "from your Google account's third-party access list and connect "
                    + "again.");
            }

            var accessToken = payload["access_token"]?.ToString() ?? string.Empty;
            var scopes = payload["scope"]?.ToString() ?? this.options.Scope;

            var expiresIn = payload["expires_in"] is { } node
                && int.TryParse(
                    node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var seconds)
                ? seconds
                : 3600;

            // The granted scope is checked rather than assumed. A user can
            // untick a permission on the consent screen, and discovering that
            // at the first send is discovering it at the worst moment.
            if (!scopes.Contains("gmail.send", StringComparison.Ordinal))
            {
                return (
                    null,
                    "Permission to send mail was not granted. Connect again and leave "
                    + "the sending permission ticked.");
            }

            return (
                new GoogleTokens(
                    refreshToken,
                    accessToken,
                    scopes,
                    DateTimeOffset.UtcNow.AddSeconds(expiresIn)),
                null);
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            this.LogTransportFailure("token exchange", exception.GetType().Name);

            return (null, "Google could not be reached. Try connecting again.");
        }
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh access token.
    /// </summary>
    /// <remarks>
    /// Used by the worker before sending. Returns a permanence flag because
    /// the two failures need different handling: <c>invalid_grant</c> means
    /// the user revoked access and the mailbox must be marked as needing
    /// reconnection, while a 5xx means try again shortly.
    /// </remarks>
    public async Task<(string? AccessToken, DateTimeOffset ExpiresAt, bool IsPermanent, string? Error)>
        RefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = this.options.ClientId,
                ["client_secret"] = this.options.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };

        try
        {
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || payload is null)
            {
                var error = payload?["error"]?.ToString() ?? "unknown_error";

                // invalid_grant means revoked, expired, or deleted. No amount
                // of retrying fixes it; a human has to reconnect.
                var permanent = string.Equals(error, "invalid_grant", StringComparison.Ordinal)
                    || string.Equals(error, "invalid_client", StringComparison.Ordinal);

                this.LogRefreshFailed((int)response.StatusCode, error, permanent);

                return (null, default, permanent, DescribeTokenError(error));
            }

            var accessToken = payload["access_token"]?.ToString();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return (null, default, false, "Google returned no access token.");
            }

            var expiresIn = payload["expires_in"] is { } node
                && int.TryParse(
                    node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var seconds)
                ? seconds
                : 3600;

            return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn), false, null);
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            this.LogTransportFailure("token refresh", exception.GetType().Name);

            return (null, default, false, "Google could not be reached.");
        }
    }

    /// <summary>
    /// Reads the connected mailbox's own address.
    /// </summary>
    /// <remarks>
    /// Via the Gmail profile endpoint rather than the userinfo one, because
    /// <c>gmail.send</c> is the only scope granted and userinfo would need
    /// another. The address matters: it is what recipients will see, and
    /// asking the user to type it would invite a mismatch with the mailbox
    /// actually doing the sending.
    /// </remarks>
    public async Task<string?> ReadMailboxAddressAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
        request.Headers.Authorization = new("Bearer", accessToken);

        try
        {
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            // "email" is userinfo's field. The old Gmail profile endpoint
            // called it "emailAddress", which is still read as a fallback so
            // a deployment pointed at the old URL keeps working.
            return payload?["email"]?.ToString()
                   ?? payload?["emailAddress"]?.ToString();
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            this.LogTransportFailure("profile read", exception.GetType().Name);

            return null;
        }
    }

    /// <summary>Where to send the browser when the flow ends.</summary>
    public Uri BuildCompletionUrl(string result, string? detail = null)
    {
        var builder = new UriBuilder(this.options.CompletionUrl);

        var parts = new List<string>
        {
            $"connection={Uri.EscapeDataString(result)}",
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            // Truncated, because this lands in a URL and therefore in the
            // browser's history.
            parts.Add($"reason={Uri.EscapeDataString(detail[..Math.Min(detail.Length, 300)])}");
        }

        builder.Query = string.Join('&', parts);

        return builder.Uri;
    }

    private static async Task<JsonObject?> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonObject>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns Google's error code into something a user can act on.
    /// </summary>
    private static string DescribeTokenError(string error) => error switch
    {
        "invalid_grant" =>
            "Google refused the request. The permission may have been revoked, or the "
            + "sign-in may have taken too long. Connect again.",
        "invalid_client" =>
            "The configured Google client id or secret is wrong.",
        "redirect_uri_mismatch" =>
            "The redirect address does not match the one registered on the Google "
            + "OAuth client.",
        "access_denied" =>
            "Access was declined on the Google consent screen.",
        _ => "Google refused the request.",
    };

    private static bool IsTransport(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        return exception is HttpRequestException or System.Text.Json.JsonException;
    }

    /// <summary>Base64url, as RFC 7636 requires: no padding, URL-safe alphabet.</summary>
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // -----------------------------------------------------------------------
    // Logging. Never a token, a code, a verifier, or the client secret.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3600,
        Level = LogLevel.Warning,
        Message = "Google token exchange failed with HTTP {StatusCode}: {ErrorCode}")]
    private partial void LogExchangeFailed(int statusCode, string errorCode);

    [LoggerMessage(
        EventId = 3601,
        Level = LogLevel.Warning,
        Message = "Google token refresh failed with HTTP {StatusCode}: {ErrorCode}. "
                  + "Permanent: {IsPermanent}.")]
    private partial void LogRefreshFailed(int statusCode, string errorCode, bool isPermanent);

    [LoggerMessage(
        EventId = 3602,
        Level = LogLevel.Warning,
        Message = "Google {Operation} could not complete ({ExceptionType}).")]
    private partial void LogTransportFailure(string operation, string exceptionType);
}
