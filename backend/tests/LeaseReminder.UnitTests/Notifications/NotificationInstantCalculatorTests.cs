using LeaseReminder.Application.Notifications;
using LeaseReminder.Domain;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace LeaseReminder.UnitTests.Notifications;

/// <summary>
/// Covers the time zone conversion and daylight saving items in section 9,
/// including the two cases the specification singles out: ambiguous and
/// nonexistent local times.
///
/// The transition dates used here are real:
///   America/New_York  spring forward  8 March 2026, 02:00 to 03:00
///                     fall back       1 November 2026, 02:00 to 01:00
///   Europe/London     spring forward  29 March 2026, 01:00 to 02:00
///   Australia/Sydney  fall back        5 April 2026 (southern hemisphere)
/// </summary>
public class NotificationInstantCalculatorTests
{
    private static readonly DateTimeZone NewYork =
        DateTimeZoneProviders.Tzdb["America/New_York"];

    private static readonly DateTimeZone London =
        DateTimeZoneProviders.Tzdb["Europe/London"];

    private static readonly DateTimeZone Sydney =
        DateTimeZoneProviders.Tzdb["Australia/Sydney"];

    private static readonly DateTimeZone Kolkata =
        DateTimeZoneProviders.Tzdb["Asia/Kolkata"];

    private static LocalDate D(int year, int month, int day) => new(year, month, day);

    private static LocalTime T(int hour, int minute) => new(hour, minute);

    private static string Utc(Instant instant) =>
        InstantPattern.ExtendedIso.Format(instant);

    // =========================================================================
    // Ordinary time zone conversion
    // =========================================================================

    [Fact]
    public void Nine_am_in_New_York_in_winter_is_fourteen_hundred_UTC()
    {
        // Eastern Standard Time is UTC-5.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 1, 15),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal("2026-01-15T14:00:00Z", Utc(result.Instant));
        Assert.False(result.DaylightSavingAdjusted);
    }

    [Fact]
    public void Nine_am_in_New_York_in_summer_is_thirteen_hundred_UTC()
    {
        // Eastern Daylight Time is UTC-4. Same local time, different instant,
        // which is the whole reason the local time and zone are stored
        // alongside the computed instant.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 7, 15),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal("2026-07-15T13:00:00Z", Utc(result.Instant));
    }

    [Fact]
    public void A_half_hour_offset_zone_is_handled_correctly()
    {
        // Asia/Kolkata is UTC+5:30 year round. Zones whose offset is not a
        // whole number of hours are a classic source of off-by-thirty-minutes.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 6, 1),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: Kolkata);

        Assert.Equal("2026-06-01T03:30:00Z", Utc(result.Instant));
    }

    [Fact]
    public void A_southern_hemisphere_zone_shifts_the_other_way()
    {
        // Sydney is UTC+11 in January (daylight saving) and UTC+10 in July.
        var january = NotificationInstantCalculator.Compute(
            D(2026, 1, 15), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(9, 0), Sydney);

        var july = NotificationInstantCalculator.Compute(
            D(2026, 7, 15), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(9, 0), Sydney);

        Assert.Equal("2026-01-14T22:00:00Z", Utc(january.Instant));
        Assert.Equal("2026-07-14T23:00:00Z", Utc(july.Instant));
    }

    // =========================================================================
    // Offsets in days preserve the local send time
    // =========================================================================

