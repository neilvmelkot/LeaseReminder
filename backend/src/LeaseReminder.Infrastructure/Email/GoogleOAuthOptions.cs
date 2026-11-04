using System.ComponentModel.DataAnnotations;

namespace LeaseReminder.Infrastructure.Email;

/// <summary>
/// Settings for the Google OAuth client used to connect a Gmail mailbox.
/// </summary>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Google";

    /// <summary>The OAuth client id. Not secret.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth client secret.
    /// </summary>
    /// <remarks>
    /// Section 4.J asks for separate clients in development and production.
    /// It takes two minutes and means a leaked development secret cannot send
    /// mail from the real account.
    /// </remarks>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Where Google sends the browser back to.
    /// </summary>
    /// <remarks>
    /// Must match one of the client's registered redirect URIs exactly,
    /// including scheme, port, and trailing path. Google compares it as a
    /// string, so a mismatch produces redirect_uri_mismatch rather than
    /// anything more helpful.
    /// </remarks>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Where to send the browser once the connection succeeds or fails.
    /// </summary>
    /// <remarks>
    /// The frontend, not the API: the user started this on a settings page
    /// and should end up back on it. A result is appended as a query
    /// parameter so the page can say what happened.
    /// </remarks>
    public string CompletionUrl { get; set; } = "http://localhost:3000/settings/email";

    /// <summary>
    /// The scope requested.
    /// </summary>
    /// <remarks>
    /// <c>gmail.send</c> alone. It sends mail and cannot read the mailbox,
    /// which is the narrowest scope that does the job: a token stolen from
    /// this system could send mail, which is bad, but could not read the
    /// user's correspondence, which would be worse.
    /// </remarks>
    public string Scope { get; set; } = "openid email https://www.googleapis.com/auth/gmail.send";

    /// <summary>How long an authorization attempt may stay open, in seconds.</summary>
    /// <remarks>
    /// Ten minutes. Long enough to sign in and pick an account, short enough
    /// that an abandoned attempt cannot be resumed from a shared machine
    /// later.
    /// </remarks>
    [Range(60, 1800)]
    public int AuthorizationTimeoutSeconds { get; set; } = 600;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(this.ClientId)
        && !string.IsNullOrWhiteSpace(this.ClientSecret)
        && !string.IsNullOrWhiteSpace(this.RedirectUri);
}
