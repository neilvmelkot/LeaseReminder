using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// The unified notification centre.
///
/// <para>
/// Rent reminders, lease expiries, and maintenance updates in one place, with
/// unread state and a link to the record. The reason it can be unified at all
/// is that notification_deliveries holds every channel in one table: an in-app
/// notification is a delivery with channel = in_app, so the centre is a filter
/// rather than a union across three schemas.
/// </para>
///
/// <para>
/// <b>None of these endpoints takes a user id.</b> Each calls a database
/// function that reads the caller's identity itself, so there is no parameter
/// through which one person could request, clear, or dismiss another's
/// notifications. The RLS policy enforces the same thing a second time. Taking
/// a user id and checking it would be the version of this that eventually ships
/// a bug.
/// </para>
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/notifications");

        // ---------------------------------------------------------------------
        // GET /notifications
        // ---------------------------------------------------------------------
        group.MapGet("/", async (
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            bool includeRead = false,
            int limit = 50) =>
        {
            // The two reads are independent, but issuing them in sequence on
            // one connection is still cheaper than the round trip saved by
            // parallelising them on two.
            var unreadCount = await dbContext.Database
                .SqlQuery<int>($"SELECT app.unread_notification_count() AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            var rows = await dbContext.Database
                .SqlQuery<NotificationRow>($"""
                    SELECT id            AS "Id",
                           subject_type  AS "SubjectType",
                           subject_id    AS "SubjectId",
                           title         AS "Title",
                           body          AS "Body",
                           link_path     AS "LinkPath",
                           recipient_role::text AS "RecipientRole",
                           read_at       AS "ReadAt",
                           created_at    AS "CreatedAt"
                    FROM app.list_notifications({includeRead}, {limit}, NULL)
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new NotificationListResponse(
                unreadCount,
                rows.Select(row => new NotificationResponse(
                    row.Id,
                    row.SubjectType,
                    row.SubjectId,
                    row.Title,
                    row.Body,
                    row.LinkPath,
                    ParseRecipientRole(row.RecipientRole),
                    row.ReadAt,
                    row.CreatedAt))
                    .ToList()));
        })
            .WithName("ListNotifications")
            .WithSummary("The caller's own notifications. Takes no user id, by design.");

        // ---------------------------------------------------------------------
        // GET /notifications/unread-count
        //
        // Its own endpoint because the badge is polled far more often than the
        // list is opened, and a count is one integer rather than fifty rows.
        // ---------------------------------------------------------------------
        group.MapGet("/unread-count", async (
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var count = await dbContext.Database
                .SqlQuery<int>($"SELECT app.unread_notification_count() AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { unreadCount = count });
        })
            .WithName("GetUnreadNotificationCount");

        // ---------------------------------------------------------------------
        // POST /notifications/read
        //
        // A null id list marks everything read, which is the "mark all" button.
        // Passing somebody else's id affects nothing: the function pins the
        // update to the caller rather than trusting the id.
        // ---------------------------------------------------------------------
        group.MapPost("/read", async (
            MarkNotificationsReadRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request.NotificationIds is { Length: > 500 })
            {
                return Results.Problem(
                    title: "Too many notifications",
                    detail: "Mark at most 500 at a time, or omit the list to mark all read.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var updated = await dbContext.Database
                .SqlQuery<int>(
                    $"SELECT app.mark_notifications_read({request.NotificationIds}) AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            // The count is returned rather than a bare 204, so a client that
            // passed an id it does not own can tell that nothing happened.
            return Results.Ok(new { markedRead = updated });
        })
            .WithName("MarkNotificationsRead");

        // ---------------------------------------------------------------------
        // POST /notifications/{id}/dismiss
        //
        // Dismissing clears it from the list but keeps it in history, so
        // "was this person told?" still has an answer.
        // ---------------------------------------------------------------------
        group.MapPost("/{notificationId:guid}/dismiss", async (
            Guid notificationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var dismissed = await dbContext.Database
                .SqlQuery<bool>(
                    $"SELECT app.dismiss_notification({notificationId}) AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            // 404 rather than 403 for a notification belonging to somebody
            // else. A 403 would confirm that the id exists, which is more than
            // the caller is entitled to know.
            return dismissed
                ? Results.NoContent()
                : EndpointSupport.NotFound("notification");
        })
            .WithName("DismissNotification");

        return routes;
    }

    private sealed record NotificationRow(
        Guid Id,
        string SubjectType,
        Guid? SubjectId,
        string Title,
        string Body,
        string? LinkPath,
        string? RecipientRole,
        Instant? ReadAt,
        Instant CreatedAt);

    private static TaskRecipientRole? ParseRecipientRole(string? label) => label switch
    {
        "assignee" => TaskRecipientRole.Assignee,
        "task_creator" => TaskRecipientRole.TaskCreator,
        "reporting_tenant" => TaskRecipientRole.ReportingTenant,
        "property_manager" => TaskRecipientRole.PropertyManager,
        "property_owner" => TaskRecipientRole.PropertyOwner,
        "vendor" => TaskRecipientRole.Vendor,
        "custom_recipient" => TaskRecipientRole.CustomRecipient,
        "recipient_group" => TaskRecipientRole.RecipientGroup,
        _ => null,
    };
}
