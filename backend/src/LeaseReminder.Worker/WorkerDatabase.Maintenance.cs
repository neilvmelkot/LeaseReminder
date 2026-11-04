using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace LeaseReminder.Worker;

/// <summary>
/// The maintenance half of the worker's database surface.
///
/// <para>
/// Same discipline as the rest of <see cref="WorkerDatabase"/>: every method
/// calls one of the SECURITY DEFINER functions in db/migrations/0015 and 0016.
/// The worker role holds no table privileges at all, verified by an assertion
/// in db/migrations/0017 that fails the migration if a table grant is ever
/// added to it.
/// </para>
/// </summary>
public sealed partial class WorkerDatabase
{
    /// <summary>
    /// Claims a batch of pending outbox events.
    ///
    /// <para>
    /// Oldest first, so notifications about one task arrive in the order the
    /// changes happened: "moved to In Progress" then "completed", never the
    /// reverse. Getting that backwards is not merely untidy; it tells somebody
    /// the job was reopened after it was finished.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<OutboxEvent>> ClaimOutboxEventsAsync(
        string workerId,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT id, organization_id, aggregate_type, aggregate_id, event_type,
                   payload::text, actor_user_id, actor_display_name, attempt_count
            FROM app.claim_outbox_events($1, $2, $3)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (int)lockDuration.TotalSeconds,
        });

        var events = new List<OutboxEvent>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new OutboxEvent
            {
                Id = reader.GetInt64(0),
                OrganizationId = reader.GetGuid(1),
                AggregateType = reader.GetString(2),
                AggregateId = reader.GetGuid(3),
                EventType = reader.GetString(4),
                PayloadJson = reader.GetString(5),
                ActorUserId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                ActorDisplayName = reader.IsDBNull(7) ? null : reader.GetString(7),
                AttemptCount = reader.GetInt32(8),
            });
        }

        return events;
    }

    /// <summary>
    /// The enabled rules that apply to this event on this task, with their
    /// recipient roles resolved to real addressees.
    ///
    /// <para>
    /// One round trip per rule rather than one per recipient. The alternative,
    /// fetching rules then recipients separately, is two round trips per rule
    /// and the rule set is small enough that it is not worth the complexity.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RuleMatch>> GetMatchingRulesAsync(
        Guid taskId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT id, name, channels::text[], max_sends_per_task_per_day
            FROM app.matching_task_notification_rules($1, $2)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = taskId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventType });

        var rules = new List<RuleMatch>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new RuleMatch
            {
                RuleId = reader.GetGuid(0),
                Name = reader.GetString(1),
                Channels = reader.GetFieldValue<string[]>(2),
                MaxSendsPerTaskPerDay = reader.GetInt32(3),
            });
        }

        return rules;
    }

    /// <summary>
    /// Resolves a rule's roles into addressees, against the task as it is now.
    ///
    /// <para>
    /// The function applies the three rules that cannot be applied anywhere
    /// else: a tenant who opted out is never returned, a reporting tenant is
    /// never returned for an internal note, and the actor is never returned
    /// for their own action.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ResolvedRecipient>> ResolveRecipientsAsync(
        Guid taskId,
        Guid ruleId,
        Guid? actorUserId,
        bool isInternal,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT recipient_user_id, recipient_email::text, display_name,
                   recipient_role::text, recipient_kind::text
            FROM app.resolve_task_notification_recipients($1, $2, $3, $4)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = taskId });
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = actorUserId.HasValue ? actorUserId.Value : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = isInternal });

        var recipients = new List<ResolvedRecipient>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            recipients.Add(new ResolvedRecipient
            {
                UserId = reader.IsDBNull(0) ? null : reader.GetGuid(0),
                Email = reader.IsDBNull(1) ? null : reader.GetString(1),
                DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Role = reader.GetString(3),
                Kind = reader.GetString(4),
            });
        }

        return recipients;
    }

    /// <summary>
    /// Writes one delivery.
    ///
    /// <para>
    /// The idempotency key is what actually prevents a duplicate: re-processing
    /// an event after a crash collapses onto the existing row rather than
    /// notifying somebody a second time. Returns null when that happens, which
    /// the caller treats as success, not as failure.
    /// </para>
    /// </summary>
    public async Task<Guid?> CreateDeliveryAsync(
        Guid organizationId,
        long outboxId,
        string subjectType,
        Guid subjectId,
        string channel,
        Guid? recipientUserId,
        string? recipientEmail,
        string recipientRole,
        Guid ruleId,
        string title,
        string body,
        string? linkPath,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT app.create_notification_delivery(
              $1, $2, $3, $4, $5::app.delivery_channel, $6, $7::citext,
              $8::app.task_recipient_role, $9, $10, $11, $12, $13)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
        command.Parameters.Add(new NpgsqlParameter { Value = outboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = subjectType });
        command.Parameters.Add(new NpgsqlParameter { Value = subjectId });
        command.Parameters.Add(new NpgsqlParameter { Value = channel });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = recipientUserId.HasValue ? recipientUserId.Value : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = recipientEmail ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = recipientRole });
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        command.Parameters.Add(new NpgsqlParameter { Value = title });
        command.Parameters.Add(new NpgsqlParameter { Value = body });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = linkPath ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is Guid id ? id : null;
    }

    public async Task<bool> CompleteOutboxEventAsync(
        long outboxId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.complete_outbox_event($1, $2)");

        command.Parameters.Add(new NpgsqlParameter { Value = outboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = workerId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is bool completed && completed;
    }

    public async Task<string?> FailOutboxEventAsync(
        long outboxId,
        string workerId,
        string errorMessage,
        bool permanent,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.fail_outbox_event($1, $2, $3, $4)");

        command.Parameters.Add(new NpgsqlParameter { Value = outboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter
        {
            // Truncated here as well as in SQL: the message can originate from
            // an exception and an operator-safe log must not become a dumping
            // ground for one.
            Value = errorMessage[..Math.Min(errorMessage.Length, 2000)],
        });
        command.Parameters.Add(new NpgsqlParameter { Value = permanent });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result?.ToString();
    }

    /// <summary>
    /// Raises the time-based task events: due soon, due today, overdue, stale.
    ///
    /// <para>
    /// Safe to call as often as you like. Each event's idempotency key includes
    /// the local date, so running the sweep five times in a day produces one
    /// overdue event rather than five. That property is why the sweep can be a
    /// simple unconditional pass instead of tracking what it has already done.
    /// </para>
    /// </summary>
    public async Task<int> SweepTaskScheduleEventsAsync(
        int dueSoonDays,
        int staleDays,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.sweep_task_schedule_events($1, $2)");

        command.Parameters.Add(new NpgsqlParameter { Value = dueSoonDays });
        command.Parameters.Add(new NpgsqlParameter { Value = staleDays });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is int raised ? raised : 0;
    }

    /// <summary>
    /// Claims notification emails awaiting send.
    ///
    /// <para>
    /// Returns nothing while no Gmail account is connected, which is what stops
    /// the worker spinning on work it cannot complete. The same gate the rent
    /// dispatcher uses.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PendingDeliveryEmail>> ClaimPendingEmailDeliveriesAsync(
        string workerId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT delivery_id, organization_id, recipient_email::text,
                   title, body, link_path, subject_type, subject_id
            FROM app.claim_pending_email_deliveries($1, $2)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

        var pending = new List<PendingDeliveryEmail>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(new PendingDeliveryEmail
            {
                DeliveryId = reader.GetGuid(0),
                OrganizationId = reader.GetGuid(1),
                RecipientEmail = reader.GetString(2),
                Title = reader.GetString(3),
                Body = reader.GetString(4),
                LinkPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                SubjectType = reader.GetString(6),
                SubjectId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
            });
        }

        return pending;
    }

    public async Task<bool> RecordDeliveryResultAsync(
        Guid deliveryId,
        bool accepted,
        string? providerMessageId,
        string errorCategory,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.record_delivery_result($1, $2, $3, $4::app.email_error_category, $5)");

        command.Parameters.Add(new NpgsqlParameter { Value = deliveryId });
        command.Parameters.Add(new NpgsqlParameter { Value = accepted });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = providerMessageId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = errorCategory });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = errorMessage is null
                ? DBNull.Value
                : errorMessage[..Math.Min(errorMessage.Length, 2000)],
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is bool recorded && recorded;
    }
}

