using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace LeaseReminder.Worker;

/// <summary>
/// The worker's entire database surface.
///
/// <para>
/// Every method here calls one of the SECURITY DEFINER functions in
/// db/migrations/0007. The worker role holds no table privileges at all, not
/// even SELECT, which is verified by an assertion in db/migrations/0010 that
/// fails the migration if a table grant is ever added. So this class is not a
/// convenience wrapper that could be bypassed with a hand-written query;
/// it is the complete list of things the worker is able to do.
/// </para>
///
/// <para>
/// There is deliberately no EF Core here. A DbContext would invite someone to
/// add a query, that query would fail with a permission error, and the obvious
/// fix would be to grant the worker a table. Having no entity model at all
/// makes the narrow path the only path.
/// </para>
/// </summary>
public sealed partial class WorkerDatabase(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Exposed so the maintenance half of this class, in
    /// WorkerDatabase.Maintenance.cs, can reach the same data source. A
    /// primary constructor parameter is in scope across partial declarations,
    /// but naming it explicitly makes the dependency obvious in both files.
    /// </summary>
    private NpgsqlDataSource DataSource => dataSource;

    /// <summary>
    /// Claims a batch of due notifications.
    ///
    /// <para>
    /// The function re-checks every send precondition at claim time: lease
    /// approval, lease automation state, rule enabled, and Gmail connection
    /// status. A lease approved last week and paused this morning yields
    /// nothing, and so does a revoked Gmail account. That re-check is why the
    /// worker can be simple: it does not have to reason about whether a send
    /// is still legitimate.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ClaimedNotification>> ClaimDueNotificationsAsync(
        string workerId,
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, organization_id, attempt_count, max_attempts, notification_rule_id " +
            "FROM app.claim_due_notifications($1, $2, $3)");

        command.Parameters.Add(new NpgsqlParameter { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (int)lockDuration.TotalSeconds,
        });

        var claimed = new List<ClaimedNotification>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new ClaimedNotification
            {
                Id = reader.GetGuid(0),
                OrganizationId = reader.GetGuid(1),
                AttemptCount = reader.GetInt32(2),
                MaxAttempts = reader.GetInt32(3),
                NotificationRuleId = reader.GetGuid(4),
            });
        }

        return claimed;
    }

    /// <summary>
    /// Everything needed to render and send one claimed notification.
    /// Returns null when the claim is no longer held, which the caller treats
    /// as "skip" rather than "send anyway".
    /// </summary>
    public async Task<JsonDocument?> GetNotificationContextAsync(
        Guid notificationId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT app.get_notification_context($1, $2)");

        command.Parameters.Add(new NpgsqlParameter { Value = notificationId });
        command.Parameters.Add(new NpgsqlParameter { Value = workerId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? null
            : JsonDocument.Parse((string)result);
    }

    /// <summary>
    /// Removes suppressed addresses, re-checked immediately before sending
    /// rather than trusting the snapshot taken at generation time (4.G).
    /// </summary>
    public async Task<JsonDocument> FilterSuppressedRecipientsAsync(
        Guid organizationId,
        string recipientsJson,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT app.filter_suppressed_recipients($1, $2)");

        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = recipientsJson,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return JsonDocument.Parse(result is null or DBNull ? "[]" : (string)result);
    }

    /// <summary>
    /// Records the outcome and writes the email log, in one transaction, AFTER
    /// the provider call has returned. The provider call itself happens outside
    /// every database lock (section 7).
    /// </summary>
    public async Task<string?> RecordResultAsync(
        Guid notificationId,
        string workerId,
        bool accepted,
        string? providerMessageId,
        string errorCategory,
        string? errorMessage,
        bool permanent,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT app.record_notification_result($1, $2, $3, $4, $5::app.email_error_category, $6, $7)");

        command.Parameters.Add(new NpgsqlParameter { Value = notificationId });
        command.Parameters.Add(new NpgsqlParameter { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter { Value = accepted });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = providerMessageId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = errorCategory });
        command.Parameters.Add(new NpgsqlParameter
        {
            // Truncated here as well as in SQL. The message can originate from
            // a provider response, and an operator-safe log entry must not
            // become a dumping ground for one.
            Value = errorMessage is null
                ? DBNull.Value
                : errorMessage[..Math.Min(errorMessage.Length, 2000)],
        });
        command.Parameters.Add(new NpgsqlParameter { Value = permanent });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? null : result.ToString();
    }

    /// <summary>
    /// Auto-pauses a rule after repeated failures and cancels its pending
    /// sends (section 4.G). Returns true when it actually paused something.
    /// </summary>
    public async Task<bool> PauseRuleOnRepeatedFailureAsync(
        Guid notificationRuleId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT app.pause_rule_on_repeated_failure($1)");

        command.Parameters.Add(new NpgsqlParameter { Value = notificationRuleId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is bool paused && paused;
    }

    /// <summary>
    /// Per-account daily send ceiling. Checked by the worker as well as by the
    /// API, because the worker is where volume actually accumulates.
    /// </summary>
    public async Task<bool> TryConsumeSendQuotaAsync(
        Guid emailAccountId,
        int dailyLimit,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT allowed FROM app.consume_rate_limit($1, $2, $3, 1)");

        command.Parameters.Add(new NpgsqlParameter
        {
            Value = string.Create(
                CultureInfo.InvariantCulture,
                $"email_send:account:{emailAccountId:N}"),
        });
        command.Parameters.Add(new NpgsqlParameter { Value = 86_400 });
        command.Parameters.Add(new NpgsqlParameter { Value = dailyLimit });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is bool allowed && allowed;
    }

    /// <summary>Queue depth and stale locks, for the heartbeat log and alerting.</summary>
    public async Task<IReadOnlyList<QueueHealth>> ReadQueueHealthAsync(
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT queue, due_now, stale_locks, dead_letter FROM app.queue_health");

        var health = new List<QueueHealth>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            health.Add(new QueueHealth
            {
                Queue = reader.GetString(0),
                DueNow = reader.GetInt64(1),
                StaleLocks = reader.GetInt64(2),
                DeadLetter = reader.GetInt64(3),
            });
        }

        return health;
    }
}

public sealed record ClaimedNotification
{
    public required Guid Id { get; init; }

    public required Guid OrganizationId { get; init; }

    public required int AttemptCount { get; init; }

    public required int MaxAttempts { get; init; }

    public required Guid NotificationRuleId { get; init; }
}

public sealed record QueueHealth
{
    public required string Queue { get; init; }

    public required long DueNow { get; init; }

    public required long StaleLocks { get; init; }

    public required long DeadLetter { get; init; }
}
