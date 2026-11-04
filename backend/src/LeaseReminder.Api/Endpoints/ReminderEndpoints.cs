using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Rent reminders, across the whole workspace.
///
/// <para>
/// The per-lease schedule screen answers "when is rent due on this lease".
/// This answers a different question — "what is this workspace about to send,
/// and did the last batch go out" — which is the one somebody asks when they
/// want to trust the system rather than configure it.
/// </para>
///
/// <remarks>
/// <para>
/// Read-only, and deliberately so. Nothing here schedules, cancels, or
/// re-sends anything: generation belongs to the worker, and a reminder that
/// could be conjured from a screen would be a second path around the queue
/// whose idempotency key is what stops a tenant being emailed twice.
/// </para>
/// <para>
/// Every query is a plain SELECT through the caller's own connection, so Row
/// Level Security decides what is visible. Membership is checked first
/// anyway, so a non-member gets a 403 rather than an empty list — those mean
/// different things and a user deserves to know which one they are looking
/// at.
/// </para>
/// </remarks>
/// </summary>
public static class ReminderEndpoints
{
    /// <summary>How far ahead the upcoming list looks.</summary>
    /// <remarks>
    /// Months rather than a row count. A workspace with forty leases
    /// generates forty reminders for one rent day, so "the next 50" could be
    /// a single morning and would look like the schedule stops there.
    /// </remarks>
    private const int UpcomingWindowDays = 90;

    public static IEndpointRouteBuilder MapReminderEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/reminders");

        group.MapGet("/", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            // Upcoming. Ordered by the instant the dispatcher will actually
            // use, not by the local event date, so the list reads in the order
            // things will happen even across properties in different zones.
            var upcoming = await dbContext.Database
                .SqlQuery<UpcomingReminderRow>(
                    $"""
                     SELECT
                       n.id                 AS "Id",
                       n.lease_id           AS "LeaseId",
                       n.event_type::text   AS "EventType",
                       n.event_local_date   AS "EventLocalDate",
                       n.scheduled_at_utc   AS "ScheduledAtUtc",
                       n.local_timezone     AS "LocalTimeZone",
                       n.dst_adjusted       AS "DstAdjusted",
                       n.status::text       AS "Status",
                       p.name               AS "PropertyName",
                       u.unit_number        AS "UnitNumber",
                       o.amount             AS "Amount",
                       o.currency           AS "Currency",
                       o.due_date           AS "DueDate",
                       jsonb_array_length(n.recipients_snapshot) AS "RecipientCount"
                     FROM scheduled_notifications n
                     JOIN leases l     ON l.id = n.lease_id
                     JOIN properties p ON p.id = l.property_id
                     LEFT JOIN units u ON u.id = l.unit_id
                     LEFT JOIN rent_occurrences o ON o.id = n.rent_occurrence_id
                     WHERE n.organization_id = {organizationId}
                       AND n.status = 'scheduled'
                       AND n.scheduled_at_utc < now() + make_interval(days => {UpcomingWindowDays})
                     ORDER BY n.scheduled_at_utc
                     LIMIT 200
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // What has already happened. A failure is kept visible rather than
            // filtered out: "nothing is scheduled" and "the last ten failed"
            // look identical on a screen that only shows what is pending.
            var recent = await dbContext.Database
                .SqlQuery<RecentReminderRow>(
                    $"""
                     SELECT
                       n.id                AS "Id",
                       n.lease_id          AS "LeaseId",
                       n.event_type::text  AS "EventType",
                       n.event_local_date  AS "EventLocalDate",
                       n.status::text      AS "Status",
                       n.sent_at           AS "SentAt",
                       n.attempt_count     AS "AttemptCount",
                       n.last_error_category::text AS "LastErrorCategory",
                       n.last_error_message AS "LastErrorMessage",
                       p.name              AS "PropertyName",
                       u.unit_number       AS "UnitNumber"
                     FROM scheduled_notifications n
                     JOIN leases l     ON l.id = n.lease_id
                     JOIN properties p ON p.id = l.property_id
                     LEFT JOIN units u ON u.id = l.unit_id
                     WHERE n.organization_id = {organizationId}
                       AND n.status <> 'scheduled'
                     ORDER BY coalesce(n.sent_at, n.updated_at) DESC NULLS LAST
                     LIMIT 50
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // The two numbers that decide whether this workspace is actually
            // going to send anything, and the one that says whether it can.
            var summary = await dbContext.Database
                .SqlQuery<ReminderSummaryRow>(
                    $"""
                     SELECT
                       (SELECT count(*) FROM scheduled_notifications n
                         WHERE n.organization_id = {organizationId}
                           AND n.status = 'scheduled')::int AS "ScheduledCount",
                       (SELECT count(*) FROM scheduled_notifications n
                         WHERE n.organization_id = {organizationId}
                           AND n.status = 'failed')::int AS "FailedCount",
                       (SELECT count(*) FROM leases l
                         WHERE l.organization_id = {organizationId}
                           AND l.approval_status = 'approved'
                           AND l.lifecycle_status IN ('active', 'upcoming'))::int
                         AS "LiveLeaseCount",
                       (SELECT count(*) FROM rent_schedules s
                         JOIN leases l ON l.id = s.lease_id
                         WHERE l.organization_id = {organizationId}
                           AND s.active)::int AS "ScheduleCount",
                       EXISTS (SELECT 1 FROM email_accounts a
                         WHERE a.organization_id = {organizationId}
                           AND a.connection_status = 'connected') AS "MailboxConnected"
                     """)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { upcoming, recent, summary });
        })
        .WithName("ListReminders");

        return routes;
    }
}

public sealed record UpcomingReminderRow(
    Guid Id,
    Guid LeaseId,
    string EventType,
    DateOnly EventLocalDate,
    DateTimeOffset ScheduledAtUtc,
    string LocalTimeZone,
    bool DstAdjusted,
    string Status,
    string PropertyName,
    string? UnitNumber,
    decimal? Amount,
    string? Currency,
    DateOnly? DueDate,
    int RecipientCount);

public sealed record RecentReminderRow(
    Guid Id,
    Guid LeaseId,
    string EventType,
    DateOnly EventLocalDate,
    string Status,
    DateTimeOffset? SentAt,
    int AttemptCount,
    string LastErrorCategory,
    string? LastErrorMessage,
    string PropertyName,
    string? UnitNumber);

public sealed record ReminderSummaryRow(
    int ScheduledCount,
    int FailedCount,
    int LiveLeaseCount,
    int ScheduleCount,
    bool MailboxConnected);
