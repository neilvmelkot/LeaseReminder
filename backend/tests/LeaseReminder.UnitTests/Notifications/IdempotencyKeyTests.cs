using LeaseReminder.Application.Notifications;
using LeaseReminder.Domain;
using NodaTime;
using Xunit;

namespace LeaseReminder.UnitTests.Notifications;

/// <summary>
/// Covers the idempotency key generation item in section 9, and the duplicate
/// notification prevention requirement in section 4.G.
///
/// The key only works if it is stable across processes and machines, and if it
/// changes exactly when a different send is intended. Both halves are tested
/// here, because getting either wrong is silent: too stable and a rule edit is
/// lost, too volatile and the tenant gets two emails.
/// </summary>
public class IdempotencyKeyTests
{
    private static readonly Guid RuleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRuleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OccurrenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherOccurrenceId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static NotificationTiming Timing(
        NotificationTriggerType trigger = NotificationTriggerType.Before,
        int offset = 3,
        OffsetUnit unit = OffsetUnit.Days,
        int hour = 9,
        string zone = "America/New_York") => new()
        {
            TriggerType = trigger,
            OffsetValue = offset,
            OffsetUnit = unit,
            SendLocalTime = new LocalTime(hour, 0),
            TimeZoneId = zone,
        };

    private static string Key(
        NotificationTiming? timing = null,
        Guid? ruleId = null,
        NotificationEventType eventType = NotificationEventType.RentDue,
        int day = 1,
        Guid? occurrenceId = null) =>
        IdempotencyKey.ForNotification(
            ruleId ?? RuleId,
            eventType,
            new LocalDate(2026, 3, day),
            timing ?? Timing(),
            occurrenceId ?? OccurrenceId);

    // =========================================================================
    // Stability
    // =========================================================================

    [Fact]
    public void The_same_inputs_always_produce_the_same_key()
    {
        Assert.Equal(Key(), Key());
    }

