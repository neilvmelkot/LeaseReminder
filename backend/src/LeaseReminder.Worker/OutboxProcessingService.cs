using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeaseReminder.Application.Notifications;
using LeaseReminder.Domain;
using Microsoft.Extensions.Options;

namespace LeaseReminder.Worker;

/// <summary>
/// Turns task events into notifications.
///
/// <para>The loop is deliberately boring:</para>
/// <list type="number">
///   <item>Claim a batch of outbox events, atomically, oldest first.</item>
///   <item>For each: find the rules that match, resolve their roles into
///         addressees, compose a message per recipient, write a delivery.</item>
///   <item>Mark the event processed. Or failed, with backoff.</item>
/// </list>
///
/// <h3>Why nothing is sent here</h3>
///
/// This service writes delivery ROWS; it does not contact a provider. An
/// in-app delivery is complete the moment it is written, and an email delivery
/// is left pending for <see cref="NotificationEmailService"/> to pick up.
///
/// That split matters. Composing a notification is fast, deterministic, and
/// must happen exactly once per recipient; sending an email is slow, fails in
/// interesting ways, and needs its own retry schedule. Combining them would
/// mean a Gmail timeout caused the whole event to be retried, and the second
/// attempt would re-resolve recipients against a task that may have changed in
/// between. Separating them means the composition is settled and durable
/// before any network call is attempted.
///
/// <h3>Why an event that matches no rule still succeeds</h3>
///
/// If an organization has disabled every rule for an event, there is nothing
/// to notify and nothing has gone wrong. The event is marked processed. Failing
/// it would fill the dead-letter queue with events whose only problem is that
/// somebody turned a notification off.
/// </summary>
public sealed partial class OutboxProcessingService(
    WorkerDatabase database,
    IOptions<WorkerOptions> options,
    ILogger<OutboxProcessingService> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:outbox";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(logger, _workerId, _options.OutboxPollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);

                // A full batch probably means more is waiting, so poll again
                // at once rather than sleeping through a backlog. Task events
                // arrive in bursts when somebody triages a board.
                if (processed >= _options.OutboxBatchSize)
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
                        TimeSpan.FromSeconds(_options.OutboxPollIntervalSeconds),
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
        var events = await database
            .ClaimOutboxEventsAsync(
                _workerId,
                _options.OutboxBatchSize,
                TimeSpan.FromSeconds(_options.LockSeconds),
                stoppingToken)
            .ConfigureAwait(false);

        if (events.Count == 0)
        {
            return 0;
        }

        LogClaimed(logger, events.Count);

        // Processed sequentially, not in parallel. Events for one task must be
        // handled in order, and the batch is ordered by occurrence; running
        // them concurrently would let "completed" overtake "moved to In
        // Progress" and deliver the two notifications the wrong way round.
        // The work is all local composition and small inserts, so the
        // sequential cost is negligible.
        foreach (var outboxEvent in events)
        {
            await ProcessOneAsync(outboxEvent, stoppingToken).ConfigureAwait(false);
        }

        return events.Count;
    }

    private async Task ProcessOneAsync(
        OutboxEvent outboxEvent,
        CancellationToken stoppingToken)
    {
        try
        {
            // Only task events are understood today. A lease or document event
            // arriving here is a producer that has run ahead of this consumer,
            // which is a permanent condition for that row: retrying it five
            // times would change nothing.
            if (outboxEvent.AggregateType != "task")
            {
                LogUnsupportedAggregate(
                    logger, outboxEvent.Id, outboxEvent.AggregateType);

                await database
                    .FailOutboxEventAsync(
                        outboxEvent.Id,
                        _workerId,
                        $"No handler for aggregate type '{outboxEvent.AggregateType}'.",
                        permanent: true,
                        stoppingToken)
                    .ConfigureAwait(false);

                return;
            }

            using var payloadDocument = outboxEvent.ParsePayload();
            var payload = payloadDocument.RootElement;

            var rules = await database
                .GetMatchingRulesAsync(
                    outboxEvent.AggregateId, outboxEvent.EventType, stoppingToken)
                .ConfigureAwait(false);

            if (rules.Count == 0)
            {
                // Nothing to notify. Not a failure: somebody turned a rule off.
                LogNoMatchingRules(logger, outboxEvent.Id, outboxEvent.EventType);

                await database
                    .CompleteOutboxEventAsync(outboxEvent.Id, _workerId, stoppingToken)
                    .ConfigureAwait(false);

                return;
            }

            // Whether this event concerns an internal note. The flag travels in
            // the payload precisely so that recipient resolution can keep an
            // internal note away from the tenant it may be about.
            var isInternal =
                payload.TryGetProperty("is_internal", out var internalFlag)
                && internalFlag.ValueKind == JsonValueKind.True;

            var deliveriesWritten = 0;

            foreach (var rule in rules)
            {
                var recipients = await database
                    .ResolveRecipientsAsync(
                        outboxEvent.AggregateId,
                        rule.RuleId,
                        outboxEvent.ActorUserId,
                        isInternal,
                        stoppingToken)
                    .ConfigureAwait(false);

                foreach (var recipient in recipients)
                {
                    deliveriesWritten += await WriteDeliveriesAsync(
                            outboxEvent, payload, rule, recipient, stoppingToken)
                        .ConfigureAwait(false);
                }
            }

            await database
                .CompleteOutboxEventAsync(outboxEvent.Id, _workerId, stoppingToken)
                .ConfigureAwait(false);

            LogProcessed(logger, outboxEvent.Id, outboxEvent.EventType, deliveriesWritten);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-event. The claim lock expires and the event is
            // picked up again; the delivery idempotency keys mean nothing is
            // notified twice.
            throw;
        }
        catch (JsonException exception)
        {
            // A malformed payload will never parse, however many times it is
            // retried. Dead-letter it immediately rather than five times.
            LogMalformedPayload(logger, outboxEvent.Id, exception);

            await database
                .FailOutboxEventAsync(
                    outboxEvent.Id, _workerId,
                    $"Malformed event payload: {exception.GetType().Name}",
                    permanent: true,
                    stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogEventFailed(logger, outboxEvent.Id, exception);

            try
            {
                await database
                    .FailOutboxEventAsync(
                        outboxEvent.Id, _workerId,
                        // The exception TYPE only. A message can contain
                        // recipient addresses or task content.
                        $"Processing failed: {exception.GetType().Name}",
                        permanent: false,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception recordFailure)
            {
                // Could not even record the failure. The lock will expire and
                // the event will be retried, which is the safe outcome.
                LogFailureNotRecorded(logger, outboxEvent.Id, recordFailure);
            }
        }
    }

    /// <summary>
    /// Writes one delivery per channel the rule asks for.
    /// </summary>
    private async Task<int> WriteDeliveriesAsync(
        OutboxEvent outboxEvent,
        JsonElement payload,
        RuleMatch rule,
        ResolvedRecipient recipient,
        CancellationToken stoppingToken)
    {
        var role = ParseRole(recipient.Role);

        // The wording depends on the role: a tenant gets "the repair you
        // reported is finished", a manager gets the reference number and the
        // cost. One message for both would either be jargon to the tenant or
        // useless to the manager.
        var composed = TaskNotificationComposer.Compose(
            outboxEvent.EventType,
            payload,
            role,
            outboxEvent.ActorDisplayName);

        var written = 0;

        foreach (var channel in rule.Channels)
        {
            // In-app needs a user; email needs an address. A vendor with no
            // login has an address but no user id, and a rule asking for
            // in-app simply does not reach them. Skipping is correct: the
            // database CHECK would reject the row anyway, and failing the
            // whole event over it would block every other recipient.
            var canDeliver = channel switch
            {
                "in_app" => recipient.UserId.HasValue,
                "email" => !string.IsNullOrEmpty(recipient.Email),
                _ => false,
            };

            if (!canDeliver)
            {
                continue;
            }

            var deliveryId = await database
                .CreateDeliveryAsync(
                    outboxEvent.OrganizationId,
                    outboxEvent.Id,
                    "task",
                    outboxEvent.AggregateId,
                    channel,
                    channel == "in_app" ? recipient.UserId : null,
                    channel == "email" ? recipient.Email : null,
                    recipient.Role,
                    rule.RuleId,
                    composed.Title,
                    composed.Body,
                    composed.LinkPath,
                    BuildDeliveryKey(outboxEvent, rule, recipient, channel),
                    stoppingToken)
                .ConfigureAwait(false);

            // Null means the key already existed, which happens when this
            // event is being re-processed after a crash. That is the
            // idempotency guarantee working, not a failure.
            if (deliveryId.HasValue)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// The deterministic key that prevents a duplicate notification.
    ///
    /// <para>
    /// Built from the outbox event, the rule, the recipient, and the channel:
    /// re-processing the same event produces identical keys, so the inserts
    /// collapse onto the existing rows and nobody is notified twice. The UNIQUE
    /// constraint on (organization_id, idempotency_key) is what enforces it;
    /// this function only has to be deterministic.
    /// </para>
    ///
    /// <para>
    /// The recipient part is hashed because an email address can be 320
    /// characters and the column accepts 200. Hashing also keeps an address out
    /// of a column that is read in operational tooling.
    /// </para>
    /// </summary>
    private static string BuildDeliveryKey(
        OutboxEvent outboxEvent,
        RuleMatch rule,
        ResolvedRecipient recipient,
        string channel)
    {
        var recipientIdentity = recipient.UserId?.ToString("N", CultureInfo.InvariantCulture)
                                ?? recipient.Email
                                ?? "unknown";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(recipientIdentity));
        var fingerprint = Convert.ToHexStringLower(hash)[..12];

        return string.Join(':',
            "v1",
            outboxEvent.Id.ToString(CultureInfo.InvariantCulture),
            rule.RuleId.ToString("N", CultureInfo.InvariantCulture),
            fingerprint,
            channel);
    }

    private static TaskRecipientRole ParseRole(string label) => label switch
    {
        "assignee" => TaskRecipientRole.Assignee,
        "task_creator" => TaskRecipientRole.TaskCreator,
        "reporting_tenant" => TaskRecipientRole.ReportingTenant,
        "property_manager" => TaskRecipientRole.PropertyManager,
        "property_owner" => TaskRecipientRole.PropertyOwner,
        "vendor" => TaskRecipientRole.Vendor,
        "custom_recipient" => TaskRecipientRole.CustomRecipient,
        "recipient_group" => TaskRecipientRole.RecipientGroup,

        // An unknown role still gets a notification, with the neutral wording.
        // Dropping it would silently stop telling somebody.
        _ => TaskRecipientRole.CustomRecipient,
    };

    // -------------------------------------------------------------------------
    // Logging. None of these templates carries a recipient address, a task
    // title, or message content (section 4.K): identifiers are enough to find
    // the row.
    // -------------------------------------------------------------------------

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "Outbox processor {WorkerId} started, polling every {PollSeconds}s")]
    private static partial void LogStarting(ILogger logger, string workerId, int pollSeconds);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information,
        Message = "Outbox processor {WorkerId} stopped cleanly")]
    private static partial void LogStopping(ILogger logger, string workerId);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Debug,
        Message = "Claimed {Count} outbox events")]
    private static partial void LogClaimed(ILogger logger, int count);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error,
        Message = "An outbox batch failed; the loop continues")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Debug,
        Message = "Event {OutboxId} ({EventType}) produced {DeliveryCount} deliveries")]
    private static partial void LogProcessed(
        ILogger logger, long outboxId, string eventType, int deliveryCount);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Debug,
        Message = "Event {OutboxId} ({EventType}) matched no enabled rule; nothing to notify")]
    private static partial void LogNoMatchingRules(
        ILogger logger, long outboxId, string eventType);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Error,
        Message = "Event {OutboxId} has aggregate type {AggregateType}, which has no handler")]
    private static partial void LogUnsupportedAggregate(
        ILogger logger, long outboxId, string aggregateType);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Error,
        Message = "Event {OutboxId} has a malformed payload and has been dead-lettered")]
    private static partial void LogMalformedPayload(
        ILogger logger, long outboxId, Exception exception);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Error,
        Message = "Processing event {OutboxId} threw")]
    private static partial void LogEventFailed(
        ILogger logger, long outboxId, Exception exception);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Error,
        Message = "Could not record the failure for event {OutboxId}; " +
                  "its lock will expire and it will be retried")]
    private static partial void LogFailureNotRecorded(
        ILogger logger, long outboxId, Exception exception);
}
