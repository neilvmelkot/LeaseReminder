using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Application.Notifications;

/// <summary>
/// Turns "three days before rent is due, at 9:00 AM America/New_York" into a
/// single UTC instant.
///
/// <para>
/// This is where daylight saving time is actually dealt with (section 7). Two
/// local times per year are not ordinary:
/// </para>
/// <list type="bullet">
///   <item>
///     A <b>skipped</b> time, in the spring-forward gap. 02:30 simply does not
///     occur on that date. The policy here is to fire at the first instant
///     that does exist, which is the moment the clocks jump to. A reminder
///     arrives slightly late rather than silently never arriving.
///   </item>
///   <item>
///     An <b>ambiguous</b> time, in the autumn fall-back overlap. 01:30 occurs
///     twice. The policy is to take the EARLIER of the two, so a reminder is
///     sent once, on time, and never twice.
///   </item>
/// </list>
/// <para>
/// Both cases set <see cref="NotificationInstant.DaylightSavingAdjusted"/>, so
/// the calendar view can explain the chosen instant instead of looking wrong.
/// </para>
///
/// <para>
/// Offsets in DAYS move the local calendar date and then resolve the send time
/// in the zone, so "3 days before at 9 AM" is 9 AM local on each of those
/// days, whatever happens to the clocks in between. Offsets in HOURS and
/// MINUTES are applied in absolute time, because an offset that fine-grained
/// is about elapsed time rather than about a wall-clock appointment.
/// </para>
/// </summary>
public static class NotificationInstantCalculator
{
    /// <summary>
    /// Computes the send instant for one event.
    /// </summary>
    /// <param name="eventLocalDate">The local date of the underlying event.</param>
    /// <param name="triggerType">Before, on, or after the event.</param>
    /// <param name="offsetValue">Magnitude of the offset; zero when trigger is On.</param>
    /// <param name="offsetUnit">Unit the offset is expressed in.</param>
    /// <param name="sendLocalTime">Intended local wall-clock send time.</param>
    /// <param name="zone">The IANA zone the local date and time are expressed in.</param>
    /// <param name="quietHours">
    /// Optional window during which nothing should be sent. Applied after the
    /// instant is computed.
    /// </param>
    public static NotificationInstant Compute(
        LocalDate eventLocalDate,
        NotificationTriggerType triggerType,
        int offsetValue,
        OffsetUnit offsetUnit,
        LocalTime sendLocalTime,
        DateTimeZone zone,
        QuietHours? quietHours = null)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (offsetValue < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offsetValue),
                offsetValue,
                "An offset magnitude is never negative; direction comes from the trigger type.");
        }

        if (triggerType == NotificationTriggerType.On && offsetValue != 0)
        {
            throw new ArgumentException(
                "An 'on the day' trigger cannot carry an offset.", nameof(offsetValue));
        }

        var signedOffset = triggerType switch
        {
            NotificationTriggerType.Before => -offsetValue,
            NotificationTriggerType.After => offsetValue,
            NotificationTriggerType.On => 0,
            _ => throw new ArgumentOutOfRangeException(
                nameof(triggerType), triggerType, "Unsupported trigger type."),
        };

        // Day offsets move the calendar date, so the local send time is
        // preserved exactly. Finer offsets move the instant.
        var targetLocalDate = offsetUnit == OffsetUnit.Days
            ? eventLocalDate.PlusDays(signedOffset)
            : eventLocalDate;

        var intendedLocal = targetLocalDate.At(sendLocalTime);
        var resolved = ResolveInZone(intendedLocal, zone);
        var instant = resolved.Instant;
        var adjusted = resolved.Adjusted;

        if (offsetUnit != OffsetUnit.Days && signedOffset != 0)
        {
            // Absolute-time arithmetic: adding two hours always advances the
            // instant by exactly two hours, even across a transition.
            var duration = offsetUnit switch
            {
                OffsetUnit.Hours => Duration.FromHours(signedOffset),
                OffsetUnit.Minutes => Duration.FromMinutes(signedOffset),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(offsetUnit), offsetUnit, "Unsupported offset unit."),
            };

            instant += duration;
        }

        var quietHoursApplied = false;

        if (quietHours is not null)
        {
            var afterQuietHours = ApplyQuietHours(instant, zone, quietHours);
            quietHoursApplied = afterQuietHours.Instant != instant || afterQuietHours.Skipped;

            if (afterQuietHours.Skipped)
            {
                return new NotificationInstant
                {
                    Instant = instant,
                    LocalDateTime = instant.InZone(zone).LocalDateTime,
                    IntendedLocalDateTime = intendedLocal,
                    Zone = zone,
                    DaylightSavingAdjusted = adjusted,
                    QuietHoursApplied = true,
                    SkippedByQuietHours = true,
                };
            }

            instant = afterQuietHours.Instant;
        }

        return new NotificationInstant
        {
            Instant = instant,
            LocalDateTime = instant.InZone(zone).LocalDateTime,
            IntendedLocalDateTime = intendedLocal,
            Zone = zone,
            DaylightSavingAdjusted = adjusted,
            QuietHoursApplied = quietHoursApplied,
            SkippedByQuietHours = false,
        };
    }

    /// <summary>
    /// Maps a local date and time to a single instant, deciding explicitly
    /// what to do when the local time is skipped or ambiguous.
    /// </summary>
    private static (Instant Instant, bool Adjusted) ResolveInZone(
        LocalDateTime local,
        DateTimeZone zone)
    {
        var mapping = zone.MapLocal(local);

        return mapping.Count switch
        {
            // The ordinary case.
            1 => (mapping.First().ToInstant(), false),

            // Spring forward: this local time does not exist. Fire at the
            // first instant that DOES, which is the transition itself, so the
            // reminder is as little late as possible.
            //
            // LateInterval.Start is that instant. NodaTime's AtLeniently is
            // deliberately not used here: it shifts the local time forward by
            // the length of the gap, so an intended 02:30 would become 03:30
            // rather than 03:00. Half an hour of avoidable delay is a small
            // thing, but the promise this method makes is "the first valid
            // moment at or after the intended local time", and it should keep
            // it exactly.
            0 => (mapping.LateInterval.Start, true),

            // Fall back: this local time happens twice. Take the first, so the
            // reminder is sent once and as early as intended.
            _ => (mapping.First().ToInstant(), true),
        };
    }

    /// <summary>
    /// Moves a send out of a quiet-hours window, or reports that it should be
    /// skipped entirely.
    /// </summary>
    private static (Instant Instant, bool Skipped) ApplyQuietHours(
        Instant instant,
        DateTimeZone zone,
        QuietHours quietHours)
    {
        var local = instant.InZone(zone).LocalDateTime;

        if (!quietHours.Contains(local.TimeOfDay))
        {
            return (instant, false);
        }

        if (!quietHours.DeferOutsideWindow)
        {
            return (instant, true);
        }

        // Defer to the moment quiet hours end. A window that wraps midnight
        // (22:00 to 07:00) ends on the following local day whenever the send
        // falls in the late-evening part of it.
        var endsNextDay = quietHours.WrapsMidnight && local.TimeOfDay >= quietHours.Start;
        var targetDate = endsNextDay ? local.Date.PlusDays(1) : local.Date;
        var deferredLocal = targetDate.At(quietHours.End);

        return (ResolveInZone(deferredLocal, zone).Instant, false);
    }
}

