using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Application.Scheduling;

/// <summary>
/// A rent recurrence rule, expressed so that <see cref="RentScheduleCalculator"/>
/// needs nothing else to expand it. Mirrors the rent_schedules table.
/// </summary>
public sealed record RentScheduleDefinition
{
    public required RentFrequency Frequency { get; init; }

    public required DueDayRule DueDayRule { get; init; }

    /// <summary>Required for <see cref="DueDayRule.SpecificDay"/>; 1 to 31.</summary>
    public int? DueDayOfMonth { get; init; }

    /// <summary>
    /// Required for <see cref="RentFrequency.Custom"/>,
    /// <see cref="RentFrequency.OneTime"/>, and
    /// <see cref="DueDayRule.CustomSequence"/>.
    /// </summary>
    public IReadOnlyList<LocalDate>? CustomDates { get; init; }

    /// <summary>
    /// First occurrence for the interval frequencies (weekly, biweekly, every
    /// 30 days). Those count forward from a real date rather than from a
    /// notion of "the start of the period", which does not exist for them.
    /// </summary>
    public LocalDate? AnchorDate { get; init; }

    public required LocalDate StartDate { get; init; }

    /// <summary>
    /// Last date the schedule covers. <see langword="null"/> means open-ended,
    /// in which case the caller supplies a horizon to generate up to.
    /// </summary>
    public LocalDate? EndDate { get; init; }

    public ShortMonthFallback ShortMonthFallback { get; init; } = ShortMonthFallback.LastDayOfMonth;

    public BusinessDayShift BusinessDayShift { get; init; } = BusinessDayShift.None;

    /// <summary>
    /// Working-day calendar. Required when <see cref="BusinessDayShift"/> is
    /// not <see cref="BusinessDayShift.None"/> or when
    /// <see cref="DueDayRule"/> is <see cref="DueDayRule.FirstBusinessDay"/>.
    /// </summary>
    public WorkingDayCalendar? Calendar { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>
    /// Days after the due date during which payment is still accepted without
    /// penalty. Distinct from <see cref="LateFeeAfterDays"/>.
    /// </summary>
    public int? GracePeriodDays { get; init; }

    /// <summary>
    /// Days after the due date at which a late fee applies. A lease may grant
    /// five grace days yet charge a fee from day three; the two are not the
    /// same date and are never derived from one another (section 6.F).
    /// </summary>
    public int? LateFeeAfterDays { get; init; }
}

/// <summary>A single-occurrence override on a schedule. Mirrors rent_schedule_exceptions.</summary>
public sealed record RentScheduleOverride
{
    /// <summary>The date the rule itself produced, before shifting.</summary>
    public required LocalDate OriginalDueDate { get; init; }

    public required RentScheduleOverrideAction Action { get; init; }

    /// <summary>Required for <see cref="RentScheduleOverrideAction.Move"/>.</summary>
    public LocalDate? ReplacementDate { get; init; }

    /// <summary>Required for <see cref="RentScheduleOverrideAction.OverrideAmount"/>.</summary>
    public decimal? Amount { get; init; }
}

public enum RentScheduleOverrideAction
{
    Skip,
    Move,
    OverrideAmount,
}

/// <summary>One expanded rent payment. Mirrors a rent_occurrences row.</summary>
public sealed record RentOccurrence
{
    public required int SequenceNumber { get; init; }

    /// <summary>
    /// What the recurrence rule produced before any shifting or exception.
    /// Retained so the UI can explain an adjustment rather than just showing a
    /// date the user did not configure.
    /// </summary>
    public required LocalDate NominalDueDate { get; init; }

    /// <summary>The date actually in force.</summary>
    public required LocalDate DueDate { get; init; }

    public LocalDate? GracePeriodEndDate { get; init; }

    public LocalDate? LateFeeDate { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>
    /// Set whenever <see cref="DueDate"/> differs from
    /// <see cref="NominalDueDate"/>. The database enforces the same pairing.
    /// </summary>
    public OccurrenceAdjustment? AdjustmentReason { get; init; }
}

/// <summary>
/// A month the rule could not produce a date for, because the configured day
/// does not exist and the fallback is
/// <see cref="ShortMonthFallback.ManualException"/>.
/// <para>
/// Surfaced rather than silently dropped: the user asked to decide these
/// cases by hand, so the review screen has to be able to tell them which
/// months are waiting on a decision.
/// </para>
/// </summary>
public sealed record UnresolvedMonth
{
    /// <summary>First day of the month that could not be resolved.</summary>
    public required LocalDate MonthStart { get; init; }

    public required int RequestedDayOfMonth { get; init; }

    public required int DaysInMonth { get; init; }
}

/// <summary>The full result of expanding a schedule.</summary>
public sealed record RentScheduleExpansion
{
    public required IReadOnlyList<RentOccurrence> Occurrences { get; init; }

    /// <summary>
    /// Months needing a manual exception. Empty for every fallback other than
    /// <see cref="ShortMonthFallback.ManualException"/>.
    /// </summary>
    public required IReadOnlyList<UnresolvedMonth> UnresolvedMonths { get; init; }

    /// <summary>
    /// True when generation stopped at the horizon rather than at the
    /// schedule's own end date, so the caller knows the list is a window and
    /// not the whole term.
    /// </summary>
    public required bool TruncatedAtHorizon { get; init; }
}
