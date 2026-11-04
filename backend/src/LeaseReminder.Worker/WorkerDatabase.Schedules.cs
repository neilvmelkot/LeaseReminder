using Npgsql;
using NpgsqlTypes;

namespace LeaseReminder.Worker;

/// <summary>
/// A lease whose rent occurrences need generating.
/// </summary>
/// <remarks>
/// Every field the expansion needs, fetched in one claim. The alternative,
/// claiming an id and then reading the schedule, would need a second grant to
/// a role that deliberately has none.
/// </remarks>
public sealed record ClaimedSchedule
{
    public required Guid LeaseId { get; init; }

    public required Guid OrganizationId { get; init; }

    public required Guid RentScheduleId { get; init; }

    public required int GenerationVersion { get; init; }

    public required string Frequency { get; init; }

    public required string DueDayRule { get; init; }

    public int? DueDayOfMonth { get; init; }

    public DateTime[]? CustomDates { get; init; }

    public DateTime? AnchorDate { get; init; }

    public required DateTime StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public required string ShortMonthFallback { get; init; }

    public required string BusinessDayShift { get; init; }

    public Guid? HolidayCalendarId { get; init; }

    public required string TimeZone { get; init; }

    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    public int? GracePeriodDays { get; init; }

    public int? LateFeeAfterDays { get; init; }
}

/// <summary>
/// The rent schedule half of the worker's database surface.
/// </summary>
public sealed partial class WorkerDatabase
{
    /// <summary>
    /// Claims leases whose occurrences are missing or no longer reach the
    /// horizon.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedSchedule>> ClaimSchedulesAsync(
        int batchSize,
        int horizonDays,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT lease_id, organization_id, rent_schedule_id, generation_version,
                   frequency, due_day_rule, due_day_of_month, custom_dates, anchor_date,
                   start_date, end_date, short_month_fallback, business_day_shift,
                   holiday_calendar_id, timezone, amount, currency,
                   grace_period_days, late_fee_after_days
            FROM app.claim_leases_for_schedule_generation($1, $2)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter { Value = horizonDays });

        var claimed = new List<ClaimedSchedule>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new ClaimedSchedule
            {
                LeaseId = reader.GetGuid(0),
                OrganizationId = reader.GetGuid(1),
                RentScheduleId = reader.GetGuid(2),
                GenerationVersion = reader.GetInt32(3),
                Frequency = reader.GetString(4),
                DueDayRule = reader.GetString(5),
                DueDayOfMonth = reader.IsDBNull(6) ? null : reader.GetInt16(6),
                CustomDates = reader.IsDBNull(7)
                    ? null
                    : reader.GetFieldValue<DateTime[]>(7),
                AnchorDate = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                StartDate = reader.GetDateTime(9),
                EndDate = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                ShortMonthFallback = reader.GetString(11),
                BusinessDayShift = reader.GetString(12),
                HolidayCalendarId = reader.IsDBNull(13) ? null : reader.GetGuid(13),
                TimeZone = reader.GetString(14),
                Amount = reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                Currency = reader.IsDBNull(16) ? null : reader.GetString(16),
                GracePeriodDays = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                LateFeeAfterDays = reader.IsDBNull(18) ? null : reader.GetInt32(18),
            });
        }

        return claimed;
    }

    /// <summary>
    /// Reads a holiday calendar's dates.
    /// </summary>
    /// <remarks>
    /// Through a function rather than a SELECT, because the worker holds no
    /// table grants. Returns an empty list when no calendar is configured,
    /// which the expansion treats as "every weekday is a working day".
    /// </remarks>
    public async Task<IReadOnlyList<DateTime>> ReadHolidayDatesAsync(
        Guid? calendarId,
        CancellationToken cancellationToken)
    {
        if (calendarId is null)
        {
            return [];
        }

        await using var command = DataSource.CreateCommand(
            "SELECT holiday_date FROM app.holiday_calendar_dates($1)");

        command.Parameters.Add(new NpgsqlParameter { Value = calendarId.Value });

        var dates = new List<DateTime>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dates.Add(reader.GetDateTime(0));
        }

        return dates;
    }

    /// <summary>
    /// Replaces a schedule's future occurrences in one transaction.
    /// </summary>
    public async Task<int> ReplaceRentOccurrencesAsync(
        Guid rentScheduleId,
        int generationVersion,
        string occurrencesJson,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.replace_rent_occurrences($1, $2, $3)");

        command.Parameters.Add(new NpgsqlParameter { Value = rentScheduleId });
        command.Parameters.Add(new NpgsqlParameter { Value = generationVersion });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = occurrencesJson,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is int written ? written : 0;
    }

    /// <summary>
    /// Cancels pending reminders whose occurrence no longer exists.
    /// </summary>
    /// <remarks>
    /// Run after every regeneration. Without it, moving a lease's due date
    /// from the 1st to the 15th leaves the tenant receiving reminders for
    /// both.
    /// </remarks>
    public async Task<int> CancelOrphanedNotificationsAsync(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.cancel_orphaned_notifications($1)");

        command.Parameters.Add(new NpgsqlParameter { Value = leaseId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is int cancelled ? cancelled : 0;
    }
}
