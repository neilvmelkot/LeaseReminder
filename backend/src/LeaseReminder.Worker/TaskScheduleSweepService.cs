using Microsoft.Extensions.Options;

namespace LeaseReminder.Worker;

/// <summary>
/// Raises the time-based task events: due soon, due today, overdue, and stale.
///
/// <para>
/// This is the half of the engine nothing else can produce. An event-driven
/// notification has a trigger, something a person did; a deadline has no
/// trigger at all. Nobody does anything on the day a task becomes overdue,
/// which is precisely the problem, so something has to go looking.
/// </para>
///
/// <h3>Why the sweep is unconditional</h3>
///
/// The service simply calls the sweep on an interval and does not track what
/// it has already raised. It can do that because every event the sweep
/// produces carries an idempotency key containing the task's LOCAL date, so
/// running it five times in a day produces one overdue event rather than five.
///
/// That one property removes a whole category of state: no "last swept at"
/// column to keep correct across restarts, no catch-up logic after downtime,
/// and no double-notification after a redeploy. A worker that was down for six
/// hours sweeps once on startup and is immediately correct.
///
/// <h3>Why it runs hourly rather than daily</h3>
///
/// "Local date" means the property's own time zone, and a portfolio can span
/// many. A single daily run at a fixed UTC hour would fire on the wrong local
/// day for roughly half of them. Sweeping hourly means each property's
/// midnight is crossed within the hour, and the date-keyed idempotency makes
/// the extra runs free.
/// </summary>
public sealed partial class TaskScheduleSweepService(
    WorkerDatabase database,
    IOptions<WorkerOptions> options,
    ILogger<TaskScheduleSweepService> logger)
    : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.ScheduleSweepIntervalMinutes);

        LogStarting(logger, _options.ScheduleSweepIntervalMinutes);

        using var timer = new PeriodicTimer(interval);

        // Sweep once immediately. A worker that has just restarted may have
        // missed a local midnight, and waiting a full interval would delay
        // every overdue notice by that much.
        await SweepAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }

        LogStopping(logger);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raised = await database
                .SweepTaskScheduleEventsAsync(
                    _options.DueSoonDays,
                    _options.StaleTaskDays,
                    cancellationToken)
                .ConfigureAwait(false);

            // Only logged when something was actually raised. An hourly "0
            // events" line would bury the one that matters, and the queue
            // health heartbeat already proves the worker is alive.
            if (raised > 0)
            {
                LogRaised(logger, raised);
            }
            else
            {
                LogQuiet(logger);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed sweep is not fatal: the next one re-raises everything
            // still outstanding, because the events are date-keyed rather than
            // incremental.
            LogSweepFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information,
        Message = "Task schedule sweep started, running every {IntervalMinutes} minutes")]
    private static partial void LogStarting(ILogger logger, int intervalMinutes);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information,
        Message = "Task schedule sweep stopped cleanly")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Information,
        Message = "Task schedule sweep raised {EventCount} due, overdue, or stale events")]
    private static partial void LogRaised(ILogger logger, int eventCount);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Debug,
        Message = "Task schedule sweep found nothing new")]
    private static partial void LogQuiet(ILogger logger);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Error,
        Message = "Task schedule sweep failed; the next run re-raises anything outstanding")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
