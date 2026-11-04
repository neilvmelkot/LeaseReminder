using System.Text.Json;
using LeaseReminder.Application.Templates;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Worker;

/// <summary>
/// Polls for due notifications and sends them.
///
/// <para>The structure follows section 7 closely, and each step matters:</para>
/// <list type="number">
///   <item>
///     <b>Claim atomically, in a tiny transaction.</b>
///     app.claim_due_notifications uses FOR UPDATE SKIP LOCKED, so several
///     workers claim disjoint batches without blocking each other.
///   </item>
///   <item>
///     <b>Send outside every database lock.</b> The provider call can take
///     seconds; holding a row lock across it would serialise the queue and
///     could hold a transaction open longer than the connection's lifetime.
///   </item>
///   <item>
///     <b>Record the result in a second transaction.</b> Which also writes the
///     email log, so the queue row and the log can never disagree.
///   </item>
/// </list>
///
/// <para>
/// PostgreSQL is the only source of truth. This loop holds no schedule in
/// memory, so a restart, a redeploy, or a container being moved loses nothing:
/// the next poll finds the same due work, and the idempotency key stops a
/// partially-processed batch being sent twice.
/// </para>
/// </summary>
public sealed partial class NotificationDispatchService(
    WorkerDatabase database,
    IEmailSender emailSender,
    IOptions<WorkerOptions> options,
    ILogger<NotificationDispatchService> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    /// <summary>
    /// Identifies this process in a claim. Including the machine name and the
    /// process id makes an abandoned lock traceable to the container that
    /// died holding it.
    /// </summary>
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:dispatch";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(logger, _workerId, _options.PollIntervalSeconds);

        if (!emailSender.IsConfigured)
        {
            // Said once, clearly, at startup. The loop still runs, because the
            // claim query returns nothing while no Gmail account is connected,
            // and a worker that refused to start would also stop the job queue.
            LogNoSenderConfigured(logger);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);

                // A full batch probably means more is waiting, so poll again
                // immediately rather than sleeping through a backlog.
                if (processed >= _options.DispatchBatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A transient database problem must not kill the loop, or the
                // platform restarts the container and the backlog grows.
                LogBatchFailed(logger, exception);
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(_options.PollIntervalSeconds),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogStopping(logger, _workerId);
    }

    private async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        var claimed = await database
            .ClaimDueNotificationsAsync(
                _workerId,
                _options.DispatchBatchSize,
                TimeSpan.FromSeconds(_options.LockSeconds),
                stoppingToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return 0;
        }

        LogClaimed(logger, claimed.Count);

        // Bounded concurrency (section 4.H). Enough to overlap provider
        // latency, not enough to trip a provider rate limit or exhaust the
        // connection pool.
        using var concurrency = new SemaphoreSlim(_options.MaxConcurrency);

        var work = claimed.Select(async notification =>
        {
            await concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await DispatchOneAsync(notification, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        return claimed.Count;
    }

    private async Task DispatchOneAsync(
        ClaimedNotification notification,
        CancellationToken stoppingToken)
    {
        try
        {
            using var context = await database
                .GetNotificationContextAsync(notification.Id, _workerId, stoppingToken)
                .ConfigureAwait(false);

            if (context is null)
            {
                // The claim expired and another worker owns the row. Doing
                // nothing is correct: recording a result here could produce a
                // second log entry, or worse a second send.
                LogClaimLost(logger, notification.Id);
                return;
            }

            var root = context.RootElement;

            var senderAddress = root.GetProperty("sender").GetProperty("email_address").GetString()!;
            var senderName = root.GetProperty("sender")
                .TryGetProperty("display_name", out var displayName)
                ? displayName.GetString()
                : null;
            var accountId = root.GetProperty("sender").GetProperty("email_account_id").GetGuid();
            var dailyLimit = root.GetProperty("sender").GetProperty("daily_send_limit").GetInt32();

            // Per-account daily ceiling. Checked before the provider call so a
            // runaway rule cannot burn through a Gmail quota (section 4.G).
            if (!await database
                    .TryConsumeSendQuotaAsync(accountId, dailyLimit, stoppingToken)
                    .ConfigureAwait(false))
            {
                LogQuotaExceeded(logger, accountId, dailyLimit);

                await database.RecordResultAsync(
                        notification.Id, _workerId, accepted: false,
                        providerMessageId: null,
                        errorCategory: "quota",
                        errorMessage: $"The daily send limit of {dailyLimit} for this account " +
                                      "has been reached. The notification will be retried.",
                        permanent: false,
                        stoppingToken)
                    .ConfigureAwait(false);

                return;
            }

            // Suppression is re-checked here, not trusted from the snapshot
            // taken when the notification was generated. Somebody may have
            // opted out in between.
            using var recipients = await database
                .FilterSuppressedRecipientsAsync(
                    notification.OrganizationId,
                    root.GetProperty("recipients").GetRawText(),
                    stoppingToken)
                .ConfigureAwait(false);

            var (to, cc, bcc) = SplitRecipients(recipients.RootElement);

            if (to.Count == 0 && cc.Count == 0 && bcc.Count == 0)
            {
                // Everyone was suppressed. Skipped, not failed: nothing is
                // wrong with the system and retrying would change nothing.
                LogAllRecipientsSuppressed(logger, notification.Id);

                await database.RecordResultAsync(
                        notification.Id, _workerId, accepted: false,
                        providerMessageId: null,
                        errorCategory: "invalid_recipient",
                        errorMessage: "Every recipient is on the suppression list.",
                        permanent: true,
                        stoppingToken)
                    .ConfigureAwait(false);

                return;
            }

            var rendered = Render(root);

            // Section 6.H: when recipients must not see one another, send
            // separate messages rather than relying on BCC.
            var sendSeparately = root.GetProperty("send_separately").GetBoolean();

            var outcome = sendSeparately
                ? await SendSeparatelyAsync(
                        notification.OrganizationId,
                        rendered, senderAddress, senderName,
                        to.Concat(cc).Concat(bcc).ToList(),
                        root.GetProperty("idempotency_key").GetString()!,
                        stoppingToken)
                    .ConfigureAwait(false)
                : await emailSender.SendAsync(
                        new OutgoingEmail
                        {
                            OrganizationId = notification.OrganizationId,
                            FromAddress = senderAddress,
                            FromDisplayName = senderName,
                            To = to,
                            Cc = cc,
                            Bcc = bcc,
                            Subject = rendered.Subject,
                            HtmlBody = rendered.HtmlBody,
                            TextBody = rendered.TextBody,
                            IdempotencyKey = root.GetProperty("idempotency_key").GetString()!,
                        },
                        stoppingToken)
                    .ConfigureAwait(false);

            var status = await database.RecordResultAsync(
                    notification.Id,
                    _workerId,
                    outcome.Accepted,
                    outcome.ProviderMessageId,
                    outcome.ErrorCategory,
                    outcome.ErrorMessage,
                    outcome.IsPermanent,
                    stoppingToken)
                .ConfigureAwait(false);

            if (outcome.Accepted)
            {
                LogAccepted(logger, notification.Id, outcome.ProviderMessageId ?? "none");
            }
            else
            {
                LogSendFailed(
                    logger, notification.Id, outcome.ErrorCategory, status ?? "unknown");

                // Repeated failures pause the rule and cancel its pending
                // sends, rather than letting it retry indefinitely and fill
                // the log with the same error (section 4.G).
                if (await database
                        .PauseRuleOnRepeatedFailureAsync(
                            notification.NotificationRuleId, stoppingToken)
                        .ConfigureAwait(false))
                {
                    LogRulePaused(logger, notification.NotificationRuleId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-notification. The claim lock expires and
            // another worker, or this one after restart, picks it up. Nothing
            // is lost because nothing was marked sent.
            throw;
        }
        catch (Exception exception)
        {
            LogDispatchFailed(logger, notification.Id, exception);

            try
            {
                await database.RecordResultAsync(
                        notification.Id, _workerId, accepted: false,
                        providerMessageId: null,
                        errorCategory: "unknown",
                        // The exception TYPE only. A message can contain
                        // recipient addresses or lease content.
                        errorMessage: $"Dispatch failed: {exception.GetType().Name}",
                        permanent: false,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception recordFailure)
            {
                // Could not even record the failure. The lock will expire and
                // the notification will be retried, which is the safe outcome.
                LogResultNotRecorded(logger, notification.Id, recordFailure);
            }
        }
    }

    /// <summary>
    /// One message per recipient, so no recipient learns who else was
    /// contacted. Accepted only if every message was accepted: a partial
    /// success must not be recorded as a success.
    /// </summary>
    private async Task<EmailSendOutcome> SendSeparatelyAsync(
        Guid organizationId,
        RenderedEmail rendered,
        string senderAddress,
        string? senderName,
        IReadOnlyList<string> allRecipients,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var messageIds = new List<string>();

        foreach (var recipient in allRecipients)
        {
            var outcome = await emailSender.SendAsync(
                    new OutgoingEmail
                    {
                        OrganizationId = organizationId,
                        FromAddress = senderAddress,
                        FromDisplayName = senderName,
                        To = [recipient],
                        Subject = rendered.Subject,
                        HtmlBody = rendered.HtmlBody,
                        TextBody = rendered.TextBody,

                        // Per-recipient key, so a retry of a partially
                        // completed fan-out does not re-send to the people who
                        // already received it.
                        IdempotencyKey = $"{idempotencyKey}:{recipient}",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Accepted)
            {
                return outcome;
            }

            if (outcome.ProviderMessageId is not null)
            {
                messageIds.Add(outcome.ProviderMessageId);
            }
        }

        return EmailSendOutcome.Success(string.Join(',', messageIds));
    }

    /// <summary>
    /// Renders the subject and both bodies from the template and the variable
    /// values the database projection supplied.
    /// </summary>
    private RenderedEmail Render(JsonElement context)
    {
        var template = context.GetProperty("template");

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var variable in context.GetProperty("variables").EnumerateObject())
        {
            values[variable.Name] = variable.Value.ValueKind switch
            {
                JsonValueKind.String => variable.Value.GetString(),
                JsonValueKind.Null => null,
                _ => variable.Value.GetRawText(),
            };
        }

        // The subject is escaped as text, not HTML: it is a header, and an
        // escaped entity there would be shown literally to the reader.
        var subject = TemplateRenderer.Render(
            template.GetProperty("subject").GetString()!, values, escapeForHtml: false);

        var htmlBody = TemplateRenderer.Render(
            template.GetProperty("html_body").GetString()!, values, escapeForHtml: true);

        var textBody = TemplateRenderer.Render(
            template.GetProperty("text_body").GetString()!, values, escapeForHtml: false);

        // A template referencing an unknown variable is a configuration
        // problem that only shows up at send time. Logged as a warning rather
        // than failing the send, because a reminder with one blank field still
        // beats no reminder at all.
        if (htmlBody.UnknownVariables.Count > 0)
        {
            LogUnknownTemplateVariables(
                logger,
                template.GetProperty("template_id").GetGuid(),
                string.Join(", ", htmlBody.UnknownVariables));
        }

        return new RenderedEmail
        {
            Subject = subject.Output,
            HtmlBody = htmlBody.Output,
            TextBody = textBody.Output,
        };
    }

    private static (List<string> To, List<string> Cc, List<string> Bcc) SplitRecipients(
        JsonElement recipients)
    {
        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();

        foreach (var recipient in recipients.EnumerateArray())
        {
            if (!recipient.TryGetProperty("email", out var emailProperty))
            {
                continue;
            }

            var email = emailProperty.GetString();

            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            var kind = recipient.TryGetProperty("kind", out var kindProperty)
                ? kindProperty.GetString()
                : "to";

            switch (kind)
            {
                case "cc":
                    cc.Add(email);
                    break;
                case "bcc":
                    bcc.Add(email);
                    break;
                default:
                    to.Add(email);
                    break;
            }
        }

        return (to, cc, bcc);
    }

    private sealed record RenderedEmail
    {
        public required string Subject { get; init; }

        public required string HtmlBody { get; init; }

        public required string TextBody { get; init; }
    }

    // -------------------------------------------------------------------------
    // Logging
    //
    // Source-generated, and none of these templates carries a recipient
    // address, a lease term, or a message body (section 4.K). Notification and
    // rule identifiers are enough to find the row.
    // -------------------------------------------------------------------------

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Notification dispatcher {WorkerId} started, polling every {PollSeconds}s")]
    private static partial void LogStarting(ILogger logger, string workerId, int pollSeconds);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "Notification dispatcher {WorkerId} stopped cleanly")]
    private static partial void LogStopping(ILogger logger, string workerId);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "No email provider is configured, so nothing can be sent. " +
                  "The Gmail integration arrives in phase 4.")]
    private static partial void LogNoSenderConfigured(ILogger logger);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information,
        Message = "Claimed {Count} due notifications")]
    private static partial void LogClaimed(ILogger logger, int count);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
        Message = "A dispatch batch failed; the loop continues")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning,
        Message = "Claim on notification {NotificationId} was lost before sending; skipping")]
    private static partial void LogClaimLost(ILogger logger, Guid notificationId);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information,
        Message = "Provider accepted notification {NotificationId} as {ProviderMessageId}")]
    private static partial void LogAccepted(
        ILogger logger, Guid notificationId, string providerMessageId);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Warning,
        Message = "Notification {NotificationId} failed ({ErrorCategory}); now {Status}")]
    private static partial void LogSendFailed(
        ILogger logger, Guid notificationId, string errorCategory, string status);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Error,
        Message = "Dispatch of notification {NotificationId} threw")]
    private static partial void LogDispatchFailed(
        ILogger logger, Guid notificationId, Exception exception);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Error,
        Message = "Could not record the result for notification {NotificationId}; " +
                  "its lock will expire and it will be retried")]
    private static partial void LogResultNotRecorded(
        ILogger logger, Guid notificationId, Exception exception);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Warning,
        Message = "Rule {RuleId} auto-paused after repeated send failures")]
    private static partial void LogRulePaused(ILogger logger, Guid ruleId);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Warning,
        Message = "Account {AccountId} reached its daily send limit of {DailyLimit}")]
    private static partial void LogQuotaExceeded(
        ILogger logger, Guid accountId, int dailyLimit);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Information,
        Message = "Every recipient of notification {NotificationId} is suppressed; skipping")]
    private static partial void LogAllRecipientsSuppressed(
        ILogger logger, Guid notificationId);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Warning,
        Message = "Template {TemplateId} references unknown variables: {Variables}")]
    private static partial void LogUnknownTemplateVariables(
        ILogger logger, Guid templateId, string variables);
}
