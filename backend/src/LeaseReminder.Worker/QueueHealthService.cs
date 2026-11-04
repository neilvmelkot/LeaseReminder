using Microsoft.Extensions.Options;

namespace LeaseReminder.Worker;

/// <summary>
/// Logs queue depth, stale locks, and dead-letter counts on an interval.
///
/// <para>
/// Section 4.K asks for alerting on worker downtime and queue backlogs. Both
/// are awkward to detect from outside: a worker that has silently stopped
/// claiming looks identical to a worker with nothing to do, and a growing
/// backlog is invisible until somebody notices reminders stopped arriving.
/// </para>
///
/// <para>
/// A periodic heartbeat carrying the queue numbers solves both. Its absence is
/// the downtime signal, and its contents are the backlog signal, so one log
/// line supports two alerts with no extra infrastructure. The numbers are
/// counts only, never lease or recipient content.
/// </para>
/// </summary>
public sealed partial class QueueHealthService(
    WorkerDatabase database,
    IOptions<WorkerOptions> options,
    ILogger<QueueHealthService> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.HealthIntervalSeconds);

        using var timer = new PeriodicTimer(interval);

        // Report once immediately, so a freshly started worker shows its state
        // without waiting out a full interval.
        await ReportAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReportAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await database
                .ReadQueueHealthAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var queue in health)
            {
                // Escalate rather than logging everything at one level, so an
                // alert rule can key on severity instead of parsing numbers.
                if (queue.DeadLetter > 0)
                {
                    LogDeadLetterPresent(
                        logger, queue.Queue, queue.DeadLetter, queue.DueNow, queue.StaleLocks);
                }
                else if (queue.StaleLocks > 0)
                {
                    // Stale locks mean a worker died holding claims. They are
                    // reclaimed automatically, so this is a warning rather
                    // than an error, but a persistent count means workers are
                    // crashing.
                    LogStaleLocks(logger, queue.Queue, queue.StaleLocks, queue.DueNow);
                }
                else
                {
                    LogHealthy(logger, queue.Queue, queue.DueNow);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogHealthCheckFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information,
        Message = "Queue {Queue}: {DueNow} due now")]
    private static partial void LogHealthy(ILogger logger, string queue, long dueNow);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning,
        Message = "Queue {Queue}: {StaleLocks} expired locks to reclaim, {DueNow} due now. " +
                  "A worker probably stopped mid-job.")]
    private static partial void LogStaleLocks(
        ILogger logger, string queue, long staleLocks, long dueNow);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Error,
        Message = "Queue {Queue}: {DeadLetter} items in dead letter and need attention " +
                  "({DueNow} due now, {StaleLocks} expired locks)")]
    private static partial void LogDeadLetterPresent(
        ILogger logger, string queue, long deadLetter, long dueNow, long staleLocks);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Error,
        Message = "Queue health could not be read")]
    private static partial void LogHealthCheckFailed(ILogger logger, Exception exception);
}
