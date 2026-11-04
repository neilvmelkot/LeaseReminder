using System.Net;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Worker;

/// <summary>
/// Sends the email half of the notifications the outbox processor composed.
///
/// <para>
/// Separate from <see cref="OutboxProcessingService"/> on purpose. Composing a
/// notification is fast, deterministic, and must happen exactly once per
/// recipient; sending an email is slow, fails in interesting ways, and needs
/// its own retry schedule. If one service did both, a Gmail timeout would
/// cause the whole event to be retried, and the retry would re-resolve
/// recipients against a task that may have changed in between.
/// </para>
///
/// <para>
/// With them separated, the composition is settled and durable before any
/// network call happens, and a send failure retries only the send.
/// </para>
///
/// <para>
/// In phase 1 the sender is <see cref="UnconfiguredEmailSender"/>, and the
/// claim function returns nothing while no Gmail account is connected, so this
/// loop finds no work. It is written now because the alternative is a
/// notification centre whose email column silently never drains.
/// </para>
/// </summary>
public sealed partial class NotificationEmailService(
    WorkerDatabase database,
    IEmailSender emailSender,
    IOptions<WorkerOptions> options,
    ILogger<NotificationEmailService> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:notification-email";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(logger, _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);

                if (sent >= _options.DispatchBatchSize)
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
        var pending = await database
            .ClaimPendingEmailDeliveriesAsync(
                _workerId, _options.DispatchBatchSize, stoppingToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        using var concurrency = new SemaphoreSlim(_options.MaxConcurrency);

        var work = pending.Select(async delivery =>
        {
            await concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await SendOneAsync(delivery, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        return pending.Count;
    }

    private async Task SendOneAsync(
        PendingDeliveryEmail delivery,
        CancellationToken stoppingToken)
    {
        try
        {
            var outcome = await emailSender
                .SendAsync(
                    new OutgoingEmail
                    {
                        // The sender address is resolved by the Gmail
                        // integration from the connected account, which is the
                        // only account that may send (section 13.B). There is
                        // deliberately no fallback sender.
                        OrganizationId = delivery.OrganizationId,
                        FromAddress = "notifications@connected-account",
                        To = [delivery.RecipientEmail],
                        Subject = delivery.Title,
                        HtmlBody = BuildHtmlBody(delivery),
                        TextBody = BuildTextBody(delivery),
                        IdempotencyKey = delivery.DeliveryId.ToString("N"),
                    },
                    stoppingToken)
                .ConfigureAwait(false);

            await database
                .RecordDeliveryResultAsync(
                    delivery.DeliveryId,
                    outcome.Accepted,
                    outcome.ProviderMessageId,
                    outcome.ErrorCategory,
                    outcome.ErrorMessage,
                    stoppingToken)
                .ConfigureAwait(false);

            if (outcome.Accepted)
            {
                LogAccepted(logger, delivery.DeliveryId);
            }
            else
            {
                LogSendFailed(logger, delivery.DeliveryId, outcome.ErrorCategory);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSendThrew(logger, delivery.DeliveryId, exception);

            await database
                .RecordDeliveryResultAsync(
                    delivery.DeliveryId,
                    accepted: false,
                    providerMessageId: null,
                    errorCategory: "unknown",
                    // The exception TYPE only: a message can contain the
                    // recipient address or task content.
                    errorMessage: $"Send failed: {exception.GetType().Name}",
                    stoppingToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The HTML body for a notification email.
    ///
    /// <para>
    /// Every interpolated value is HTML-escaped. The title and body originate
    /// from a task title, a comment excerpt, or a property name, all of which
    /// are user-supplied text that reaches a recipient's mail client. Escaping
    /// here is the same requirement section 6.I places on the lease templates,
    /// applied to the one other place text becomes markup.
    /// </para>
    /// </summary>
    private static string BuildHtmlBody(PendingDeliveryEmail delivery)
    {
        var title = HtmlEncoder.Default.Encode(delivery.Title);
        var body = HtmlEncoder.Default.Encode(delivery.Body);

        var link = delivery.LinkPath is { Length: > 0 } path
            // The path is already constrained to a relative form by a CHECK
            // constraint, so it cannot carry a scheme or a host.
            ? $"""
               <p style="margin:20px 0 0;">
                 <a href="{HtmlEncoder.Default.Encode(path)}"
                    style="color:#2f52c8;text-decoration:none;font-weight:600;">
                   Open this task
                 </a>
               </p>
               """
            : string.Empty;

        return $"""
                <div style="margin:0;padding:24px;background:#f6f7f9;
                            font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
                  <div style="max-width:560px;margin:0 auto;background:#ffffff;
                              border:1px solid #e5e8ee;border-radius:10px;padding:28px;">
                    <h1 style="margin:0 0 14px;font-size:18px;line-height:1.35;
                               color:#141c2b;font-weight:600;">{title}</h1>
                    <p style="margin:0;font-size:15px;line-height:1.6;color:#141c2b;">{body}</p>
                    {link}
                  </div>
                </div>
                """;
    }

    private static string BuildTextBody(PendingDeliveryEmail delivery)
    {
        // Not escaped, and that is correct: escaping the plain-text part would
        // show "&amp;" to the reader.
        var builder = new System.Text.StringBuilder();

        builder.AppendLine(delivery.Title);
        builder.AppendLine();
        builder.AppendLine(delivery.Body);

        if (delivery.LinkPath is { Length: > 0 } path)
        {
            builder.AppendLine();
            builder.Append("Open: ").AppendLine(path);
        }

        return builder.ToString();
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information,
        Message = "Notification email sender {WorkerId} started")]
    private static partial void LogStarting(ILogger logger, string workerId);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Information,
        Message = "Notification email sender {WorkerId} stopped cleanly")]
    private static partial void LogStopping(ILogger logger, string workerId);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Error,
        Message = "A notification email batch failed; the loop continues")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Debug,
        Message = "Provider accepted notification delivery {DeliveryId}")]
    private static partial void LogAccepted(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 4205, Level = LogLevel.Warning,
        Message = "Notification delivery {DeliveryId} failed ({ErrorCategory})")]
    private static partial void LogSendFailed(
        ILogger logger, Guid deliveryId, string errorCategory);

    [LoggerMessage(EventId = 4206, Level = LogLevel.Error,
        Message = "Sending notification delivery {DeliveryId} threw")]
    private static partial void LogSendThrew(
        ILogger logger, Guid deliveryId, Exception exception);
}
