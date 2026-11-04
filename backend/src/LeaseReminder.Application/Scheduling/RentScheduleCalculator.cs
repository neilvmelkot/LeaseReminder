using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Application.Scheduling;

/// <summary>
/// Expands a rent recurrence rule into concrete dated occurrences.
///
/// <para>
/// This type is deliberately pure: no clock, no database, no time zone
/// conversion. It works entirely in <see cref="LocalDate"/>, because a rent
/// due date is a calendar date in the property's own zone, not an instant.
/// Turning a date into a send instant is a separate concern and lives in
/// NotificationInstantCalculator. Keeping the two apart is what makes both
/// testable.
/// </para>
///
/// <para>The order of operations matters and is fixed:</para>
/// <list type="number">
///   <item>Produce the nominal date from the frequency and day rule.</item>
///   <item>Apply the short-month fallback, if the configured day is absent.</item>
///   <item>Apply a user exception (skip, move, or change the amount).</item>
///   <item>Apply weekend or holiday shifting, unless the user moved the date
///         by hand, in which case their date is honoured exactly.</item>
///   <item>Derive the grace-period end and late-fee dates from the effective
///         due date.</item>
/// </list>
///
/// <para>
/// Grace-period and late-fee dates are NOT business-day shifted. They are
/// deadlines the lease expresses as a number of days, and moving them would
/// change a contractual term rather than a convenience. The due date is
/// shifted because that is the date on which somebody has to act.
/// </para>
/// </summary>
public static class RentScheduleCalculator
{
    /// <summary>
    /// Hard ceiling on generated occurrences, so an open-ended weekly schedule
    /// with a distant horizon cannot produce an unbounded list.
    /// </summary>
    public const int MaxOccurrences = 600;

    /// <summary>
    /// Expands <paramref name="definition"/> up to and including
    /// <paramref name="horizon"/>.
    /// </summary>
    /// <param name="definition">The recurrence rule.</param>
    /// <param name="horizon">
    /// Last date to generate. Required rather than optional because a schedule
    /// may be open-ended; callers typically pass "today plus two years".
    /// </param>
    /// <param name="exceptions">Single-occurrence overrides, if any.</param>
    public static RentScheduleExpansion Expand(
        RentScheduleDefinition definition,
        LocalDate horizon,
        IEnumerable<RentScheduleOverride>? exceptions = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);

        // The schedule's own end date wins over the horizon when it is earlier.
        var effectiveEnd = definition.EndDate.HasValue && definition.EndDate.Value < horizon
            ? definition.EndDate.Value
            : horizon;

        var exceptionsByDate = BuildExceptionIndex(exceptions);
        var occurrences = new List<RentOccurrence>();
        var unresolved = new List<UnresolvedMonth>();
        var hitCeiling = false;
        var sequence = 0;

        foreach (var nominal in EnumerateNominalDates(definition, effectiveEnd, unresolved))
        {
            if (nominal < definition.StartDate || nominal > effectiveEnd)
            {
                continue;
            }

            if (occurrences.Count >= MaxOccurrences)
            {
                hitCeiling = true;
                break;
            }

            var occurrence = BuildOccurrence(definition, nominal, exceptionsByDate, sequence + 1);

            if (occurrence is null)
            {
                // A skip exception consumed this date without consuming a
                // sequence number, so numbering stays contiguous and the UI
                // can say "payment 4 of 12" meaningfully.
                continue;
            }

            sequence++;
            occurrences.Add(occurrence);
        }

        // The list is a window rather than the whole term whenever the
        // schedule outlives the horizon we were asked for.
        var scheduleOutlivesHorizon =
            definition.EndDate is null || definition.EndDate.Value > horizon;