/// <summary>The computed send instant, with enough context to explain it.</summary>
public sealed record NotificationInstant
{
    /// <summary>The instant to send. Stored as scheduled_at_utc.</summary>
    public required Instant Instant { get; init; }

    /// <summary>What <see cref="Instant"/> actually is in local terms.</summary>
    public required LocalDateTime LocalDateTime { get; init; }

    /// <summary>
    /// What the rule asked for. Differs from <see cref="LocalDateTime"/> when a
    /// daylight saving transition or quiet hours moved the send.
    /// </summary>
    public required LocalDateTime IntendedLocalDateTime { get; init; }

    public required DateTimeZone Zone { get; init; }

    /// <summary>
    /// True when the intended local time was skipped or ambiguous at a
    /// daylight saving transition. Stored as dst_adjusted.
    /// </summary>
    public required bool DaylightSavingAdjusted { get; init; }

    public required bool QuietHoursApplied { get; init; }

    /// <summary>
    /// True when the send falls inside quiet hours and the rule asked to skip
    /// rather than defer. The caller records the notification as skipped with
    /// a reason instead of queueing it.
    /// </summary>
    public required bool SkippedByQuietHours { get; init; }
}

/// <summary>A local-time window during which nothing should be sent.</summary>
public sealed record QuietHours
{
    public required LocalTime Start { get; init; }

    public required LocalTime End { get; init; }

    /// <summary>
    /// When true, a send inside the window is moved to <see cref="End"/>. When
    /// false, it is skipped. Deferring is the default because a dropped rent
    /// reminder is worse than a late one.
    /// </summary>
    public bool DeferOutsideWindow { get; init; } = true;

    /// <summary>True for a window like 22:00 to 07:00.</summary>
    public bool WrapsMidnight => Start > End;

    public bool Contains(LocalTime time) =>
        WrapsMidnight
            ? time >= Start || time < End
            : time >= Start && time < End;
}