    [Fact]
    public void Three_days_before_sends_at_the_same_local_time_three_days_earlier()
    {
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 4, 1),
            triggerType: NotificationTriggerType.Before,
            offsetValue: 3,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal(D(2026, 3, 29), result.LocalDateTime.Date);
        Assert.Equal(T(9, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void Three_days_overdue_sends_after_the_event()
    {
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 4, 1),
            triggerType: NotificationTriggerType.After,
            offsetValue: 3,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal(D(2026, 4, 4), result.LocalDateTime.Date);
        Assert.Equal(T(9, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void A_day_offset_spanning_a_transition_keeps_the_local_time_not_the_elapsed_time()
    {
        // Rent due 10 March 2026, reminder 7 days earlier at 09:00 local.
        // The clocks go forward on 8 March, between the two dates. The send
        // must still be 09:00 local on 3 March, which is 14:00 UTC (EST),
        // NOT 13:00 UTC as a naive "subtract 168 hours" would give.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 3, 10),
            triggerType: NotificationTriggerType.Before,
            offsetValue: 7,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal(D(2026, 3, 3), result.LocalDateTime.Date);
        Assert.Equal(T(9, 0), result.LocalDateTime.TimeOfDay);
        Assert.Equal("2026-03-03T14:00:00Z", Utc(result.Instant));
    }

    [Fact]
    public void An_hour_offset_is_applied_in_absolute_time()
    {
        // Hours and minutes are about elapsed time, so two hours is always
        // exactly two hours. 09:00 EST is 14:00 UTC; two hours earlier is
        // 12:00 UTC.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 1, 15),
            triggerType: NotificationTriggerType.Before,
            offsetValue: 2,
            offsetUnit: OffsetUnit.Hours,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal("2026-01-15T12:00:00Z", Utc(result.Instant));
        Assert.Equal(T(7, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void A_minute_offset_is_applied_in_absolute_time()
    {
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 1, 15),
            triggerType: NotificationTriggerType.After,
            offsetValue: 90,
            offsetUnit: OffsetUnit.Minutes,
            sendLocalTime: T(9, 0),
            zone: NewYork);

        Assert.Equal("2026-01-15T15:30:00Z", Utc(result.Instant));
    }

    // =========================================================================
    // Daylight saving: nonexistent local times (spring forward)
    // =========================================================================

    [Fact]
    public void A_local_time_inside_the_spring_forward_gap_fires_at_the_first_real_instant()
    {
        // On 8 March 2026 in New York, 02:30 does not exist: the clocks jump
        // from 02:00 EST straight to 03:00 EDT. The reminder must still go
        // out, at the moment the clocks jump, rather than being dropped.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 3, 8),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(2, 30),
            zone: NewYork);

        Assert.True(result.DaylightSavingAdjusted);

        // 03:00 EDT is 07:00 UTC.
        Assert.Equal("2026-03-08T07:00:00Z", Utc(result.Instant));

        // The actual local time is after the gap.
        Assert.Equal(T(3, 0), result.LocalDateTime.TimeOfDay);

        // What the rule asked for is retained, so the UI can explain the move.
        Assert.Equal(T(2, 30), result.IntendedLocalDateTime.TimeOfDay);
    }

    [Fact]
    public void A_send_time_outside_the_gap_on_a_transition_day_is_not_flagged()
    {
        // 09:00 on 8 March 2026 exists perfectly well; only the gap is special.
        var result = NotificationInstantCalculator.Compute(
            D(2026, 3, 8), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(9, 0), NewYork);

        Assert.False(result.DaylightSavingAdjusted);

        // Already EDT by 09:00, so UTC-4.
        Assert.Equal("2026-03-08T13:00:00Z", Utc(result.Instant));
    }

    [Fact]
    public void The_London_spring_forward_gap_behaves_the_same_way()
    {
        // 29 March 2026: London jumps from 01:00 GMT to 02:00 BST, so 01:30
        // does not exist.
        var result = NotificationInstantCalculator.Compute(
            D(2026, 3, 29), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(1, 30), London);

        Assert.True(result.DaylightSavingAdjusted);
        Assert.Equal("2026-03-29T01:00:00Z", Utc(result.Instant));
        Assert.Equal(T(2, 0), result.LocalDateTime.TimeOfDay);
    }

    // =========================================================================
    // Daylight saving: ambiguous local times (fall back)
    // =========================================================================

    [Fact]
    public void An_ambiguous_local_time_resolves_to_the_earlier_instant()
    {
        // On 1 November 2026 in New York, 01:30 happens twice: once at
        // 05:30 UTC (still EDT) and again at 06:30 UTC (now EST). Taking the
        // earlier one sends the reminder once, at the first moment the local
        // clock reads 01:30. Taking "both" would double-send; taking the later
        // one would delay it an hour for no reason.
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 11, 1),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(1, 30),
            zone: NewYork);

        Assert.True(result.DaylightSavingAdjusted);
        Assert.Equal("2026-11-01T05:30:00Z", Utc(result.Instant));
    }

    [Fact]
    public void An_ambiguous_time_produces_exactly_one_instant_not_two()
    {
        // The property that actually matters: whatever the resolution policy,
        // one scheduled notification must come out of one event.
        var first = NotificationInstantCalculator.Compute(
            D(2026, 11, 1), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(1, 30), NewYork);

        var second = NotificationInstantCalculator.Compute(
            D(2026, 11, 1), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(1, 30), NewYork);

        // Deterministic, so a regeneration cannot pick the other instant and
        // create a second queue row for the same reminder.
        Assert.Equal(first.Instant, second.Instant);
    }

    [Fact]
    public void A_southern_hemisphere_fall_back_is_also_handled()
    {
        // Sydney falls back on 5 April 2026: 03:00 becomes 02:00, so 02:30
        // occurs twice.
        var result = NotificationInstantCalculator.Compute(
            D(2026, 4, 5), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(2, 30), Sydney);

        Assert.True(result.DaylightSavingAdjusted);

        // The earlier of the two, still at UTC+11.
        Assert.Equal("2026-04-04T15:30:00Z", Utc(result.Instant));
    }

    [Fact]
    public void Reminders_across_a_transition_stay_in_chronological_order()
    {
        // A realistic rent-due ladder spanning the autumn transition. The
        // computed instants must be strictly increasing; if they were not, a
        // "3 days before" reminder could be sent after a "1 day before" one.
        var dueDate = D(2026, 11, 3);
        var offsets = new[] { 7, 5, 3, 1, 0 };

        var instants = offsets
            .Select(days => NotificationInstantCalculator.Compute(
                dueDate,
                days == 0 ? NotificationTriggerType.On : NotificationTriggerType.Before,
                days,
                OffsetUnit.Days,
                T(9, 0),
                NewYork).Instant)
            .ToList();

        for (var i = 1; i < instants.Count; i++)
        {
            Assert.True(
                instants[i] > instants[i - 1],
                $"Reminder {offsets[i]} days before was not after {offsets[i - 1]} days before.");
        }
    }

    // =========================================================================
    // Quiet hours
    // =========================================================================

    [Fact]
    public void A_send_inside_quiet_hours_is_deferred_to_the_end_of_the_window()
    {
        var result = NotificationInstantCalculator.Compute(
            eventLocalDate: D(2026, 6, 15),
            triggerType: NotificationTriggerType.On,
            offsetValue: 0,
            offsetUnit: OffsetUnit.Days,
            sendLocalTime: T(23, 0),
            zone: NewYork,
            quietHours: new QuietHours
            {
                Start = T(22, 0),
                End = T(7, 0),
                DeferOutsideWindow = true,
            });

        Assert.True(result.QuietHoursApplied);
        Assert.False(result.SkippedByQuietHours);

        // The window wraps midnight, so 23:00 defers to 07:00 the NEXT day.
        Assert.Equal(D(2026, 6, 16), result.LocalDateTime.Date);
        Assert.Equal(T(7, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void An_early_morning_send_defers_to_the_same_day()
    {
        var result = NotificationInstantCalculator.Compute(
            D(2026, 6, 15), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(3, 0), NewYork,
            new QuietHours { Start = T(22, 0), End = T(7, 0) });

        Assert.True(result.QuietHoursApplied);

        // 03:00 is in the tail of the previous evening's window, which ends at
        // 07:00 on the same calendar day.
        Assert.Equal(D(2026, 6, 15), result.LocalDateTime.Date);
        Assert.Equal(T(7, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void A_send_outside_quiet_hours_is_untouched()
    {
        var result = NotificationInstantCalculator.Compute(
            D(2026, 6, 15), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(9, 0), NewYork,
            new QuietHours { Start = T(22, 0), End = T(7, 0) });

        Assert.False(result.QuietHoursApplied);
        Assert.Equal(T(9, 0), result.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void Quiet_hours_can_skip_instead_of_defer_when_the_rule_says_so()
    {
        var result = NotificationInstantCalculator.Compute(
            D(2026, 6, 15), NotificationTriggerType.On, 0, OffsetUnit.Days,
            T(23, 0), NewYork,
            new QuietHours
            {
                Start = T(22, 0),
                End = T(7, 0),
                DeferOutsideWindow = false,
            });

        Assert.True(result.SkippedByQuietHours);
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]   // the window end is exclusive
    [InlineData(12, 0, false)]
    public void A_midnight_wrapping_window_contains_the_right_times(
        int hour, int minute, bool expected)
    {
        var quietHours = new QuietHours { Start = T(22, 0), End = T(7, 0) };

        Assert.Equal(expected, quietHours.Contains(T(hour, minute)));
    }

    [Theory]
    [InlineData(12, 59, false)]
    [InlineData(13, 0, true)]
    [InlineData(13, 30, true)]
    [InlineData(14, 0, false)]  // the window end is exclusive
    public void A_same_day_window_contains_the_right_times(int hour, int minute, bool expected)
    {
        var quietHours = new QuietHours { Start = T(13, 0), End = T(14, 0) };

        Assert.Equal(expected, quietHours.Contains(T(hour, minute)));
    }

    // =========================================================================
    // Argument validation
    // =========================================================================

    [Fact]
    public void An_on_the_day_trigger_cannot_carry_an_offset()
    {
        Assert.Throws<ArgumentException>(() => NotificationInstantCalculator.Compute(
            D(2026, 1, 1), NotificationTriggerType.On, 3, OffsetUnit.Days,
            T(9, 0), NewYork));
    }

    [Fact]
    public void A_negative_offset_is_rejected_because_direction_comes_from_the_trigger()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationInstantCalculator.Compute(
            D(2026, 1, 1), NotificationTriggerType.Before, -3, OffsetUnit.Days,
            T(9, 0), NewYork));
    }
}
