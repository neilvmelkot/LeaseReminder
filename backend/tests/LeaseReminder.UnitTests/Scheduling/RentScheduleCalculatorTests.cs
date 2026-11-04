using LeaseReminder.Application.Scheduling;
using LeaseReminder.Domain;
using NodaTime;
using Xunit;

namespace LeaseReminder.UnitTests.Scheduling;

/// <summary>
/// Covers the unit test list in section 9: monthly rent date calculations,
/// last-day-of-month rules, leap years, and business-day rules.
///
/// Every date here is written out in full rather than computed, so a test
/// failure points at a wrong date rather than at a second implementation of
/// the same arithmetic.
/// </summary>
public class RentScheduleCalculatorTests
{
    private static LocalDate D(int year, int month, int day) => new(year, month, day);

    private static RentScheduleDefinition MonthlyOnDay(
        int day,
        LocalDate start,
        LocalDate? end = null,
        ShortMonthFallback fallback = ShortMonthFallback.LastDayOfMonth) => new()
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.SpecificDay,
            DueDayOfMonth = day,
            StartDate = start,
            EndDate = end,
            ShortMonthFallback = fallback,
            Amount = 2400m,
            Currency = "USD",
        };

    // =========================================================================
    // Monthly: first of month
    // =========================================================================

    [Fact]
    public void FirstOfMonth_produces_the_first_of_every_month_in_the_term()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 6, 30),
            Amount = 1800m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [
                D(2026, 1, 1), D(2026, 2, 1), D(2026, 3, 1),
                D(2026, 4, 1), D(2026, 5, 1), D(2026, 6, 1),
            ],
            result.Occurrences.Select(o => o.DueDate));

        // Sequence numbers are 1-based and contiguous, so the UI can say
        // "payment 3 of 6".
        Assert.Equal([1, 2, 3, 4, 5, 6], result.Occurrences.Select(o => o.SequenceNumber));

        // The schedule ends inside the horizon, so the list is complete.
        Assert.False(result.TruncatedAtHorizon);
    }

    [Fact]
    public void A_mid_month_start_does_not_produce_a_due_date_before_the_lease_begins()
    {
        // Lease starts 15 January; rent is due on the first. January's first
        // has already passed, so the first payment is 1 February.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 15),
            EndDate = D(2026, 4, 30),
            Amount = 1800m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 2, 1), D(2026, 3, 1), D(2026, 4, 1)],
            result.Occurrences.Select(o => o.DueDate));
    }

    // =========================================================================
    // Monthly: last day of month
    // =========================================================================

    [Fact]
    public void LastOfMonth_tracks_the_real_length_of_each_month()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.LastOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 12, 31),
            Amount = 1500m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 6, 1));

        Assert.Equal(
            [
                D(2026, 1, 31),  // 31 days
                D(2026, 2, 28),  // 2026 is not a leap year
                D(2026, 3, 31),
                D(2026, 4, 30),  // 30 days
                D(2026, 5, 31),
                D(2026, 6, 30),
                D(2026, 7, 31),
                D(2026, 8, 31),
                D(2026, 9, 30),
                D(2026, 10, 31),
                D(2026, 11, 30),
                D(2026, 12, 31),
            ],
            result.Occurrences.Select(o => o.DueDate));
    }

    [Fact]
    public void LastOfMonth_gives_29_February_in_a_leap_year()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.LastOfMonth,
            StartDate = D(2028, 2, 1),
            EndDate = D(2028, 2, 29),
            Amount = 1500m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2029, 1, 1));

        var only = Assert.Single(result.Occurrences);
        Assert.Equal(D(2028, 2, 29), only.DueDate);

        // The rule asked for the last day and got it, so nothing was adjusted.
        Assert.Null(only.AdjustmentReason);
    }

    [Theory]
    // 2024 and 2028 are leap years; 2026, 2027, 2100 are not. 2000 is,
    // because it is divisible by 400.
    [InlineData(2024, 29)]
    [InlineData(2026, 28)]
    [InlineData(2027, 28)]
    [InlineData(2028, 29)]
    [InlineData(2100, 28)]
    [InlineData(2000, 29)]
    public void February_length_follows_the_Gregorian_leap_rule(int year, int expectedLastDay)
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.LastOfMonth,
            StartDate = D(year, 2, 1),
            EndDate = D(year, 2, expectedLastDay),
            Amount = 1000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(year + 1, 1, 1));

        Assert.Equal(D(year, 2, expectedLastDay), Assert.Single(result.Occurrences).DueDate);
    }

    // =========================================================================
    // Monthly: a specific day that does not exist in every month
    // =========================================================================

    [Fact]
    public void Day_31_falls_back_to_the_last_day_of_a_short_month_when_configured()
    {
        var definition = MonthlyOnDay(
            31, D(2026, 1, 1), D(2026, 6, 30), ShortMonthFallback.LastDayOfMonth);

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [
                D(2026, 1, 31),
                D(2026, 2, 28),  // clamped
                D(2026, 3, 31),
                D(2026, 4, 30),  // clamped
                D(2026, 5, 31),
                D(2026, 6, 30),  // clamped
            ],
            result.Occurrences.Select(o => o.DueDate));

        // Each clamped date says why, and keeps the date the rule asked for.
        var february = result.Occurrences[1];
        Assert.Equal(OccurrenceAdjustment.ShortMonthFallback, february.AdjustmentReason);
        Assert.Equal(D(2026, 2, 28), february.NominalDueDate);

        // An unclamped month carries no adjustment at all.
        Assert.Null(result.Occurrences[0].AdjustmentReason);
    }

    [Fact]
    public void Day_31_skips_short_months_when_configured_to_skip()
    {
        var definition = MonthlyOnDay(
            31, D(2026, 1, 1), D(2026, 6, 30), ShortMonthFallback.SkipMonth);

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        // Only the months that actually have a 31st.
        Assert.Equal(
            [D(2026, 1, 31), D(2026, 3, 31), D(2026, 5, 31)],
            result.Occurrences.Select(o => o.DueDate));

        // Skipping is a deliberate configuration, not an unresolved question.
        Assert.Empty(result.UnresolvedMonths);
    }

    [Fact]
    public void Day_31_reports_short_months_for_review_when_the_user_wants_to_decide()
    {
        var definition = MonthlyOnDay(
            31, D(2026, 1, 1), D(2026, 6, 30), ShortMonthFallback.ManualException);

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 1, 31), D(2026, 3, 31), D(2026, 5, 31)],
            result.Occurrences.Select(o => o.DueDate));

        // The months that need a human decision are named, not silently
        // dropped: the review screen has to be able to list them.
        Assert.Equal(
            [D(2026, 2, 1), D(2026, 4, 1), D(2026, 6, 1)],
            result.UnresolvedMonths.Select(m => m.MonthStart));

        var february = result.UnresolvedMonths[0];
        Assert.Equal(31, february.RequestedDayOfMonth);
        Assert.Equal(28, february.DaysInMonth);
    }

    [Fact]
    public void Day_30_is_only_short_in_February()
    {
        var definition = MonthlyOnDay(30, D(2026, 1, 1), D(2026, 4, 30));

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 1, 30), D(2026, 2, 28), D(2026, 3, 30), D(2026, 4, 30)],
            result.Occurrences.Select(o => o.DueDate));

        Assert.Equal(
            [null, OccurrenceAdjustment.ShortMonthFallback, null, null],
            result.Occurrences.Select(o => o.AdjustmentReason));
    }

    [Fact]
    public void Day_29_is_not_short_in_a_leap_February()
    {
        var definition = MonthlyOnDay(29, D(2028, 1, 1), D(2028, 3, 31));

        var result = RentScheduleCalculator.Expand(definition, D(2029, 1, 1));

        Assert.Equal(
            [D(2028, 1, 29), D(2028, 2, 29), D(2028, 3, 29)],
            result.Occurrences.Select(o => o.DueDate));

        // 29 February exists in 2028, so nothing is clamped.
        Assert.All(result.Occurrences, o => Assert.Null(o.AdjustmentReason));
    }

    [Fact]
    public void Day_15_needs_no_fallback_at_all()
    {
        var definition = MonthlyOnDay(15, D(2026, 1, 1), D(2026, 4, 30));

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 1, 15), D(2026, 2, 15), D(2026, 3, 15), D(2026, 4, 15)],
            result.Occurrences.Select(o => o.DueDate));
        Assert.All(result.Occurrences, o => Assert.Null(o.AdjustmentReason));
    }

    // =========================================================================
    // Business-day rules
    // =========================================================================

    [Fact]
    public void FirstBusinessDay_skips_a_weekend_at_the_start_of_the_month()
    {
        // 1 February 2026 is a Sunday, so the first business day is Monday 2nd.
        // 1 March 2026 is also a Sunday. 1 April 2026 is a Wednesday.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstBusinessDay,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 4, 30),
            Calendar = WorkingDayCalendar.SaturdaySundayWeekend,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 2, 2), D(2026, 3, 2), D(2026, 4, 1)],
            result.Occurrences.Select(o => o.DueDate));
    }

    [Fact]
    public void FirstBusinessDay_also_skips_a_holiday()
    {
        // 1 January 2027 is a Friday. Marked a holiday, so the first business
        // day becomes Monday 4 January (2nd and 3rd are the weekend).
        var calendar = new WorkingDayCalendar(
            [IsoDayOfWeek.Saturday, IsoDayOfWeek.Sunday],
            [D(2027, 1, 1)]);

        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstBusinessDay,
            StartDate = D(2027, 1, 1),
            EndDate = D(2027, 1, 31),
            Calendar = calendar,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 6, 1));

        Assert.Equal(D(2027, 1, 4), Assert.Single(result.Occurrences).DueDate);
    }

    [Fact]
    public void Nothing_shifts_when_the_user_has_not_asked_for_shifting()
    {
        // This is the default, and the specification is explicit that weekend
        // handling must never be assumed. 1 February 2026 is a Sunday and
        // stays a Sunday.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 3, 31),
            BusinessDayShift = BusinessDayShift.None,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 2, 1), D(2026, 3, 1)],
            result.Occurrences.Select(o => o.DueDate));
        Assert.All(result.Occurrences, o => Assert.Null(o.AdjustmentReason));
    }

    [Fact]
    public void NextBusinessDay_moves_a_weekend_due_date_forward_and_says_why()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 4, 30),
            BusinessDayShift = BusinessDayShift.NextBusinessDay,
            Calendar = WorkingDayCalendar.SaturdaySundayWeekend,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 2, 2), D(2026, 3, 2), D(2026, 4, 1)],
            result.Occurrences.Select(o => o.DueDate));

        // The nominal date is preserved so the UI can explain the move.
        Assert.Equal(D(2026, 2, 1), result.Occurrences[0].NominalDueDate);
        Assert.Equal(OccurrenceAdjustment.WeekendShift, result.Occurrences[0].AdjustmentReason);

        // April's first is a Wednesday, so it is untouched.
        Assert.Null(result.Occurrences[2].AdjustmentReason);
    }

    [Fact]
    public void PreviousBusinessDay_moves_a_weekend_due_date_backward()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 2, 28),
            BusinessDayShift = BusinessDayShift.PreviousBusinessDay,
            Calendar = WorkingDayCalendar.SaturdaySundayWeekend,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        // Sunday 1 February moves back to Friday 30 January, which is before
        // the schedule start. The occurrence is still produced, because the
        // nominal date is what the term is tested against.
        var only = Assert.Single(result.Occurrences);
        Assert.Equal(D(2026, 1, 30), only.DueDate);
        Assert.Equal(D(2026, 2, 1), only.NominalDueDate);
        Assert.Equal(OccurrenceAdjustment.WeekendShift, only.AdjustmentReason);
    }

    [Fact]
    public void A_holiday_shift_is_reported_separately_from_a_weekend_shift()
    {
        // Thursday 1 January 2026 marked as a holiday.
        var calendar = new WorkingDayCalendar(
            [IsoDayOfWeek.Saturday, IsoDayOfWeek.Sunday],
            [D(2026, 1, 1)]);

        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 1, 31),
            BusinessDayShift = BusinessDayShift.NextBusinessDay,
            Calendar = calendar,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        var only = Assert.Single(result.Occurrences);
        Assert.Equal(D(2026, 1, 2), only.DueDate);
        Assert.Equal(OccurrenceAdjustment.HolidayShift, only.AdjustmentReason);
    }

    [Fact]
    public void Business_day_shifting_without_a_calendar_is_rejected()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            BusinessDayShift = BusinessDayShift.NextBusinessDay,
            Calendar = null,
            Amount = 2000m,
            Currency = "USD",
        };

        // Guessing a weekend or a holiday list would quietly move rent due
        // dates, so the engine refuses instead.
        var error = Assert.Throws<ArgumentException>(
            () => RentScheduleCalculator.Expand(definition, D(2027, 1, 1)));

        Assert.Contains("working-day calendar", error.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // Interval frequencies
    // =========================================================================

    [Fact]
    public void Weekly_counts_seven_days_from_the_anchor()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Weekly,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 1, 5)],
            AnchorDate = D(2026, 1, 5),
            StartDate = D(2026, 1, 5),
            EndDate = D(2026, 2, 2),
            Amount = 500m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [
                D(2026, 1, 5), D(2026, 1, 12), D(2026, 1, 19),
                D(2026, 1, 26), D(2026, 2, 2),
            ],
            result.Occurrences.Select(o => o.DueDate));
    }

    [Fact]
    public void Biweekly_counts_fourteen_days_and_does_not_drift()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Biweekly,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 1, 2)],
            AnchorDate = D(2026, 1, 2),
            StartDate = D(2026, 1, 2),
            EndDate = D(2026, 4, 30),
            Amount = 1200m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        // Every date is a Friday, and the gap is exactly 14 days throughout.
        Assert.All(result.Occurrences, o => Assert.Equal(IsoDayOfWeek.Friday, o.DueDate.DayOfWeek));

        var dates = result.Occurrences.Select(o => o.DueDate).ToList();
        for (var i = 1; i < dates.Count; i++)
        {
            Assert.Equal(14, Period.Between(dates[i - 1], dates[i], PeriodUnits.Days).Days);
        }
    }

    [Fact]
    public void Every_30_days_drifts_against_the_calendar_which_is_the_point()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Every30Days,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 1, 1)],
            AnchorDate = D(2026, 1, 1),
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 5, 31),
            Amount = 1200m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        // "Every 30 days" is deliberately not "monthly": the dates walk
        // backwards through the calendar, which is why they are separate
        // frequencies rather than one with a fudge factor.
        Assert.Equal(
            [
                D(2026, 1, 1), D(2026, 1, 31), D(2026, 3, 2),
                D(2026, 4, 1), D(2026, 5, 1), D(2026, 5, 31),
            ],
            result.Occurrences.Select(o => o.DueDate));
    }

    [Fact]
    public void An_anchor_far_in_the_past_still_lands_on_the_right_dates()
    {
        // The anchor is years before the window. The generator must jump to
        // the window without stepping through every intervening fortnight,
        // and must land on a date that is an exact multiple of 14 days from
        // the anchor.
        var anchor = D(2020, 1, 3);

        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Biweekly,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [anchor],
            AnchorDate = anchor,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 2, 1),
            Amount = 1200m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.NotEmpty(result.Occurrences);
        Assert.All(result.Occurrences, o =>
            Assert.Equal(0, Period.Between(anchor, o.DueDate, PeriodUnits.Days).Days % 14));
        Assert.All(result.Occurrences, o => Assert.True(o.DueDate >= D(2026, 1, 1)));
    }

    [Fact]
    public void A_one_time_payment_produces_exactly_one_occurrence()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.OneTime,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 3, 15)],
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 12, 31),
            Amount = 4800m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(D(2026, 3, 15), Assert.Single(result.Occurrences).DueDate);
    }

    [Fact]
    public void A_custom_sequence_is_sorted_and_deduplicated()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Custom,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 3, 1), D(2026, 1, 1), D(2026, 3, 1), D(2026, 2, 1)],
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 12, 31),
            Amount = 900m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));

        Assert.Equal(
            [D(2026, 1, 1), D(2026, 2, 1), D(2026, 3, 1)],
            result.Occurrences.Select(o => o.DueDate));
    }

    // =========================================================================
    // The five dates stay separate (section 6.F)
    // =========================================================================

    [Fact]
    public void Grace_period_end_and_late_fee_date_are_independent_of_each_other()
    {
        // A lease that grants five grace days but charges a fee from day three.
        // Deriving one from the other would get this wrong in either direction.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 1, 31),
            GracePeriodDays = 5,
            LateFeeAfterDays = 3,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));
        var only = Assert.Single(result.Occurrences);

        Assert.Equal(D(2026, 1, 1), only.DueDate);
        Assert.Equal(D(2026, 1, 6), only.GracePeriodEndDate);
        Assert.Equal(D(2026, 1, 4), only.LateFeeDate);
    }

    [Fact]
    public void Derived_dates_follow_a_shifted_due_date()
    {
        // Sunday 1 February 2026 shifts to Monday the 2nd, and the grace
        // period is counted from the date actually in force.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 2, 28),
            BusinessDayShift = BusinessDayShift.NextBusinessDay,
            Calendar = WorkingDayCalendar.SaturdaySundayWeekend,
            GracePeriodDays = 5,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));
        var only = Assert.Single(result.Occurrences);

        Assert.Equal(D(2026, 2, 2), only.DueDate);
        Assert.Equal(D(2026, 2, 7), only.GracePeriodEndDate);
    }

    [Fact]
    public void No_grace_period_means_no_grace_date_rather_than_the_due_date()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = D(2026, 1, 31),
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2027, 1, 1));
        var only = Assert.Single(result.Occurrences);

        // Null, not the due date. "No grace period" and "a grace period that
        // ends the same day" are different statements about the lease.
        Assert.Null(only.GracePeriodEndDate);
        Assert.Null(only.LateFeeDate);
    }

    // =========================================================================
    // Per-occurrence overrides
    // =========================================================================

    [Fact]
    public void A_skip_override_removes_one_occurrence_and_keeps_numbering_contiguous()
    {
        var definition = MonthlyOnDay(1, D(2026, 1, 1), D(2026, 4, 30));

        var result = RentScheduleCalculator.Expand(
            definition,
            D(2027, 1, 1),
            [
                new RentScheduleOverride
                {
                    OriginalDueDate = D(2026, 2, 1),
                    Action = RentScheduleOverrideAction.Skip,
                },
            ]);

        Assert.Equal(
            [D(2026, 1, 1), D(2026, 3, 1), D(2026, 4, 1)],
            result.Occurrences.Select(o => o.DueDate));
        Assert.Equal([1, 2, 3], result.Occurrences.Select(o => o.SequenceNumber));
    }

    [Fact]
    public void A_move_override_is_honoured_exactly_and_is_not_shifted_again()
    {
        // The user moved rent to Saturday 7 February on purpose. Weekend
        // shifting is on, but an explicit instruction must win over a general
        // rule, or the user cannot express what they want.
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 2, 1),
            EndDate = D(2026, 2, 28),
            BusinessDayShift = BusinessDayShift.NextBusinessDay,
            Calendar = WorkingDayCalendar.SaturdaySundayWeekend,
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(
            definition,
            D(2027, 1, 1),
            [
                new RentScheduleOverride
                {
                    OriginalDueDate = D(2026, 2, 1),
                    Action = RentScheduleOverrideAction.Move,
                    ReplacementDate = D(2026, 2, 7),
                },
            ]);

        var only = Assert.Single(result.Occurrences);
        Assert.Equal(D(2026, 2, 7), only.DueDate);
        Assert.Equal(IsoDayOfWeek.Saturday, only.DueDate.DayOfWeek);
        Assert.Equal(OccurrenceAdjustment.UserException, only.AdjustmentReason);
    }

    [Fact]
    public void An_amount_override_changes_only_that_occurrence()
    {
        var definition = MonthlyOnDay(1, D(2026, 1, 1), D(2026, 3, 31));

        var result = RentScheduleCalculator.Expand(
            definition,
            D(2027, 1, 1),
            [
                new RentScheduleOverride
                {
                    OriginalDueDate = D(2026, 1, 1),
                    Action = RentScheduleOverrideAction.OverrideAmount,
                    Amount = 1200m,  // prorated first month
                },
            ]);

        Assert.Equal([1200m, 2400m, 2400m], result.Occurrences.Select(o => o.Amount));

        // The date did not move, but the occurrence still records that a human
        // changed something about it.
        Assert.Equal(D(2026, 1, 1), result.Occurrences[0].DueDate);
        Assert.Equal(OccurrenceAdjustment.UserException, result.Occurrences[0].AdjustmentReason);
    }

    [Fact]
    public void Two_overrides_for_the_same_date_are_rejected()
    {
        var definition = MonthlyOnDay(1, D(2026, 1, 1), D(2026, 3, 31));

        // The database enforces one exception per original due date; failing
        // here gives a message naming the date instead of a constraint error.
        Assert.Throws<ArgumentException>(() => RentScheduleCalculator.Expand(
            definition,
            D(2027, 1, 1),
            [
                new RentScheduleOverride
                {
                    OriginalDueDate = D(2026, 1, 1),
                    Action = RentScheduleOverrideAction.Skip,
                },
                new RentScheduleOverride
                {
                    OriginalDueDate = D(2026, 1, 1),
                    Action = RentScheduleOverrideAction.OverrideAmount,
                    Amount = 10m,
                },
            ]));
    }

    // =========================================================================
    // Horizon, bounds, and validation
    // =========================================================================

    [Fact]
    public void An_open_ended_schedule_stops_at_the_horizon_and_says_so()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 1, 1),
            EndDate = null,  // month to month
            Amount = 2000m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2026, 6, 30));

        Assert.Equal(6, result.Occurrences.Count);
        Assert.Equal(D(2026, 6, 1), result.Occurrences[^1].DueDate);

        // The caller needs to know this is a window, not the whole term,
        // otherwise a UI would show "6 payments" for an open-ended lease.
        Assert.True(result.TruncatedAtHorizon);
    }

    [Fact]
    public void Generation_is_bounded_even_for_a_weekly_schedule_with_a_distant_horizon()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Weekly,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 1, 1)],
            AnchorDate = D(2026, 1, 1),
            StartDate = D(2026, 1, 1),
            EndDate = null,
            Amount = 300m,
            Currency = "USD",
        };

        var result = RentScheduleCalculator.Expand(definition, D(2060, 1, 1));

        Assert.Equal(RentScheduleCalculator.MaxOccurrences, result.Occurrences.Count);
        Assert.True(result.TruncatedAtHorizon);
    }

    [Fact]
    public void Expansion_is_deterministic_so_regeneration_produces_identical_dates()
    {
        // Regeneration after a lease edit re-runs this engine. If it were not
        // deterministic, idempotency keys would differ between runs and
        // duplicate reminders would follow.
        var definition = MonthlyOnDay(31, D(2026, 1, 1), D(2027, 12, 31));

        var first = RentScheduleCalculator.Expand(definition, D(2028, 1, 1));
        var second = RentScheduleCalculator.Expand(definition, D(2028, 1, 1));

        Assert.Equal(
            first.Occurrences.Select(o => (o.SequenceNumber, o.DueDate, o.Amount)),
            second.Occurrences.Select(o => (o.SequenceNumber, o.DueDate, o.Amount)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public void An_impossible_due_day_is_rejected(int day)
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.SpecificDay,
            DueDayOfMonth = day,
            StartDate = D(2026, 1, 1),
            Amount = 2000m,
            Currency = "USD",
        };

        Assert.Throws<ArgumentException>(
            () => RentScheduleCalculator.Expand(definition, D(2027, 1, 1)));
    }

    [Fact]
    public void An_end_date_before_the_start_date_is_rejected()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Monthly,
            DueDayRule = DueDayRule.FirstOfMonth,
            StartDate = D(2026, 6, 1),
            EndDate = D(2026, 1, 1),
            Amount = 2000m,
            Currency = "USD",
        };

        Assert.Throws<ArgumentException>(
            () => RentScheduleCalculator.Expand(definition, D(2027, 1, 1)));
    }

    [Fact]
    public void An_interval_frequency_without_an_anchor_is_rejected()
    {
        var definition = new RentScheduleDefinition
        {
            Frequency = RentFrequency.Biweekly,
            DueDayRule = DueDayRule.CustomSequence,
            CustomDates = [D(2026, 1, 1)],
            AnchorDate = null,
            StartDate = D(2026, 1, 1),
            Amount = 1000m,
            Currency = "USD",
        };

        Assert.Throws<ArgumentException>(
            () => RentScheduleCalculator.Expand(definition, D(2027, 1, 1)));
    }
}
