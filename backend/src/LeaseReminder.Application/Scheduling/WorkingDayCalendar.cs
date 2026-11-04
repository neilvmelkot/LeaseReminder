using NodaTime;

namespace LeaseReminder.Application.Scheduling;

/// <summary>
/// Which days count as working days, for the rules that need it.
/// <para>
/// The application ships no holiday data at all (section 6.F). A calendar is
/// something the user defines: which weekdays are non-working, and which
/// specific dates are holidays. Nothing in the engine assumes a Saturday and
/// Sunday weekend, let alone a national holiday list, because both are wrong
/// somewhere and a wrong assumption here silently moves a rent due date.
/// </para>
/// </summary>
public sealed class WorkingDayCalendar
{
    private readonly HashSet<IsoDayOfWeek> _nonWorkingDays;
    private readonly HashSet<LocalDate> _holidays;

    /// <param name="nonWorkingDays">
    /// ISO weekdays treated as non-working. May be empty, which means every
    /// weekday is a working day.
    /// </param>
    /// <param name="holidays">Specific non-working dates.</param>
    public WorkingDayCalendar(
        IEnumerable<IsoDayOfWeek> nonWorkingDays,
        IEnumerable<LocalDate>? holidays = null)
    {
        ArgumentNullException.ThrowIfNull(nonWorkingDays);

        _nonWorkingDays = [.. nonWorkingDays];
        _holidays = holidays is null ? [] : [.. holidays];

        if (_nonWorkingDays.Count >= 7)
        {
            // Every day non-working would make "next business day" diverge
            // forever. Reject it at construction rather than loop at runtime.
            throw new ArgumentException(
                "At least one weekday must be a working day.", nameof(nonWorkingDays));
        }
    }

    /// <summary>
    /// A calendar in which every day is a working day. The correct choice when
    /// a lease says nothing about business days, and the default the engine
    /// falls back to when no shifting is requested.
    /// </summary>
    public static WorkingDayCalendar AllDaysAreWorkingDays { get; } = new([]);

    /// <summary>
    /// Saturday and Sunday non-working, with no holidays. Offered as a
    /// convenience for tests and for seeding a new organization. It is still
    /// an explicit choice at the call site, not a hidden default.
    /// </summary>
    public static WorkingDayCalendar SaturdaySundayWeekend { get; } =
        new([IsoDayOfWeek.Saturday, IsoDayOfWeek.Sunday]);

    public bool IsWorkingDay(LocalDate date) =>
        !_nonWorkingDays.Contains(date.DayOfWeek) && !_holidays.Contains(date);

    public bool IsHoliday(LocalDate date) => _holidays.Contains(date);

    /// <summary>The first working day at or after <paramref name="date"/>.</summary>
    public LocalDate NextWorkingDayOnOrAfter(LocalDate date)
    {
        var candidate = date;

        // Bounded so a pathological calendar cannot spin. Seven non-working
        // weekdays are rejected in the constructor, so the only way to consume
        // this budget is a long run of holidays.
        for (var i = 0; i < MaxSearchDays; i++)
        {
            if (IsWorkingDay(candidate))
            {
                return candidate;
            }

            candidate = candidate.PlusDays(1);
        }

        throw new InvalidOperationException(
            $"No working day found within {MaxSearchDays} days after {date:yyyy-MM-dd}. " +
            "The calendar marks an implausible run of days as non-working.");
    }

    /// <summary>The last working day at or before <paramref name="date"/>.</summary>
    public LocalDate PreviousWorkingDayOnOrBefore(LocalDate date)
    {
        var candidate = date;

        for (var i = 0; i < MaxSearchDays; i++)
        {
            if (IsWorkingDay(candidate))
            {
                return candidate;
            }

            candidate = candidate.PlusDays(-1);
        }

        throw new InvalidOperationException(
            $"No working day found within {MaxSearchDays} days before {date:yyyy-MM-dd}. " +
            "The calendar marks an implausible run of days as non-working.");
    }

    /// <summary>The first working day of the month containing <paramref name="anyDateInMonth"/>.</summary>
    public LocalDate FirstWorkingDayOfMonth(LocalDate anyDateInMonth)
    {
        var firstOfMonth = new LocalDate(anyDateInMonth.Year, anyDateInMonth.Month, 1);
        var candidate = NextWorkingDayOnOrAfter(firstOfMonth);

        if (candidate.Month != firstOfMonth.Month)
        {
            // Only reachable if an entire month is marked non-working.
            throw new InvalidOperationException(
                $"The calendar contains no working day in {firstOfMonth:yyyy-MM}.");
        }

        return candidate;
    }

    private const int MaxSearchDays = 370;
}