        return new RentScheduleExpansion
        {
            Occurrences = occurrences,
            UnresolvedMonths = unresolved,
            TruncatedAtHorizon = hitCeiling || scheduleOutlivesHorizon,
        };
    }

    // -------------------------------------------------------------------------
    // Nominal date generation
    // -------------------------------------------------------------------------

    private static IEnumerable<LocalDate> EnumerateNominalDates(
        RentScheduleDefinition definition,
        LocalDate effectiveEnd,
        List<UnresolvedMonth> unresolved)
    {
        return definition.Frequency switch
        {
            RentFrequency.Monthly => EnumerateMonthly(definition, effectiveEnd, unresolved),
            RentFrequency.Weekly => EnumerateInterval(definition, effectiveEnd, days: 7),
            RentFrequency.Biweekly => EnumerateInterval(definition, effectiveEnd, days: 14),
            RentFrequency.Every30Days => EnumerateInterval(definition, effectiveEnd, days: 30),
            RentFrequency.OneTime or RentFrequency.Custom => EnumerateCustom(definition),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition),
                definition.Frequency,
                "Unsupported rent frequency."),
        };
    }

    private static IEnumerable<LocalDate> EnumerateMonthly(
        RentScheduleDefinition definition,
        LocalDate effectiveEnd,
        List<UnresolvedMonth> unresolved)
    {
        // A custom sequence may be attached to a monthly schedule, in which
        // case the explicit dates ARE the schedule.
        if (definition.DueDayRule == DueDayRule.CustomSequence)
        {
            foreach (var date in EnumerateCustom(definition))
            {
                yield return date;
            }

            yield break;
        }

        // Iterate on the first of each month. Using LocalDate arithmetic keeps
        // every month-length question (February, leap years) in one place:
        // DaysInMonth below.
        var cursor = new LocalDate(definition.StartDate.Year, definition.StartDate.Month, 1);
        var lastMonth = new LocalDate(effectiveEnd.Year, effectiveEnd.Month, 1);

        while (cursor <= lastMonth)
        {
            var date = ResolveMonthlyDate(definition, cursor, unresolved);

            if (date.HasValue)
            {
                yield return date.Value;
            }

            cursor = cursor.PlusMonths(1);
        }
    }

    /// <summary>
    /// Resolves the nominal date within the month beginning at
    /// <paramref name="monthStart"/>. Returns <see langword="null"/> when the
    /// month produces no occurrence at all.
    /// </summary>
    private static LocalDate? ResolveMonthlyDate(
        RentScheduleDefinition definition,
        LocalDate monthStart,
        List<UnresolvedMonth> unresolved)
    {
        var daysInMonth = DaysInMonth(monthStart);

        switch (definition.DueDayRule)
        {
            case DueDayRule.FirstOfMonth:
                return monthStart;

            case DueDayRule.LastOfMonth:
                // Always the real last day: 28, 29, 30, or 31 as appropriate,
                // so a leap year needs no special case.
                return monthStart.PlusDays(daysInMonth - 1);

            case DueDayRule.FirstBusinessDay:
                // Validate() guarantees a calendar for this rule.
                return definition.Calendar!.FirstWorkingDayOfMonth(monthStart);

            case DueDayRule.SpecificDay:
                var requestedDay = definition.DueDayOfMonth!.Value;

                if (requestedDay <= daysInMonth)
                {
                    return monthStart.PlusDays(requestedDay - 1);
                }

                // The configured day does not exist this month, for example
                // the 31st in February. What happens next is the user's
                // explicit choice, never a guess.
                return definition.ShortMonthFallback switch
                {
                    ShortMonthFallback.LastDayOfMonth =>
                        monthStart.PlusDays(daysInMonth - 1),

                    ShortMonthFallback.SkipMonth => null,

                    ShortMonthFallback.ManualException =>
                        RecordUnresolved(unresolved, monthStart, requestedDay, daysInMonth),

                    _ => throw new ArgumentOutOfRangeException(
                        nameof(definition),
                        definition.ShortMonthFallback,
                        "Unsupported short-month fallback."),
                };

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.DueDayRule,
                    "Unsupported monthly due day rule.");
        }
    }

    private static LocalDate? RecordUnresolved(
        List<UnresolvedMonth> unresolved,
        LocalDate monthStart,
        int requestedDay,
        int daysInMonth)
    {
        unresolved.Add(new UnresolvedMonth
        {
            MonthStart = monthStart,
            RequestedDayOfMonth = requestedDay,
            DaysInMonth = daysInMonth,
        });

        return null;
    }

    private static IEnumerable<LocalDate> EnumerateInterval(
        RentScheduleDefinition definition,
        LocalDate effectiveEnd,
        int days)
    {
        // Validate() guarantees an anchor for the interval frequencies.
        var anchor = definition.AnchorDate!.Value;
        var date = anchor;

        // Jump straight to the window instead of stepping through every
        // interval since the anchor, which may be years in the past.
        if (date < definition.StartDate)
        {
            var elapsed = Period.Between(anchor, definition.StartDate, PeriodUnits.Days).Days;
            date = anchor.PlusDays(elapsed / days * days);

            // Land on or after the start even when the division truncated.
            while (date < definition.StartDate)
            {
                date = date.PlusDays(days);
            }
        }

        while (date <= effectiveEnd)
        {
            yield return date;
            date = date.PlusDays(days);
        }
    }

    private static IEnumerable<LocalDate> EnumerateCustom(RentScheduleDefinition definition)
    {
        // Validate() guarantees custom dates for the frequencies that need them.
        return definition.CustomDates!.Distinct().OrderBy(date => date);
    }

    /// <summary>
    /// Days in the month beginning at <paramref name="monthStart"/>. Derived by
    /// date arithmetic so February and leap years need no table or branch.
    /// </summary>
    private static int DaysInMonth(LocalDate monthStart) =>
        Period.Between(monthStart, monthStart.PlusMonths(1), PeriodUnits.Days).Days;

    // -------------------------------------------------------------------------
    // Occurrence construction
    // -------------------------------------------------------------------------

    private static RentOccurrence? BuildOccurrence(
        RentScheduleDefinition definition,
        LocalDate nominal,
        Dictionary<LocalDate, RentScheduleOverride> exceptions,
        int sequenceNumber)
    {
        var dueDate = nominal;
        var amount = definition.Amount;
        OccurrenceAdjustment? adjustment = null;

        // The nominal date already reflects a short-month fallback (the
        // generator clamped it), so record that before anything else can
        // overwrite the reason.
        if (definition.DueDayRule == DueDayRule.SpecificDay
            && definition.DueDayOfMonth!.Value != nominal.Day)
        {
            adjustment = OccurrenceAdjustment.ShortMonthFallback;
        }

        var userMovedTheDate = false;

        // Exceptions are keyed on the nominal date, which is the date the user
        // was looking at in the preview when they created the exception.
        if (exceptions.TryGetValue(nominal, out var exception))
        {
            switch (exception.Action)
            {
                case RentScheduleOverrideAction.Skip:
                    return null;

                case RentScheduleOverrideAction.Move:
                    dueDate = exception.ReplacementDate!.Value;
                    adjustment = OccurrenceAdjustment.UserException;
                    userMovedTheDate = true;
                    break;

                case RentScheduleOverrideAction.OverrideAmount:
                    amount = exception.Amount!.Value;
                    adjustment ??= OccurrenceAdjustment.UserException;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(exceptions), exception.Action, "Unsupported exception action.");
            }
        }

        // A date the user set by hand is final. Shifting it would override an
        // explicit instruction with a general rule.
        if (!userMovedTheDate && definition.BusinessDayShift != BusinessDayShift.None)
        {
            var calendar = definition.Calendar!;

            if (!calendar.IsWorkingDay(dueDate))
            {
                // Distinguish a holiday from a weekend so the UI can say which.
                adjustment = calendar.IsHoliday(dueDate)
                    ? OccurrenceAdjustment.HolidayShift
                    : OccurrenceAdjustment.WeekendShift;

                dueDate = definition.BusinessDayShift switch
                {
                    BusinessDayShift.NextBusinessDay =>
                        calendar.NextWorkingDayOnOrAfter(dueDate),
                    BusinessDayShift.PreviousBusinessDay =>
                        calendar.PreviousWorkingDayOnOrBefore(dueDate),
                    _ => dueDate,
                };
            }
        }

        return new RentOccurrence
        {
            SequenceNumber = sequenceNumber,
            NominalDueDate = nominal,
            DueDate = dueDate,

            // Both derived from the EFFECTIVE due date, so a shifted due date
            // carries its grace period with it, and the two stay independent
            // of one another.
            GracePeriodEndDate = definition.GracePeriodDays is { } grace
                ? dueDate.PlusDays(grace)
                : null,
            LateFeeDate = definition.LateFeeAfterDays is { } lateFee
                ? dueDate.PlusDays(lateFee)
                : null,

            Amount = amount,
            Currency = definition.Currency,

            // Whenever the effective date differs from the nominal one, a
            // reason is always set above, which is exactly what the
            // rent_occurrences CHECK constraint requires.
            AdjustmentReason = adjustment,
        };
    }

    private static Dictionary<LocalDate, RentScheduleOverride> BuildExceptionIndex(
        IEnumerable<RentScheduleOverride>? exceptions)
    {
        if (exceptions is null)
        {
            return [];
        }

        var index = new Dictionary<LocalDate, RentScheduleOverride>();

        foreach (var exception in exceptions)
        {
            if (exception.Action == RentScheduleOverrideAction.Move && exception.ReplacementDate is null)
            {
                throw new ArgumentException(
                    $"The move exception for {exception.OriginalDueDate:yyyy-MM-dd} " +
                    "has no replacement date.",
                    nameof(exceptions));
            }

            if (exception.Action == RentScheduleOverrideAction.OverrideAmount && exception.Amount is null)
            {
                throw new ArgumentException(
                    $"The amount exception for {exception.OriginalDueDate:yyyy-MM-dd} " +
                    "has no amount.",
                    nameof(exceptions));
            }

            if (!index.TryAdd(exception.OriginalDueDate, exception))
            {
                throw new ArgumentException(
                    $"More than one exception targets {exception.OriginalDueDate:yyyy-MM-dd}. " +
                    "The database enforces one exception per original due date.",
                    nameof(exceptions));
            }
        }

        return index;
    }

    // -------------------------------------------------------------------------
    // Validation
    //
    // These mirror the CHECK constraints on rent_schedules. Checking here too
    // means a malformed definition fails with a message naming the field,
    // rather than as a constraint violation at save time.
    // -------------------------------------------------------------------------

    private static void Validate(RentScheduleDefinition definition)
    {
        if (definition.EndDate.HasValue && definition.EndDate.Value < definition.StartDate)
        {
            throw new ArgumentException(
                "The schedule end date precedes its start date.", nameof(definition));
        }

        if (definition.Amount < 0)
        {
            throw new ArgumentException("Rent cannot be negative.", nameof(definition));
        }

        if (definition.Currency is null || definition.Currency.Length != 3)
        {
            throw new ArgumentException(
                "Currency must be a three-letter code.", nameof(definition));
        }

        if (definition.GracePeriodDays is < 0)
        {
            throw new ArgumentException(
                "A grace period cannot be negative.", nameof(definition));
        }

        if (definition.LateFeeAfterDays is < 0)
        {
            throw new ArgumentException(
                "A late fee cannot apply before the due date.", nameof(definition));
        }

        if (definition.DueDayRule == DueDayRule.SpecificDay)
        {
            if (definition.DueDayOfMonth is not (>= 1 and <= 31))
            {
                throw new ArgumentException(
                    "A specific-day rule needs a due day between 1 and 31.", nameof(definition));
            }
        }
        else if (definition.DueDayOfMonth.HasValue)
        {
            throw new ArgumentException(
                $"A due day of month is meaningless for the {definition.DueDayRule} rule.",
                nameof(definition));
        }

        var needsCustomDates =
            definition.Frequency is RentFrequency.Custom or RentFrequency.OneTime
            || definition.DueDayRule == DueDayRule.CustomSequence;

        if (needsCustomDates && definition.CustomDates is null or { Count: 0 })
        {
            throw new ArgumentException(
                "A custom or one-time schedule needs at least one explicit date.",
                nameof(definition));
        }

        if (definition.Frequency == RentFrequency.OneTime
            && definition.CustomDates is { Count: > 1 })
        {
            throw new ArgumentException(
                "A one-time payment has exactly one date.", nameof(definition));
        }

        if (definition.Frequency is RentFrequency.Weekly
                or RentFrequency.Biweekly
                or RentFrequency.Every30Days
            && definition.AnchorDate is null)
        {
            throw new ArgumentException(
                $"The {definition.Frequency} frequency needs an anchor date to count from.",
                nameof(definition));
        }

        // Shifting by business day without a calendar has no defined meaning,
        // and inventing one would move rent due dates.
        if (definition.BusinessDayShift != BusinessDayShift.None && definition.Calendar is null)
        {
            throw new ArgumentException(
                "Business-day shifting requires a working-day calendar. " +
                "The application ships no holiday data, so one must be supplied.",
                nameof(definition));
        }

        if (definition.DueDayRule == DueDayRule.FirstBusinessDay && definition.Calendar is null)
        {
            throw new ArgumentException(
                "The first-business-day rule requires a working-day calendar.",
                nameof(definition));
        }
    }
}