public sealed record OutboxEvent
{
    public required long Id { get; init; }

    public required Guid OrganizationId { get; init; }

    public required string AggregateType { get; init; }

    public required Guid AggregateId { get; init; }

    public required string EventType { get; init; }

    /// <summary>
    /// The snapshot taken at the moment of the event, so a message describes
    /// what happened rather than what is true now.
    /// </summary>
    public required string PayloadJson { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? ActorDisplayName { get; init; }

    public required int AttemptCount { get; init; }

    public JsonDocument ParsePayload() => JsonDocument.Parse(PayloadJson);
}

public sealed record RuleMatch
{
    public required Guid RuleId { get; init; }

    public required string Name { get; init; }

    public required string[] Channels { get; init; }

    public required int MaxSendsPerTaskPerDay { get; init; }
}

public sealed record ResolvedRecipient
{
    public Guid? UserId { get; init; }

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Why this person was chosen, for the "why am I getting this?" line.</summary>
    public required string Role { get; init; }

    public required string Kind { get; init; }
}

public sealed record PendingDeliveryEmail
{
    public required Guid DeliveryId { get; init; }

    public required Guid OrganizationId { get; init; }

    public required string RecipientEmail { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public string? LinkPath { get; init; }

    public required string SubjectType { get; init; }

    public Guid? SubjectId { get; init; }
}