    [Fact]
    public void The_key_does_not_depend_on_object_identity()
    {
        // Two separately constructed but equal timing records must agree.
        // Using GetHashCode anywhere in the key would break this across
        // processes, which is the subtle version of the same bug.
        var first = Key(Timing());
        var second = Key(Timing());

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_key_is_stable_across_the_current_culture()
    {
        // A machine with a different locale must compute an identical key, or
        // the UNIQUE constraint stops protecting anything. Date and time
        // formatting is the usual culprit.
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            var american = Key();

            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var german = Key();

            // Thai Buddhist calendar: the year differs by 543 in a
            // culture-sensitive format.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var thai = Key();

            Assert.Equal(american, german);
            Assert.Equal(american, thai);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_key_fits_the_database_column()
    {
        // scheduled_notifications.idempotency_key accepts 16 to 200 characters.
        var key = Key();

        Assert.InRange(key.Length, 16, 200);
    }

    [Fact]
    public void Every_event_type_produces_a_distinct_token()
    {
        // Two event types sharing a token would make two different reminders
        // collide, and one would silently never be sent.
        var keys = Enum.GetValues<NotificationEventType>()
            .Select(eventType => Key(eventType: eventType))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // =========================================================================
    // What the key IS sensitive to: anything that changes when a send happens
    // =========================================================================

    [Fact]
    public void A_different_rule_produces_a_different_key()
    {
        Assert.NotEqual(Key(ruleId: RuleId), Key(ruleId: OtherRuleId));
    }

    [Fact]
    public void A_different_event_date_produces_a_different_key()
    {
        Assert.NotEqual(Key(day: 1), Key(day: 2));
    }

    [Fact]
    public void A_different_rent_occurrence_produces_a_different_key()
    {
        // Each month's rent is its own reminder, even from the same rule.
        Assert.NotEqual(
            Key(occurrenceId: OccurrenceId),
            Key(occurrenceId: OtherOccurrenceId));
    }

    [Fact]
    public void Changing_the_offset_produces_a_different_key()
    {
        // The user moved a reminder from 3 days before to 5 days before. That
        // is a different send, so it must get its own queue row rather than
        // colliding with the superseded one.
        Assert.NotEqual(Key(Timing(offset: 3)), Key(Timing(offset: 5)));
    }

    [Fact]
    public void Changing_the_offset_unit_produces_a_different_key()
    {
        // 3 days before and 3 hours before are not the same reminder, even
        // though the numeral is identical.
        Assert.NotEqual(
            Key(Timing(offset: 3, unit: OffsetUnit.Days)),
            Key(Timing(offset: 3, unit: OffsetUnit.Hours)));
    }

    [Fact]
    public void Changing_the_trigger_direction_produces_a_different_key()
    {
        Assert.NotEqual(
            Key(Timing(trigger: NotificationTriggerType.Before)),
            Key(Timing(trigger: NotificationTriggerType.After)));
    }

    [Fact]
    public void Changing_the_send_time_produces_a_different_key()
    {
        Assert.NotEqual(Key(Timing(hour: 9)), Key(Timing(hour: 17)));
    }

    [Fact]
    public void Changing_the_time_zone_produces_a_different_key()
    {
        // Same wall-clock time, different zone, therefore a different instant.
        Assert.NotEqual(
            Key(Timing(zone: "America/New_York")),
            Key(Timing(zone: "America/Los_Angeles")));
    }

    // =========================================================================
    // What the key is NOT sensitive to
    // =========================================================================

    [Fact]
    public void A_lease_level_event_and_a_rent_event_do_not_collide()
    {
        // Lease expiry has no rent occurrence. Passing null must not land on
        // the same key as some occurrence happening to be absent.
        var leaseLevel = IdempotencyKey.ForNotification(
            RuleId,
            NotificationEventType.LeaseExpiration,
            new LocalDate(2026, 12, 31),
            Timing(),
            rentOccurrenceId: null);

        var rentLevel = IdempotencyKey.ForNotification(
            RuleId,
            NotificationEventType.RentDue,
            new LocalDate(2026, 12, 31),
            Timing(),
            OccurrenceId);

        Assert.NotEqual(leaseLevel, rentLevel);
    }

    [Fact]
    public void Two_lease_level_events_on_the_same_date_from_one_rule_are_the_same_reminder()
    {
        // Regeneration after an unrelated edit must converge on the same key,
        // which is what makes the second insert a no-op instead of a second
        // email.
        var first = IdempotencyKey.ForNotification(
            RuleId, NotificationEventType.LeaseExpiration,
            new LocalDate(2026, 12, 31), Timing(), null);

        var second = IdempotencyKey.ForNotification(
            RuleId, NotificationEventType.LeaseExpiration,
            new LocalDate(2026, 12, 31), Timing(), null);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_whole_realistic_ladder_from_section_6G_produces_six_distinct_keys()
    {
        // The example schedule in the specification: 7 days before, 3 days
        // before, 1 day before, on the day at 09:00, 3 days overdue, 5 days
        // overdue. Six rules, one rent occurrence, six separate emails.
        var ladder = new (NotificationTriggerType Trigger, int Offset)[]
        {
            (NotificationTriggerType.Before, 7),
            (NotificationTriggerType.Before, 3),
            (NotificationTriggerType.Before, 1),
            (NotificationTriggerType.On, 0),
            (NotificationTriggerType.After, 3),
            (NotificationTriggerType.After, 5),
        };

        var keys = ladder
            .Select((step, index) => IdempotencyKey.ForNotification(
                // Each rung is its own rule row in the real system.
                Guid.Parse($"aaaaaaaa-0000-0000-0000-00000000000{index}"),
                NotificationEventType.RentDue,
                new LocalDate(2026, 4, 1),
                Timing(step.Trigger, step.Offset),
                OccurrenceId))
            .ToList();

        Assert.Equal(6, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_full_year_of_monthly_reminders_from_one_rule_is_twelve_distinct_keys()
    {
        var keys = Enumerable.Range(1, 12)
            .Select(month => IdempotencyKey.ForNotification(
                RuleId,
                NotificationEventType.RentDue,
                new LocalDate(2026, month, 1),
                Timing(),
                // In reality each month has its own occurrence id; the date
                // alone already separates them.
                Guid.Parse($"bbbbbbbb-0000-0000-0000-0000000000{month:D2}")))
            .ToList();

        Assert.Equal(12, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
