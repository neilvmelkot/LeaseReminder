using System.Text;
using System.Text.Json;

using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Email;
using LeaseReminder.Infrastructure.Persistence;
using LeaseReminder.Infrastructure.Security;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Connecting, inspecting, and disconnecting the Gmail mailbox.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot do.</b> It can create an account row, store an
/// encrypted token, and destroy one. It cannot read a token back: that is
/// <c>app.read_email_account_token</c>, granted to the worker role alone, and
/// migration 0010 additionally revokes the four ciphertext columns from the
/// API role. So a flaw anywhere in this file cannot leak a Gmail credential,
/// because the credential is not reachable from this process.
/// </para>
/// <para>
/// <b>Where the PKCE verifier lives.</b> In an http-only, Secure, SameSite=Lax
/// cookie scoped to the callback path — not in the <c>state</c> parameter.
/// State travels in the URL, and a URL reaches browser history, proxy logs,
/// and any error report that captured the redirect chain; the verifier is the
/// one value in this flow that must not. The cookie is first-party to the API,
/// which is where Google returns the browser, so it is present on the
/// callback.
/// </para>
/// </remarks>
public static class EmailAccountEndpoints
{
    /// <summary>
    /// The cookie holding an in-flight authorization attempt.
    /// </summary>
    /// <remarks>
    /// The __Host- prefix is a browser-enforced guarantee: Secure, no Domain
    /// attribute, and Path=/. A cookie with this name cannot have been set by
    /// a subdomain, which removes the subdomain-fixation attack on the state
    /// value.
    /// </remarks>
    private const string AttemptCookie = "__Host-gmail_connect";

    /// <summary>
    /// How long an in-flight authorization stays valid, in seconds.
    /// </summary>
    /// <remarks>
    /// Ten minutes, matching the attempt cookie's own lifetime. Long enough
    /// to read a consent screen and pick the right Google account, short
    /// enough that an abandoned attempt is not left usable for an afternoon.
    /// </remarks>
    private const int AttemptTtlSeconds = 600;

