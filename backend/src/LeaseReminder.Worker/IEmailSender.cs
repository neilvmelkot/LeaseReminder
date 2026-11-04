namespace LeaseReminder.Worker;

/// <summary>
/// Sends one already-rendered message.
///
/// <para>
/// Narrow on purpose. The sender does not decide who receives a message, does
/// not choose a template, and does not consult the database. All of that is
/// settled before it is called, which keeps the Gmail integration a leaf of
/// the system rather than a participant in authorization. Section 4.D is
/// explicit that document content must not be able to select recipients or
/// authorize Gmail access, and the simplest way to guarantee that is for the
/// component holding the credential to have no say in either.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// True when a usable sender is configured. The dispatcher logs a clear
    /// reason and stops rather than looping over work it cannot complete.
    /// </summary>
    bool IsConfigured { get; }

    Task<EmailSendOutcome> SendAsync(OutgoingEmail message, CancellationToken cancellationToken);
}

public sealed record OutgoingEmail
{
    /// <summary>
    /// Whose mailbox to send from.
    /// </summary>
    /// <remarks>
    /// The organization, not an account id. Section 13.B gives an
    /// organization exactly one connected mailbox and no fallback, so the
    /// sender resolves it rather than being told: a caller that could name an
    /// account could name a different one.
    /// </remarks>
    public required Guid OrganizationId { get; init; }

    /// <summary>
    /// The address a caller believes it is sending from.
    /// </summary>
    /// <remarks>
    /// Advisory. The Gmail sender ignores it and uses the connected mailbox,
    /// because that is the only address Google will accept from this token
    /// anyway. Kept because a test or a future provider may want it.
    /// </remarks>
    public required string FromAddress { get; init; }

    public string? FromDisplayName { get; init; }

    /// <summary>Primary recipients.</summary>
    public required IReadOnlyList<string> To { get; init; }

    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>
    /// Blind recipients. Section 6.H requires that recipients who should not
    /// see one another's addresses do not; BCC and one-message-per-recipient
    /// are the two mechanisms, and the rule chooses between them.
    /// </summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public required string TextBody { get; init; }

    /// <summary>
    /// Passed to the provider where it supports one, and used for the log
    /// entry either way.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

public sealed record EmailSendOutcome
{
    /// <summary>
    /// True when the provider ACCEPTED the send request.
    ///
    /// <para>
    /// Named Accepted rather than Delivered or Sent deliberately. Section 6.J
    /// is explicit: a successful Gmail API response means the provider took
    /// the request, not that the message reached an inbox. Nothing in this
    /// system may claim otherwise, and naming the field honestly is the
    /// cheapest way to stop it drifting.
    /// </para>
    /// </summary>
    public required bool Accepted { get; init; }

    public string? ProviderMessageId { get; init; }

    /// <summary>
    /// One of the app.email_error_category labels. Determines whether the
    /// dispatcher retries, gives up, or flags the account for
    /// reauthorization.
    /// </summary>
    public string ErrorCategory { get; init; } = "none";

    /// <summary>
    /// Operator-safe summary. Never a raw provider payload, which can contain
    /// recipient content.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True for an error that will never succeed on retry: an invalid
    /// recipient, a revoked grant, a rejected scope. These are dead-lettered
    /// immediately rather than retried five times (section 4.H).
    /// </summary>
    public bool IsPermanent { get; init; }

    public static EmailSendOutcome Success(string? providerMessageId) =>
        new() { Accepted = true, ProviderMessageId = providerMessageId };

    public static EmailSendOutcome Transient(string category, string message) =>
        new() { Accepted = false, ErrorCategory = category, ErrorMessage = message };

    public static EmailSendOutcome Permanent(string category, string message) =>
        new()
        {
            Accepted = false,
            ErrorCategory = category,
            ErrorMessage = message,
            IsPermanent = true,
        };
}

/// <summary>
/// The phase 1 sender: there is none.
///
/// <para>
/// Phase 4 brings the Gmail integration. Until then this records a
/// configuration error rather than pretending to send, because the one
/// behaviour that must never occur is a notification marked sent that nobody
/// received.
/// </para>
///
/// <para>
/// In practice it is rarely reached: app.claim_due_notifications only returns
/// work whose Gmail account is <c>connected</c>, and phase 1 has no way to
/// connect one, so there is nothing to claim. This exists for the case where
/// an account row is connected but the application build has no sender
/// compiled in, which is a deployment mistake worth reporting loudly.
/// </para>
/// </summary>
public sealed class UnconfiguredEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task<EmailSendOutcome> SendAsync(
        OutgoingEmail message,
        CancellationToken cancellationToken) =>
        Task.FromResult(EmailSendOutcome.Permanent(
            "configuration",
            "No email provider is configured in this build. The Gmail integration " +
            "arrives in phase 4. The notification has not been sent."));
}
