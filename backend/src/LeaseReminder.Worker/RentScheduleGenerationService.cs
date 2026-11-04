using System.Globalization;
using System.Text.Json.Nodes;

using LeaseReminder.Application.Scheduling;
using LeaseReminder.Domain;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NodaTime;

namespace LeaseReminder.Worker;

/// <summary>
/// Expands approved leases into dated rent occurrences.
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is not here. <see cref="RentScheduleCalculator"/> does it,
/// is pure, and has forty-four tests covering the cases that actually go
/// wrong: the 31st in February, a due date on a bank holiday, a lease that
/// starts mid-period, every-30-days drifting against the calendar. This
/// service is the part that cannot be unit-tested in isolation — claiming
/// work, fetching what the calculator needs, and writing the result
/// atomically.
/// </para>
/// <para>
/// <b>What it refuses to do.</b> It generates only for leases the database has
/// already marked approved, because the claim query filters on that. A rent
/// schedule is what reminders are built from, so generating one for an
/// unapproved lease would be the first step of exactly what section 13.C
/// forbids. That filter lives in SQL rather than here, where a future edit to
/// this file cannot remove it by accident.
/// </para>
/// </remarks>
public sealed partial class RentScheduleGenerationService(
    WorkerDatabase database,
    ILogger<RentScheduleGenerationService> logger) : BackgroundService
{
    /// <summary>
    /// How far ahead to generate.
    /// </summary>
    /// <remarks>
    /// Thirteen months. Far enough that an annual lease is fully expanded on
    /// the day it is approved, and that the yearly reminders have somewhere to
    /// attach; short enough that an open-ended month-to-month tenancy does not
    /// accumulate a decade of rows nobody will read.
    /// </remarks>
    private const int HorizonDays = 400;

    private const int BatchSize = 10;

    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.LogStarted(HorizonDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;

            try
            {
                var claimed = await database
                    .ClaimSchedulesAsync(BatchSize, HorizonDays, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var schedule in claimed)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await this.GenerateAsync(schedule, stoppingToken).ConfigureAwait(false);
                    processed++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One bad lease must not stop the others. The claim is not
                // committed on failure, so it will be retried on the next
                // pass rather than silently skipped.
                this.LogBatchFailed(exception.GetType().Name, exception.Message);
            }

            try
            {
                await Task
                    .Delay(processed > 0 ? BusyInterval : IdleInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task GenerateAsync(ClaimedSchedule schedule, CancellationToken cancellationToken)
    {
        if (schedule.Amount is not { } amount || string.IsNullOrWhiteSpace(schedule.Currency))
        {
            // A schedule with no amount cannot produce an occurrence, and the
            // occurrence table requires one. Nothing to do; the lease stays
            // claimable until somebody supplies it.
            this.LogIncomplete(schedule.LeaseId);

            return;
        }

        var definition = new RentScheduleDefinition
        {
            Frequency = ParseFrequency(schedule.Frequency),
            DueDayRule = ParseDueDayRule(schedule.DueDayRule),
            DueDayOfMonth = schedule.DueDayOfMonth,
            CustomDates = schedule.CustomDates?.Select(ToLocalDate).ToArray(),
            AnchorDate = schedule.AnchorDate is { } anchor ? ToLocalDate(anchor) : null,
            StartDate = ToLocalDate(schedule.StartDate),
            EndDate = schedule.EndDate is { } end ? ToLocalDate(end) : null,
            ShortMonthFallback = ParseShortMonthFallback(schedule.ShortMonthFallback),
            BusinessDayShift = ParseBusinessDayShift(schedule.BusinessDayShift),
            Calendar = await this.BuildCalendarAsync(schedule, cancellationToken)
                .ConfigureAwait(false),
            Amount = amount,
            Currency = schedule.Currency!,
            GracePeriodDays = schedule.GracePeriodDays,
            LateFeeAfterDays = schedule.LateFeeAfterDays,
        };

        // The horizon is a calendar date in the property's own zone, not a UTC
        // one. On a UTC server late in the day these differ, and generating to
        // the wrong one produces a schedule that is a day short for every
        // property west of Greenwich.
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(schedule.TimeZone)
            ?? DateTimeZoneProviders.Tzdb["UTC"];

        var today = SystemClock.Instance.GetCurrentInstant().InZone(zone).Date;
        var horizon = today.PlusDays(HorizonDays);

        var expansion = RentScheduleCalculator.Expand(definition, horizon);

        var written = await database
            .ReplaceRentOccurrencesAsync(
                schedule.RentScheduleId,
                schedule.GenerationVersion,
                Serialise(expansion),
                cancellationToken)
            .ConfigureAwait(false);

        // Reminders attached to occurrences that no longer exist. Without
        // this, moving a lease's due date from the 1st to the 15th leaves the
        // tenant receiving both.
        var cancelled = await database
            .CancelOrphanedNotificationsAsync(schedule.LeaseId, cancellationToken)
            .ConfigureAwait(false);

        this.LogGenerated(schedule.LeaseId, written, cancelled);

        // A month the calculator could not resolve is a real ambiguity in the
        // lease, not a bug: "the 31st" in a thirty-day month has no correct
        // answer, only a chosen one. The fallback already applied; logging it
        // means an operator can see which leases are relying on it.
        if (expansion.UnresolvedMonths.Count > 0)
        {
            this.LogUnresolvedMonths(schedule.LeaseId, expansion.UnresolvedMonths.Count);
        }

        if (expansion.TruncatedAtHorizon)
        {
            this.LogTruncated(schedule.LeaseId, HorizonDays);
        }
    }

    /// <summary>
    /// Builds the working-day calendar the expansion shifts against.
    /// </summary>
    private async Task<WorkingDayCalendar?> BuildCalendarAsync(
        ClaimedSchedule schedule,
        CancellationToken cancellationToken)
    {
        // No shifting requested means no calendar is consulted at all, so
        // fetching holidays would be wasted work.
        if (string.Equals(schedule.BusinessDayShift, "none", StringComparison.Ordinal))
        {
            return null;
        }

        var holidays = await database
            .ReadHolidayDatesAsync(schedule.HolidayCalendarId, cancellationToken)
            .ConfigureAwait(false);

        // Saturday and Sunday are the weekend unless a calendar says
        // otherwise. A configurable working week is a real requirement in some
        // markets and is not modelled yet; this is the assumption, stated
        // rather than hidden.
        return new WorkingDayCalendar(
            [IsoDayOfWeek.Saturday, IsoDayOfWeek.Sunday],
            holidays.Select(ToLocalDate));
    }

    /// <summary>
    /// Shapes occurrences for <c>app.replace_rent_occurrences</c>.
    /// </summary>
    private static string Serialise(RentScheduleExpansion expansion)
    {
        var array = new JsonArray();

        foreach (var occurrence in expansion.Occurrences)
        {
            array.Add(new JsonObject
            {
                ["sequence_number"] = occurrence.SequenceNumber,
                ["nominal_due_date"] = Iso(occurrence.NominalDueDate),
                ["due_date"] = Iso(occurrence.DueDate),
                ["grace_period_end_date"] = occurrence.GracePeriodEndDate is { } grace
                    ? Iso(grace)
                    : null,
                ["late_fee_date"] = occurrence.LateFeeDate is { } late ? Iso(late) : null,
                ["amount"] = occurrence.Amount,
                ["currency"] = occurrence.Currency,
                ["adjustment_reason"] = occurrence.AdjustmentReason?.ToString(),
            });
        }

        return array.ToJsonString();
    }

    private static string Iso(LocalDate date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LocalDate ToLocalDate(DateTime value) =>
        new(value.Year, value.Month, value.Day);

    // -----------------------------------------------------------------------
    // Enum parsing.
    //
    // The database stores these as its own enum types and the claim returns
    // them as text. Unknown values fall back rather than throwing, because a
    // value the database accepted is by definition valid and a mismatch here
    // means this code is behind the schema, which should degrade rather than
    // stop every lease in the batch.
    // -----------------------------------------------------------------------

    private static RentFrequency ParseFrequency(string value) => value switch
    {
        "monthly" => RentFrequency.Monthly,
        "weekly" => RentFrequency.Weekly,
        "biweekly" => RentFrequency.Biweekly,
        "every_30_days" => RentFrequency.Every30Days,
        "one_time" => RentFrequency.OneTime,
        "custom" => RentFrequency.Custom,
        _ => RentFrequency.Monthly,
    };

    private static DueDayRule ParseDueDayRule(string value) => value switch
    {
        "first_of_month" => DueDayRule.FirstOfMonth,
        "last_of_month" => DueDayRule.LastOfMonth,
        "specific_day" => DueDayRule.SpecificDay,
        "first_business_day" => DueDayRule.FirstBusinessDay,
        "custom_sequence" => DueDayRule.CustomSequence,
        _ => DueDayRule.FirstOfMonth,
    };

    private static ShortMonthFallback ParseShortMonthFallback(string value) => value switch
    {
        "last_day_of_month" => ShortMonthFallback.LastDayOfMonth,
        "skip_month" => ShortMonthFallback.SkipMonth,
        "manual_exception" => ShortMonthFallback.ManualException,
        _ => ShortMonthFallback.LastDayOfMonth,
    };

    private static BusinessDayShift ParseBusinessDayShift(string value) => value switch
    {
        "none" => BusinessDayShift.None,
        "previous_business_day" => BusinessDayShift.PreviousBusinessDay,
        "next_business_day" => BusinessDayShift.NextBusinessDay,
        _ => BusinessDayShift.None,
    };

    // -----------------------------------------------------------------------
    // Logging. Identifiers and counts, never an amount or a tenant.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 3500,
        Level = LogLevel.Information,
        Message = "Rent schedule generation started, generating {HorizonDays} days ahead.")]
    private partial void LogStarted(int horizonDays);

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Information,
        Message = "Generated {Written} rent occurrence(s) for lease {LeaseId}; "
                  + "{Cancelled} orphaned reminder(s) cancelled.")]
    private partial void LogGenerated(Guid leaseId, int written, int cancelled);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Warning,
        Message = "Lease {LeaseId} has a rent schedule with no amount and was skipped.")]
    private partial void LogIncomplete(Guid leaseId);

    [LoggerMessage(
        EventId = 3503,
        Level = LogLevel.Information,
        Message = "Lease {LeaseId} has {Count} month(s) whose due day does not exist; "
                  + "the configured fallback was applied.")]
    private partial void LogUnresolvedMonths(Guid leaseId, int count);

    [LoggerMessage(
        EventId = 3504,
        Level = LogLevel.Debug,
        Message = "Lease {LeaseId} was truncated at the {HorizonDays}-day horizon.")]
    private partial void LogTruncated(Guid leaseId, int horizonDays);

    [LoggerMessage(
        EventId = 3505,
        Level = LogLevel.Error,
        Message = "A rent schedule generation batch failed ({ExceptionType}): {Reason}")]
    private partial void LogBatchFailed(string exceptionType, string reason);
}