    /// <summary>
    /// The SHA-256 of an OAuth state, which is what the database stores.
    /// </summary>
    /// <remarks>
    /// The state is a bearer value for the life of the flow, so the row that
    /// authorises the callback holds only its digest: a leaked or backed-up
    /// row then cannot be replayed into somebody's mailbox connection. The
    /// invitation tokens in migration 0025 are stored the same way for the
    /// same reason.
    /// </remarks>
    private static byte[] StateDigest(string state) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state));

    public static IEndpointRouteBuilder MapEmailAccountEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapStatus(routes);
        MapAuthorize(routes);
        MapCallback(routes);
        MapDisconnect(routes);
        MapTestSend(routes);

        return routes;
    }

    // =========================================================================
    // Status
    // =========================================================================

    private static void MapStatus(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/organizations/{organizationId:guid}/email-account", async (
            Guid organizationId,
            AppDbContext dbContext,
            GoogleOAuthClient oauth,
            TokenProtectorAccessor protector,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            // Selected column by column rather than as an entity, so the
            // ciphertext columns are never named in a query this role issues.
            // It could not read them anyway, but a query that mentions them is
            // a query that will fail confusingly rather than never be written.
            var account = await dbContext.Database
                .SqlQuery<EmailAccountRow>(
                    $"""
                     SELECT
                       a.id                AS "Id",
                       a.email_address::text AS "EmailAddress",
                       a.display_name      AS "DisplayName",
                       a.connection_status::text AS "ConnectionStatus",
                       a.status_detail     AS "StatusDetail",
                       a.granted_scopes    AS "GrantedScopes",
                       a.connected_at      AS "ConnectedAt",
                       a.last_successful_send_at AS "LastSuccessfulSendAt",
                       a.last_error_at     AS "LastErrorAt",
                       a.daily_send_limit  AS "DailySendLimit"
                     FROM email_accounts a
                     WHERE a.organization_id = {organizationId}
                     ORDER BY (a.connection_status = 'connected') DESC, a.updated_at DESC
                     LIMIT 1
                     """)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // Reported so the settings page can say what is missing rather
            // than offering a Connect button that fails on click.
            return Results.Ok(new
            {
                account,
                isOAuthConfigured = oauth.IsConfigured,
                isEncryptionConfigured = protector.IsConfigured,
            });
        })
        .WithName("GetEmailAccount");

        // What has actually been sent. The log is the evidence; the account's
        // status is only a summary of it.
        routes.MapGet("/organizations/{organizationId:guid}/email-logs", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            int limit = 50) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var capped = Math.Clamp(limit, 1, 200);

            var logs = await dbContext.Database
                .SqlQuery<EmailLogRow>(
                    $"""
                     SELECT
                       l.id              AS "Id",
                       l.sender_email::text AS "SenderEmail",
                       l.recipients_json::text AS "RecipientsJson",
                       l.subject         AS "Subject",
                       l.status::text    AS "Status",
                       l.error_category::text AS "ErrorCategory",
                       l.error_message   AS "ErrorMessage",
                       l.provider_message_id AS "ProviderMessageId",
                       l.attempt_number  AS "AttemptNumber",
                       l.created_at      AS "CreatedAt"
                     FROM email_logs l
                     WHERE l.organization_id = {organizationId}
                     ORDER BY l.created_at DESC
                     LIMIT {capped}
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(logs);
        })
        .WithName("ListEmailLogs");
    }

    // =========================================================================
    // Connect
    // =========================================================================

    private static void MapAuthorize(IEndpointRouteBuilder routes)
    {
        // A GET that redirects, because the browser has to navigate to Google
        // at the top level. A fetch would be blocked by Google's framing and
        // CORS rules, and rightly so: consent must happen somewhere the user
        // can see the address bar.
        routes.MapGet("/organizations/{organizationId:guid}/email-account/authorize", async (
            Guid organizationId,
            HttpContext context,
            AppDbContext dbContext,
            GoogleOAuthClient oauth,
            TokenProtectorAccessor protector,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can connect a mailbox.");
            }

            if (!oauth.IsConfigured)
            {
                return Results.Problem(
                    title: "Google sign-in is not configured",
                    detail: "Add the Google OAuth client id, secret, and redirect URI.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!protector.IsConfigured)
            {
                // Refused before sending the user to Google. Letting the flow
                // succeed and then failing to store the token would mean the
                // user granted access for nothing, and would leave a live
                // grant on their Google account with nothing holding it.
                return Results.Problem(
                    title: "Token encryption is not configured",
                    detail: "Set a token encryption key before connecting a mailbox. "
                            + "A refresh token is never stored unencrypted.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            Guid accountId;

            try
            {
                accountId = await dbContext.Database
                    .SqlQuery<Guid>(
                        $"""
                         SELECT app.begin_email_account_connection(
                           {organizationId}, null, null) AS "Value"
                         """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }

            var (url, attempt) = oauth.BeginAuthorization(accountId);

            // Recorded server-side before the browser leaves for Google.
            //
            // This is what the callback authorises on. It cannot authorise on
            // the session, because there is none by then — Google returns the
            // browser by a top-level GET and the endpoint is anonymous — and
            // it must not authorise on the attempt cookie alone, which is
            // unsigned JSON the browser could invent. The administrator
            // check belongs here, where the caller is authenticated and it is
            // actually satisfiable.
            //
            // Only the digest of the state is stored: for the ten minutes it
            // is live the state is a bearer value, and a leaked row should
            // not be replayable into somebody's mailbox.
            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     SELECT app.begin_email_oauth_attempt(
                       {accountId}, {StateDigest(attempt.State)}, {AttemptTtlSeconds})
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            context.Response.Cookies.Append(
                AttemptCookie,
                JsonSerializer.Serialize(attempt),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    // Lax, not Strict: Google returns the browser by a
                    // top-level GET from another site, and Strict would
                    // withhold the cookie on exactly that navigation.
                    SameSite = SameSiteMode.Lax,
                    // Required by the __Host- prefix, and the reason that
                    // prefix is usable at all.
                    Path = "/",
                    MaxAge = TimeSpan.FromMinutes(10),
                    IsEssential = true,
                });

            return Results.Redirect(url.ToString());
        })
        .WithName("AuthorizeEmailAccount");
    }

    private static void MapCallback(IEndpointRouteBuilder routes)
    {
        // Anonymous. Google redirects the browser here, and whether a session
        // cookie survives that navigation is not something to depend on. The
        // authority comes from the attempt cookie, which this server set and
        // which proves the flow was started by an administrator; the database
        // function re-checks administrator rights before storing anything.
        routes.MapGet("/integrations/google/callback", async (
            HttpContext context,
            AppDbContext dbContext,
            GoogleOAuthClient oauth,
            TokenProtectorAccessor protector,
            CancellationToken cancellationToken,
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error) =>
        {
            AuthorizationAttempt? attempt = null;

            if (context.Request.Cookies.TryGetValue(AttemptCookie, out var raw))
            {
                try
                {
                    attempt = JsonSerializer.Deserialize<AuthorizationAttempt>(raw);
                }
                catch (JsonException)
                {
                    attempt = null;
                }
            }

            // Single use, whatever happens next. An attempt that is left
            // usable is an attempt that can be replayed.
            context.Response.Cookies.Delete(AttemptCookie, new CookieOptions
            {
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
            });

            if (!string.IsNullOrEmpty(error))
            {
                return Results.Redirect(
                    oauth.BuildCompletionUrl("declined", DescribeGoogleError(error)).ToString());
            }

            if (attempt is null || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code))
            {
                return Results.Redirect(
                    oauth.BuildCompletionUrl(
                            "failed",
                            "The connection attempt could not be matched. Start again.")
                        .ToString());
            }

            // The CSRF check. A callback whose state does not match the one
            // this server issued is a callback this server did not start.
            if (!CryptographicEquals(attempt.State, state))
            {
                return Results.Redirect(
                    oauth.BuildCompletionUrl(
                            "failed", "The connection attempt did not match. Start again.")
                        .ToString());
            }

            var (tokens, exchangeError) = await oauth
                .ExchangeCodeAsync(code, attempt.CodeVerifier, cancellationToken)
                .ConfigureAwait(false);

            if (tokens is null)
            {
                return Results.Redirect(
                    oauth.BuildCompletionUrl("failed", exchangeError).ToString());
            }

            if (!protector.IsConfigured)
            {
                return Results.Redirect(
                    oauth.BuildCompletionUrl(
                            "failed", "Token encryption is not configured.")
                        .ToString());
            }

            // The address Google reports, not one the user typed. It is what
            // recipients will see, and it is the only address this token can
            // actually send from.
            var address = await oauth
                .ReadMailboxAddressAsync(tokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);

            var protectedToken = protector.Require()
                .Protect(tokens.RefreshToken, attempt.EmailAccountId);

            try
            {
                // One call, authorised by the server-side attempt recorded at
                // authorize time and found by the digest of the state Google
                // handed back. It writes the address and the token together,
                // and consumes the attempt, so a replayed callback finds
                // nothing.
                //
                // This replaces a pair of calls —
                // app.set_email_account_identity and
                // app.store_email_account_token — that both gated on
                // app.can_administer_org. On an anonymous callback that check
                // can never pass, so connecting a mailbox always failed with
                // "sign in as an administrator", for owners included, and the
                // address was silently never written.
                var stored = await dbContext.Database
                    .SqlQuery<bool>(
                        $"""
                         SELECT app.complete_email_oauth_attempt(
                           {StateDigest(state)}, {address},
                           {protectedToken.Ciphertext},
                           {protectedToken.Nonce}, {protectedToken.AuthenticationTag},
                           {protectedToken.KeyVersion},
                           {tokens.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)})
                           AS "Value"
                         """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!stored)
                {
                    // Unknown, already used, or expired state; or the person
                    // who started the flow is no longer an administrator. One
                    // message for all of them, because this endpoint is
                    // anonymous and distinguishing them would let it be
                    // probed.
                    return Results.Redirect(
                        oauth.BuildCompletionUrl(
                                "failed",
                                "That connection attempt is no longer valid. "
                                + "Start again from Settings.")
                            .ToString());
                }
            }
            catch (PostgresException exception)
            {
                // Never the exception's own text: it can echo parameter
                // values, and one of those parameters is ciphertext.
                return Results.Redirect(
                    oauth.BuildCompletionUrl(
                            "failed",
                            exception.SqlState == PostgresErrorCodes.UniqueViolation
                                ? "Another mailbox is already connected. Disconnect it first."
                                : "The mailbox could not be saved.")
                        .ToString());
            }

            return Results.Redirect(oauth.BuildCompletionUrl("connected").ToString());
        })
        .AllowAnonymous()
        .WithName("GoogleOAuthCallback");
    }

    // =========================================================================
    // Disconnect
    // =========================================================================

    private static void MapDisconnect(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/organizations/{organizationId:guid}/email-account/disconnect", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can disconnect a mailbox.");
            }

            var accountId = await dbContext.Database
                .SqlQuery<Guid>(
                    $"""
                     SELECT a.id AS "Value" FROM email_accounts a
                     WHERE a.organization_id = {organizationId}
                       AND a.connection_status = 'connected'
                     LIMIT 1
                     """)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (accountId == Guid.Empty)
            {
                return EndpointSupport.NotFound("connected mailbox");
            }

            // Destroys the ciphertext and cancels pending sends, rather than
            // setting a flag. A disconnect that leaves a usable credential in
            // the database is not a disconnect.
            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     SELECT app.disconnect_email_account(
                       {accountId}, 'not_connected'::app.email_connection_status,
                       'Disconnected by an administrator.')
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        })
        .WithName("DisconnectEmailAccount");
    }

    // =========================================================================
    // Test send
    // =========================================================================

    private static void MapTestSend(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/organizations/{organizationId:guid}/email-account/test", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can send a test message.");
            }

            try
            {
                // Queued, not sent. The API holds no Gmail credential and
                // builds no messages; a test that bypassed the worker would
                // prove nothing about the path a real reminder takes.
                //
                // The recipient is read from the caller's own user record
                // inside the function. An endpoint that accepted an address
                // and mailed it from a connected Gmail account would be an
                // open relay with extra steps.
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.enqueue_test_email({organizationId})",
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.Accepted();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.CheckViolation
                      || exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                // Not an internal error: nothing is connected, or the account
                // has no address. The message says which.
                return Results.Problem(
                    title: "A test message could not be sent",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("SendTestEmail");
    }

    // =========================================================================
    // Shared
    // =========================================================================

    /// <summary>
    /// Compares two state values without leaking their contents through timing.
    /// </summary>
    /// <remarks>
    /// Both are base64url of 32 random bytes, so an ordinary comparison would
    /// be hard to exploit. It costs nothing to be certain.
    /// </remarks>
    private static bool CryptographicEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));

    private static string DescribeGoogleError(string error) => error switch
    {
        "access_denied" => "Access was declined on the Google consent screen.",
        "admin_policy_enforced" =>
            "A Google Workspace policy blocks this application. An administrator "
            + "of that Workspace has to allow it.",
        _ => "Google did not complete the connection.",
    };
}

/// <summary>The connected mailbox, as the settings page sees it.</summary>
/// <remarks>
/// Note what is absent: nothing token-shaped. Those columns are revoked from
/// the API role, so they could not be returned even by mistake.
/// </remarks>
public sealed record EmailAccountRow(
    Guid Id,
    string EmailAddress,
    string? DisplayName,
    string ConnectionStatus,
    string? StatusDetail,
    string[] GrantedScopes,
    DateTime? ConnectedAt,
    DateTime? LastSuccessfulSendAt,
    DateTime? LastErrorAt,
    int DailySendLimit);

/// <summary>One line of the send record.</summary>
public sealed record EmailLogRow(
    Guid Id,
    string SenderEmail,
    string RecipientsJson,
    string Subject,
    string Status,
    string ErrorCategory,
    string? ErrorMessage,
    string? ProviderMessageId,
    int AttemptNumber,
    DateTime CreatedAt);
