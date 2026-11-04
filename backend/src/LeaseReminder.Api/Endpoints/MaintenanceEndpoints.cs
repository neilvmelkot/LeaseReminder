using FluentValidation;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// The maintenance board.
///
/// <para>
/// Every mutation goes through a SECURITY DEFINER function in
/// db/migrations/0015 rather than through the change tracker. That is not
/// ceremony: a task change has to commit together with its timeline entry and
/// its outbox event, and the change tracker cannot express "these three rows
/// or none". Updating <c>tasks</c> directly here would make it possible to
/// move a card without raising the event, which is the exact failure the
/// outbox exists to prevent.
/// </para>
///
/// <para>
/// Reads use EF, filtered by Row Level Security like everything else.
/// </para>
/// </summary>
public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/maintenance");

        MapBoard(group);
        MapTaskCommands(group);
        MapVendors(group);
        MapNotificationRules(group);

        return routes;
    }

    // =========================================================================
    // Board
    // =========================================================================

    /// <summary>
    /// Calls one of the task lifecycle functions and maps its refusals.
    /// </summary>
    private static async Task<IResult> RunTaskLifecycleAsync(
        AppDbContext dbContext,
        FormattableString command,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(command, cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return EndpointSupport.Forbidden(exception.MessageText);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.NoDataFound)
        {
            return EndpointSupport.NotFound("task");
        }
    }

    /// <summary>
    /// How long a completed or cancelled card stays on the board.
    /// </summary>
    /// <remarks>
    /// Two weeks. "Did we deal with that?" is a question about last week, so
    /// the board should still answer it; beyond that the card is history and
    /// belongs behind a filter rather than in a column competing for width.
    /// </remarks>
    private const int SettledCardWindowDays = 14;

    private static void MapBoard(RouteGroupBuilder group)
    {
        group.MapGet("/board", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            Guid? propertyId,
            TaskPriority? priority,
            TaskCategory? category,
            Guid? assigneeUserId,
            bool includeCompleted = true,
            // Lifts the recency window, for "show me everything we have ever
            // finished". Opt-in, because it is the behaviour that made the
            // board unusable.
            bool includeAllCompleted = false,
            // For the archive view. Off by default: the board is for work
            // somebody might do.
            bool includeArchived = false,
            // The archive view itself, which wants the archived tasks and
            // only those. Distinct from includeArchived, which widens the
            // board rather than replacing it.
            bool archivedOnly = false) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var columns = await dbContext.TaskColumns
                .AsNoTracking()
                .Where(column => column.OrganizationId == organizationId)
                .OrderBy(column => column.Position)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (columns.Count == 0)
            {
                return Results.Problem(
                    title: "Board not set up",
                    detail: "This organization has no board columns. Seed the board first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var query = dbContext.Tasks
                .AsNoTracking()
                .Where(task => task.OrganizationId == organizationId);

            if (propertyId.HasValue)
            {
                query = query.Where(task => task.PropertyId == propertyId.Value);
            }

            if (priority.HasValue)
            {
                query = query.Where(task => task.Priority == priority.Value);
            }

            if (category.HasValue)
            {
                query = query.Where(task => task.Category == category.Value);
            }

            if (assigneeUserId.HasValue)
            {
                query = query.Where(task => dbContext.TaskAssignees
                    .Any(assignee => assignee.TaskId == task.Id
                                     && assignee.UserId == assigneeUserId.Value));
            }

            // Archived tasks are off the board entirely. Unlike the settled
            // window below, this is not a recency rule — somebody decided the
            // task does not belong here.
            query = archivedOnly
                ? query.Where(task => task.ArchivedAt != null)
                : includeArchived
                    ? query
                    : query.Where(task => task.ArchivedAt == null);

            // Counted before the window is applied, so the board can report
            // what it is not showing rather than hiding it silently.
            var settledCutoff = SystemClock.Instance.GetCurrentInstant()
                - Duration.FromDays(SettledCardWindowDays);

            var hiddenCompletedCount = includeCompleted && !includeAllCompleted
                ? await query
                    .Where(task =>
                        (task.StatusCategory == TaskStatusCategory.Completed
                         || task.StatusCategory == TaskStatusCategory.Cancelled)
                        && task.CompletedAt != null
                        && task.CompletedAt < settledCutoff)
                    .CountAsync(cancellationToken)
                    .ConfigureAwait(false)
                : 0;

            if (includeCompleted && !includeAllCompleted)
            {
                // A settled card with no completed_at is kept rather than
                // hidden: an unknown completion date is not evidence that it
                // was long ago, and dropping it would lose a card for good.
                query = query.Where(task =>
                    (task.StatusCategory != TaskStatusCategory.Completed
                     && task.StatusCategory != TaskStatusCategory.Cancelled)
                    || task.CompletedAt == null
                    || task.CompletedAt >= settledCutoff);
            }

            if (!includeCompleted)
            {
                query = query.Where(task =>
                    task.StatusCategory != TaskStatusCategory.Completed
                    && task.StatusCategory != TaskStatusCategory.Cancelled);
            }

            // One projection for the whole board. The alternative, a query per
            // column, is five to eight round trips before anything renders.
            var cards = await query
                .OrderBy(task => task.Position)
                .Select(task => new
                {
                    task.Id,
                    task.ReferenceNumber,
                    task.Title,
                    task.TaskColumnId,
                    task.StatusCategory,
                    task.Position,
                    task.Priority,
                    task.Category,
                    task.PropertyId,
                    PropertyName = task.Property!.Name,
                    PropertyTimeZone = task.Property.TimeZone,
                    UnitNumber = task.Unit != null ? task.Unit.UnitNumber : null,
                    task.DueDate,
                    VendorName = task.Vendor != null
                        ? (task.Vendor.Company ?? task.Vendor.Name)
                        : null,

                    CommentCount = dbContext.TaskComments
                        .Count(comment => comment.TaskId == task.Id),

                    HasSubtasks = dbContext.Tasks
                        .Any(child => child.ParentTaskId == task.Id),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Assignees, as one query for the whole board rather than a
            // subquery inside the projection above.
            //
            // A row names a workspace member or a vendor and never both, so
            // the name is resolved by two independent subqueries. Joining to
            // users and vendors together is what broke this before: both
            // joins were mandatory, no row can satisfy both, and every task
            // on the board read "Unassigned" while its activity trail
            // correctly named the person it was assigned to.
            var taskIds = cards.Select(card => card.Id).ToList();

            var assigneeNamesByTask = (await dbContext.TaskAssignees
                    .Where(assignee => taskIds.Contains(assignee.TaskId))
                    .Select(assignee => new
                    {
                        assignee.TaskId,
                        Name = assignee.UserId != null
                            ? dbContext.Users
                                .Where(user => user.Id == assignee.UserId)
                                .Select(user => user.DisplayName ?? user.Email)
                                .FirstOrDefault()
                            : dbContext.Vendors
                                .Where(vendor => vendor.Id == assignee.VendorId)
                                .Select(vendor => vendor.Company ?? vendor.Name)
                                .FirstOrDefault(),
                    })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                // A name resolves to null only when the person's row is not
                // readable -- RLS hiding a user outside the caller's
                // workspace, say. Dropping them is right: a card cannot name
                // somebody the caller is not allowed to see.
                .Where(row => row.Name is not null)
                .GroupBy(row => row.TaskId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Select(row => row.Name!)
                        .ToList());

            // Overdue is computed here, server-side, in each property's own
            // zone. The browser cannot do this: it knows only the viewer's
            // zone, and a task in Los Angeles is not overdue because it has
            // gone midnight in London.
            var now = SystemClock.Instance.GetCurrentInstant();

            var projected = cards.Select(card =>
            {
                var today = LocalDateInZone(now, card.PropertyTimeZone);

                var daysUntilDue = card.DueDate.HasValue
                    ? Period.Between(today, card.DueDate.Value, PeriodUnits.Days).Days
                    : (int?)null;

                var isOverdue =
                    card.DueDate.HasValue
                    && card.DueDate.Value < today
                    && card.StatusCategory is not (TaskStatusCategory.Completed
                        or TaskStatusCategory.Cancelled);

                return new TaskCardResponse(
                    card.Id,
                    card.ReferenceNumber,
                    card.Title,
                    card.TaskColumnId,
                    card.StatusCategory,
                    card.Position,
                    card.Priority,
                    card.Category,
                    card.PropertyId,
                    card.PropertyName,
                    card.UnitNumber,
                    card.DueDate,
                    isOverdue,
                    daysUntilDue,
                    assigneeNamesByTask.TryGetValue(card.Id, out var names)
                        ? names
                        : [],
                    card.CommentCount,
                    // Attachment counts are a phase 2 concern; the shape is
                    // here so the card does not have to change later.
                    0,
                    card.HasSubtasks,
                    card.VendorName);
            }).ToList();

            var byColumn = projected
                .GroupBy(card => card.TaskColumnId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var columnResponses = columns.Select(column =>
            {
                var columnCards = byColumn.TryGetValue(column.Id, out var found)
                    ? found
                    : [];

                return new BoardColumnResponse(
                    column.Id,
                    column.Name,
                    column.Category,
                    column.Position,
                    column.Colour,
                    column.CollapsedByDefault,
                    column.WorkInProgressLimit,
                    columnCards.Count,
                    columnCards.Count(card => card.IsOverdue),
                    columnCards.Count(card => card.Priority == TaskPriority.Urgent),
                    columnCards);
            }).ToList();

            string? propertyName = null;

            if (propertyId.HasValue)
            {
                propertyName = await dbContext.Properties
                    .AsNoTracking()
                    .Where(property => property.Id == propertyId.Value)
                    .Select(property => property.Name)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.Ok(new BoardResponse(
                organizationId,
                propertyId,
                propertyName,
                projected.Count(card =>
                    card.StatusCategory is not (TaskStatusCategory.Completed
                        or TaskStatusCategory.Cancelled)),
                projected.Count(card => card.IsOverdue),
                columnResponses,
                hiddenCompletedCount));
        })
            .WithName("GetMaintenanceBoard")
            .WithSummary("The Kanban board, portfolio-wide or for one property.");

        // ---------------------------------------------------------------------
        // Task detail
        // ---------------------------------------------------------------------
        group.MapGet("/tasks/{taskId:guid}", async (
            Guid organizationId,
            Guid taskId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var task = await dbContext.Tasks
                .AsNoTracking()
                .Include(candidate => candidate.Property)
                .Include(candidate => candidate.Unit)
                .Include(candidate => candidate.Column)
                .Include(candidate => candidate.Vendor)
                .Include(candidate => candidate.ReportedByTenant)
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == taskId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (task is null)
            {
                return EndpointSupport.NotFound("task");
            }

            // Same correlated-subquery shape as the board. Include-ing both
            // navigations produced INNER JOINs to users and vendors at once,
            // which no row can satisfy, so this list was always empty however
            // many people were actually on the task.
            var assignees = await dbContext.TaskAssignees
                .Where(assignee => assignee.TaskId == taskId)
                .OrderByDescending(assignee => assignee.IsLead)
                .Select(assignee => new TaskAssigneeResponse(
                    assignee.UserId,
                    assignee.VendorId,
                    assignee.UserId != null
                        ? dbContext.Users
                            .Where(user => user.Id == assignee.UserId)
                            .Select(user => user.DisplayName ?? user.Email)
                            .FirstOrDefault()!
                        : dbContext.Vendors
                            .Where(vendor => vendor.Id == assignee.VendorId)
                            .Select(vendor => vendor.Company ?? vendor.Name)
                            .FirstOrDefault()!,
                    assignee.IsLead,
                    assignee.AssignedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var comments = await dbContext.TaskComments
                .AsNoTracking()
                .Where(comment => comment.TaskId == taskId)
                .OrderBy(comment => comment.CreatedAt)
                .Select(comment => new TaskCommentResponse(
                    comment.Id,
                    comment.AuthorDisplayName,
                    comment.Body,
                    comment.IsInternal,
                    comment.CreatedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var activity = await dbContext.TaskActivity
                .AsNoTracking()
                .Where(entry => entry.TaskId == taskId)
                .OrderByDescending(entry => entry.CreatedAt)
                // Bounded: a long-running task accumulates hundreds of entries
                // and the card only shows a timeline, not an archive.
                .Take(100)
                .Select(entry => new TaskActivityResponse(
                    entry.Id,
                    entry.ActorDisplayName,
                    entry.ActorKind,
                    entry.ActivityType,
                    entry.Summary,
                    entry.CreatedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var subtasks = await dbContext.Tasks
                .AsNoTracking()
                .Where(child => child.ParentTaskId == taskId)
                .OrderBy(child => child.ReferenceNumber)
                .Select(child => new TaskSubtaskResponse(
                    child.Id, child.ReferenceNumber, child.Title, child.StatusCategory))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            string? parentTitle = null;

            if (task.ParentTaskId.HasValue)
            {
                parentTitle = await dbContext.Tasks
                    .AsNoTracking()
                    .Where(parent => parent.Id == task.ParentTaskId.Value)
                    .Select(parent => parent.Title)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var today = LocalDateInZone(
                SystemClock.Instance.GetCurrentInstant(),
                task.Property?.TimeZone ?? "UTC");

            var daysUntilDue = task.DueDate.HasValue
                ? Period.Between(today, task.DueDate.Value, PeriodUnits.Days).Days
                : (int?)null;

            return Results.Ok(new TaskDetailResponse(
                task.Id,
                task.ReferenceNumber,
                task.Title,
                task.Description,
                task.TaskColumnId,
                task.Column?.Name ?? "Unknown",
                task.StatusCategory,
                task.Priority,
                task.Category,
                task.Source,
                task.PropertyId,
                task.Property?.Name ?? "Unknown",
                task.UnitId,
                task.Unit?.UnitNumber,
                task.ReportedByTenant is null
                    ? null
                    : (task.ReportedByTenant.PreferredName ?? task.ReportedByTenant.LegalName),
                task.DueDate,
                task.DueDate.HasValue && task.DueDate.Value < today && task.IsOpen,
                daysUntilDue,
                task.StartedAt,
                task.CompletedAt,
                task.EstimatedCost,
                task.ActualCost,
                task.Currency,
                task.VendorId,
                task.Vendor is null ? null : (task.Vendor.Company ?? task.Vendor.Name),
                task.ParentTaskId,
                parentTitle,
                task.RecurrenceIntervalDays,
                task.LastActivityAt,
                task.CreatedAt,
                assignees,
                comments,
                activity,
                subtasks));
        })
            .WithName("GetMaintenanceTask");
    }

    // =========================================================================
    // Commands
    //
    // Each one calls the matching database function, so the change, the
    // timeline entry, and the event share a transaction.
    // =========================================================================

    private static void MapTaskCommands(RouteGroupBuilder group)
    {
        group.MapPost("/tasks", async (
            Guid organizationId,
            CreateTaskRequest request,
            IValidator<CreateTaskRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot create tasks here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // The function validates the property, lands the task in the
            // intake column, writes the timeline entry, and raises the created
            // event, all in one transaction.
            var taskId = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT (app.create_task(
                      {organizationId},
                      {request.PropertyId},
                      {request.Title},
                      {actor.Id},
                      {actor.DisplayName},
                      {request.UnitId},
                      {request.Description},
                      {request.Priority},
                      {request.Category},
                      {request.Source},
                      {request.DueDate},
                      {request.ReportedByTenantId},
                      {request.EstimatedCost},
                      {request.VendorId},
                      {request.ParentTaskId},
                      {request.RecurrenceIntervalDays},
                      NULL
                    )).id AS "Value"
                    """)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/maintenance/tasks/{taskId}",
                new { id = taskId });
        })
            .WithName("CreateMaintenanceTask");

        // ---------------------------------------------------------------------
        // Move: the drag-and-drop operation
        // ---------------------------------------------------------------------
        group.MapPost("/tasks/{taskId:guid}/move", async (
            Guid organizationId,
            Guid taskId,
            MoveTaskRequest request,
            IValidator<MoveTaskRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot move tasks here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                // Neighbour ids, not a position: the function recomputes from
                // current truth, so two simultaneous drags merge rather than
                // both writing the same midpoint.
                var newPosition = await dbContext.Database
                    .SqlQuery<decimal>($"""
                        SELECT (app.move_task(
                          {taskId},
                          {request.TargetColumnId},
                          {request.PreviousTaskId},
                          {request.NextTaskId},
                          {actor.Id},
                          {actor.DisplayName}
                        )).position AS "Value"
                        """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new { taskId, position = newPosition });
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden("You cannot move this task.");
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("task");
            }
        })
            .WithName("MoveMaintenanceTask")
            .WithSummary("Moves a card. Reordering within a column raises no notification.");

        // ---------------------------------------------------------------------
        // Assign
        // ---------------------------------------------------------------------
        group.MapPost("/tasks/{taskId:guid}/assign", async (
            Guid organizationId,
            Guid taskId,
            AssignTaskRequest request,
            IValidator<AssignTaskRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot assign tasks here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"""
                         SELECT app.assign_task(
                           {taskId}, {request.UserId}, {request.VendorId},
                           {request.IsLead}, {actor.Id}, {actor.DisplayName})
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden("You cannot assign this task.");
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("task or assignee");
            }
        })
            .WithName("AssignMaintenanceTask");

        // ---------------------------------------------------------------------
        // Comment
        //
        // Open to any member, including read-only. A read-only role is about
        // not changing the portfolio, not about being unable to say "the
        // contractor cannot get access on Tuesday".
        // ---------------------------------------------------------------------
        // Archiving withdraws a task from the board and keeps everything it
        // carries — the comment thread, the activity trail, the costs. There
        // is deliberately no delete: nothing needs one, and offering it would
        // make a comment thread destructible by a misclick.
        group.MapPost("/tasks/{taskId:guid}/archive", async (
            Guid taskId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunTaskLifecycleAsync(
                dbContext,
                $"SELECT app.archive_task({taskId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("ArchiveTask");

        group.MapPost("/tasks/{taskId:guid}/restore", async (
            Guid taskId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunTaskLifecycleAsync(
                dbContext,
                $"SELECT app.restore_task({taskId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("RestoreTask");

        // Permanent deletion, and the only destructive operation on the
        // board.
        //
        // app.delete_task refuses a task that is not already archived, so
        // there is no path from the board straight to gone — archiving is the
        // reversible, visible first step, and this is only offered from the
        // archive. That rule lives in the function rather than here, so it
        // holds for a direct call as well as for the button.
        //
        // It is administrator-only for the same reason, and the audit entry
        // it writes outlives the row.
        group.MapDelete("/tasks/{taskId:guid}", async (
            Guid taskId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunTaskLifecycleAsync(
                dbContext,
                $"SELECT app.delete_task({taskId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("DeleteTask");

        group.MapPost("/tasks/{taskId:guid}/comments", async (
            Guid organizationId,
            Guid taskId,
            AddTaskCommentRequest request,
            IValidator<AddTaskCommentRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var commentId = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT (app.add_task_comment(
                      {taskId}, {request.Body}, {request.IsInternal},
                      {actor.Id}, {actor.DisplayName}
                    )).id AS "Value"
                    """)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/maintenance/tasks/{taskId}",
                new { id = commentId });
        })
            .WithName("AddMaintenanceTaskComment");

        // ---------------------------------------------------------------------
        // Edit
        // ---------------------------------------------------------------------
        group.MapPatch("/tasks/{taskId:guid}", async (
            Guid organizationId,
            Guid taskId,
            UpdateTaskRequest request,
            IValidator<UpdateTaskRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot edit tasks here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                // The function raises a SPECIFIC event per meaningful change
                // and none for a typo fix, so a subscriber does not have to
                // diff a generic payload to find out whether it cared.
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"""
                         SELECT app.update_task_fields(
                           {taskId}, {actor.Id}, {actor.DisplayName},
                           {request.Title}, {request.Description},
                           {request.Priority}, {request.Category},
                           {request.DueDate}, {request.ClearDueDate},
                           {request.EstimatedCost}, {request.ActualCost},
                           {request.VendorId})
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden("You cannot edit this task.");
            }
            catch (Npgsql.PostgresException exception)
                when (exception.SqlState == Npgsql.PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("task");
            }
        })
            .WithName("UpdateMaintenanceTask");
    }

    // =========================================================================
    // Vendors
    // =========================================================================

    private static void MapVendors(RouteGroupBuilder group)
    {
        group.MapGet("/vendors", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            bool includeInactive = false) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var today = LocalDateInZone(SystemClock.Instance.GetCurrentInstant(), "UTC");
            var soon = today.PlusDays(30);

            var vendors = await dbContext.Vendors
                .AsNoTracking()
                .Where(vendor => vendor.OrganizationId == organizationId
                                 && (includeInactive || vendor.Active))
                .OrderBy(vendor => vendor.Company ?? vendor.Name)
                .Select(vendor => new VendorResponse(
                    vendor.Id,
                    vendor.Name,
                    vendor.Company,
                    vendor.Email,
                    vendor.Phone,
                    vendor.Trades,
                    vendor.HourlyRate,
                    vendor.Currency,
                    vendor.Active,
                    vendor.InsuranceExpiresOn,

                    // Surfaced rather than buried in a notes field: sending an
                    // uninsured contractor to a property is a liability
                    // problem, not an administrative one.
                    vendor.InsuranceExpiresOn != null
                        && vendor.InsuranceExpiresOn <= soon,

                    dbContext.Tasks.Count(task =>
                        task.VendorId == vendor.Id
                        && task.StatusCategory != TaskStatusCategory.Completed
                        && task.StatusCategory != TaskStatusCategory.Cancelled)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(vendors);
        })
            .WithName("ListVendors");

        group.MapPost("/vendors", async (
            Guid organizationId,
            UpsertVendorRequest request,
            IValidator<UpsertVendorRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot add vendors here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var vendor = new Vendor
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Name = request.Name.Trim(),
                Company = request.Company?.Trim(),
                Email = request.Email?.Trim(),
                Phone = request.Phone?.Trim(),
                Trades = request.Trades ?? [],
                HourlyRate = request.HourlyRate,
                InsuranceExpiresOn = request.InsuranceExpiresOn,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Vendors.Add(vendor);

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is not null)
            {
                EndpointSupport.Audit(
                    dbContext, organizationId, actor.Id, identity.CorrelationId,
                    "vendor.created", "vendor", vendor.Id);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/maintenance/vendors/{vendor.Id}",
                new { id = vendor.Id });
        })
            .WithName("CreateVendor");
    }

    // =========================================================================
    // Notification rules
    // =========================================================================

    private static void MapNotificationRules(RouteGroupBuilder group)
    {
        group.MapGet("/notification-rules", async (
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

            var rules = await dbContext.TaskNotificationRules
                .AsNoTracking()
                .Where(rule => rule.OrganizationId == organizationId)
                .OrderBy(rule => rule.TriggerKind)
                .ThenBy(rule => rule.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var ruleIds = rules.Select(rule => rule.Id).ToList();

            // Recipient roles in one query rather than one per rule.
            var roles = await dbContext.Database
                .SqlQuery<RuleRoleRow>($"""
                    SELECT task_notification_rule_id AS "RuleId",
                           recipient_role::text      AS "Role"
                    FROM task_notification_rule_recipients
                    WHERE task_notification_rule_id = ANY({ruleIds})
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var rolesByRule = roles
                .GroupBy(row => row.RuleId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => ParseRecipientRole(row.Role))
                        .Where(role => role.HasValue)
                        .Select(role => role!.Value)
                        .Distinct()
                        .ToList());

            return Results.Ok(rules.Select(rule => new TaskNotificationRuleResponse(
                rule.Id,
                rule.PropertyId,
                rule.Name,
                rule.TriggerKind,
                rule.EventType,
                rule.ScheduleBasis,
                rule.ScheduleOffsetDays,
                rule.RepeatDaily,
                rule.MaxRepeats,
                rule.OnlyPriorities,
                rule.Channels,
                rule.SendLocalTime,
                rule.Enabled,
                rule.PausedAt,
                rule.PausedReason,
                DescribeRule(rule),
                rolesByRule.TryGetValue(rule.Id, out var found) ? found : []))
                .ToList());
        })
            .WithName("ListTaskNotificationRules");

        group.MapPatch("/notification-rules/{ruleId:guid}", async (
            Guid organizationId,
            Guid ruleId,
            ToggleTaskNotificationRuleRequest request,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            // Administrator tier: a notification rule change affects what
            // everybody in the workspace receives.
            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can change notification rules.");
            }

            var rule = await dbContext.TaskNotificationRules
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == ruleId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rule is null)
            {
                return EndpointSupport.NotFound("notification rule");
            }

            rule.Enabled = request.Enabled;

            // Re-enabling clears an automatic pause. Leaving the pause set
            // would mean the toggle said On while the rule still sent nothing.
            if (request.Enabled)
            {
                rule.PausedAt = null;
                rule.PausedReason = null;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is not null)
            {
                EndpointSupport.Audit(
                    dbContext, organizationId, actor.Id, identity.CorrelationId,
                    request.Enabled
                        ? "task_notification_rule.enabled"
                        : "task_notification_rule.disabled",
                    "task_notification_rule", ruleId,
                    EndpointSupport.AuditMetadata(new { name = rule.Name }));
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("ToggleTaskNotificationRule");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private sealed record RuleRoleRow(Guid RuleId, string Role);

    private static TaskRecipientRole? ParseRecipientRole(string label) => label switch
    {
        "assignee" => TaskRecipientRole.Assignee,
        "task_creator" => TaskRecipientRole.TaskCreator,
        "reporting_tenant" => TaskRecipientRole.ReportingTenant,
        "property_manager" => TaskRecipientRole.PropertyManager,
        "property_owner" => TaskRecipientRole.PropertyOwner,
        "vendor" => TaskRecipientRole.Vendor,
        "custom_recipient" => TaskRecipientRole.CustomRecipient,
        "recipient_group" => TaskRecipientRole.RecipientGroup,

        // An unknown label means the database has a value this build does not
        // know. Dropping it is safer than guessing, and StartupEnumMappingCheck
        // would already have failed the boot if the enum itself had drifted.
        _ => null,
    };

    /// <summary>
    /// One sentence describing a rule.
    ///
    /// <para>
    /// Built server-side so the settings screen does not have to reassemble
    /// trigger, basis, offset, and repeat into prose, and so the wording stays
    /// identical wherever a rule is shown.
    /// </para>
    /// </summary>
    private static string DescribeRule(TaskNotificationRule rule)
    {
        var when = rule.TriggerKind == TaskRuleTrigger.Event
            ? rule.EventType switch
            {
                TaskEventType.TaskCreated => "when a task is created",
                TaskEventType.TaskAssigned => "when a task is assigned",
                TaskEventType.TaskReassigned => "when a task is reassigned",
                TaskEventType.TaskStatusChanged => "when a task changes column",
                TaskEventType.TaskPriorityChanged => "when a task's priority changes",
                TaskEventType.TaskDueDateChanged => "when a task's due date changes",
                TaskEventType.TaskCommentAdded => "when somebody comments",
                TaskEventType.TaskCompleted => "when a task is completed",
                TaskEventType.TaskReopened => "when a task is reopened",
                TaskEventType.TaskCostUpdated => "when a cost is recorded",
                _ => "on a task event",
            }
            : rule.ScheduleBasis switch
            {
                TaskScheduleBasis.BeforeDue =>
                    $"{rule.ScheduleOffsetDays} day(s) before the due date",
                TaskScheduleBasis.OnDue => "on the due date",
                TaskScheduleBasis.AfterDue =>
                    $"{rule.ScheduleOffsetDays} day(s) after the due date",
                TaskScheduleBasis.NoActivity =>
                    $"after {rule.ScheduleOffsetDays} day(s) with no activity",
                _ => "on a schedule",
            };

        var channels = rule.Channels.Length == 2
            ? "by email and in the app"
            : rule.Channels.Contains(DeliveryChannel.Email)
                ? "by email"
                : "in the app";

        var filter = rule.OnlyPriorities is { Length: > 0 }
            ? $", for {string.Join(" and ", rule.OnlyPriorities.Select(p => p.ToString().ToLowerInvariant()))} priority only"
            : string.Empty;

        var repeat = rule.RepeatDaily
            ? $", repeating daily up to {rule.MaxRepeats} times"
            : string.Empty;

        return $"Notify {when} {channels}{filter}{repeat}.";
    }

    /// <summary>
    /// Today's date in a named IANA zone.
    ///
    /// <para>
    /// Used for every overdue and days-until calculation. An unknown zone
    /// falls back to UTC rather than throwing: a board that fails to render
    /// because one property has a bad zone would be a worse outcome than one
    /// card whose overdue flag is a few hours off, and the zone is validated
    /// by a trigger on write anyway.
    /// </para>
    /// </summary>
    private static LocalDate LocalDateInZone(Instant instant, string timeZoneId)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId)
                   ?? DateTimeZone.Utc;

        return instant.InZone(zone).Date;
    }
}
