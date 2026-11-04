using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Application.Notifications;

/// <summary>
/// Builds the deterministic key that prevents duplicate reminders.
///
/// <para>
/// The key is paired with a UNIQUE constraint on
/// (organization_id, idempotency_key). That constraint, not any application
/// logic, is what actually stops a second email: two workers racing, a
/// regeneration running twice, or a restart mid-generation all converge on the
/// same key, and the second insert is a no-op.
/// </para>
///
/// <para>
/// The interesting design question is what the key includes. Section 7 asks
/// that a lease or rule change supersede future notifications and then
/// regenerate them. If the key never varied, the regenerated row would collide
/// with the superseded one and the new timing would be lost. So the key
/// carries a fingerprint of exactly the fields that determine WHEN a send
/// happens:
/// </para>
/// <list type="bullet">
///   <item>trigger type, offset value and unit</item>
///   <item>local send time and time zone</item>
/// </list>
/// <para>
/// Changing any of those is a different send, gets a different key, and
/// correctly produces a new queue row. Changing a recipient list, a template
/// body, or a sender is NOT in the fingerprint: the send still happens at the
/// same moment, so the pending row is updated in place rather than being
/// cancelled and recreated. That distinction is the difference between editing
/// a recipient and silently re-sending a reminder somebody already received.
/// </para>
/// </summary>
public static class IdempotencyKey
{
    /// <summary>
    /// Bumped only if the key format itself changes. A new version makes every
    /// key differ, which would regenerate pending notifications, so it must be
    /// deployed together with a migration that supersedes the old rows.
    /// </summary>
    private const string KeyFormatVersion = "v1";

    /// <summary>Length of the hex fingerprint. 12 hex characters is 48 bits.</summary>
    private const int FingerprintLength = 12;

    /// <summary>
    /// Builds the key for one scheduled notification.
    /// </summary>
    /// <param name="notificationRuleId">The rule producing the send.</param>
    /// <param name="eventType">Which lease event this reminder is about.</param>
    /// <param name="eventLocalDate">Local date of that event.</param>
    /// <param name="timing">The timing fields the key is sensitive to.</param>
    /// <param name="rentOccurrenceId">
    /// The specific rent payment, for rent-related events. Null for lease-level
    /// events such as expiry, which happen once per lease.
    /// </param>
    public static string ForNotification(
        Guid notificationRuleId,
        NotificationEventType eventType,
        LocalDate eventLocalDate,
        NotificationTiming timing,
        Guid? rentOccurrenceId = null)
    {
        ArgumentNullException.ThrowIfNull(timing);

        // Invariant culture throughout: a key computed on a machine with a
        // different locale must be byte-identical, or the uniqueness guarantee
        // evaporates.
        var occurrencePart = rentOccurrenceId?.ToString("N", CultureInfo.InvariantCulture)
                             ?? "lease";

        var key = string.Join(':',
            KeyFormatVersion,
            notificationRuleId.ToString("N", CultureInfo.InvariantCulture),
            EventTypeToken(eventType),
            eventLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            occurrencePart,
            Fingerprint(timing));

        // The column accepts 16 to 200 characters. The format above is fixed
        // width and well inside that, but assert rather than discover it as a
        // constraint violation during a generation run.
        if (key.Length is < 16 or > 200)
        {
            throw new InvalidOperationException(
                $"Generated idempotency key has invalid length {key.Length}: '{key}'.");
        }

        return key;
    }

    /// <summary>
    /// Hashes the timing fields into a short, stable fingerprint.
    ///
    /// <para>
    /// SHA-256 is used for determinism, not secrecy: the key is not a secret
    /// and is not required to be unguessable. What matters is that the same
    /// timing always produces the same string on every machine and across
    /// process restarts, which rules out <see cref="object.GetHashCode"/>.
    /// </para>
    /// </summary>
    private static string Fingerprint(NotificationTiming timing)
    {
        var canonical = string.Join('|',
            timing.TriggerType.ToString(),
            timing.OffsetValue.ToString(CultureInfo.InvariantCulture),
            timing.OffsetUnit.ToString(),
            timing.SendLocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            timing.TimeZoneId);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(hash)[..FingerprintLength];
    }

    private static string EventTypeToken(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.RentDue => "rent_due",
        NotificationEventType.GracePeriodEnd => "grace_period_end",
        NotificationEventType.LateFeeTrigger => "late_fee_trigger",
        NotificationEventType.LeaseExpiration => "lease_expiration",
        NotificationEventType.RenewalNoticeDeadline => "renewal_notice_deadline",
        NotificationEventType.RentIncrease => "rent_increase",
        NotificationEventType.EarlyTerminationDeadline => "early_termination_deadline",
        NotificationEventType.Custom => "custom",

        // An unmapped event must not silently share a token with another one,
        // which would make two different reminders collide and one vanish.
        _ => throw new ArgumentOutOfRangeException(
            nameof(eventType), eventType, "Unmapped notification event type."),
    };
}

/// <summary>
/// The fields a notification's identity depends on: everything that determines
/// WHEN it is sent, and nothing that determines what it says or who receives
/// it.
/// </summary>
public sealed record NotificationTiming
{
    public required NotificationTriggerType TriggerType { get; init; }

    public required int OffsetValue { get; init; }

    public required OffsetUnit OffsetUnit { get; init; }

    public required LocalTime SendLocalTime { get; init; }

    /// <summary>The IANA zone identifier, for example "America/New_York".</summary>
    public required string TimeZoneId { get; init; }
}
