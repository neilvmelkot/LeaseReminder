using FluentValidation;
using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

// =============================================================================
// Maintenance module contracts.
//
// As with the lease contracts, what is absent is as deliberate as what is
// present. A request can never set statusCategory, position, referenceNumber,
// startedAt, or completedAt: those are derived by the database from the column
// a task sits in, so letting a client send them would allow a card shown as
// Completed with no completion date, and every duration metric built on it
// would be quietly wrong.
//
// Moving a card is its own endpoint taking NEIGHBOUR IDS rather than a
// position, because two people dragging at once would both compute the same
// position from the same stale view. Neighbours are stable; positions are not.
// =============================================================================

/* -------------------------------------------------------------------------- */
/* Board                                                                       */
/* -------------------------------------------------------------------------- */

public sealed record BoardColumnResponse(
    Guid Id,
    string Name,
    TaskStatusCategory Category,
    decimal Position,
    string? Colour,
    bool CollapsedByDefault,
    int? WorkInProgressLimit,
    int TaskCount,
    int OverdueCount,
    int UrgentCount,
    IReadOnlyList<TaskCardResponse> Tasks);

/// <summary>
/// One card. Carries only what the board renders, so a portfolio board of
/// several hundred tasks is one query and one modest payload rather than a
/// full task record per card.
/// </summary>
public sealed record TaskCardResponse(
    Guid Id,
    int ReferenceNumber,
    string Title,
    Guid TaskColumnId,
    TaskStatusCategory StatusCategory,
    decimal Position,
    TaskPriority Priority,
    TaskCategory Category,
    Guid PropertyId,
    string PropertyName,
    string? UnitNumber,
    LocalDate? DueDate,

    // Precomputed server-side, in the property own time zone. The browser
    // cannot work this out correctly: it only knows the viewer zone, and a
    // task in Los Angeles is not overdue because it is past midnight in
    // London.
    bool IsOverdue,
    int? DaysUntilDue,

    IReadOnlyList<string> AssigneeNames,
    int CommentCount,
    int AttachmentCount,
    bool HasSubtasks,
    string? VendorName);

public sealed record BoardResponse(
    Guid OrganizationId,
    Guid? PropertyId,
    string? PropertyName,
    int OpenTaskCount,
    int OverdueTaskCount,
    IReadOnlyList<BoardColumnResponse> Columns,
    /// <summary>
    /// Completed or cancelled cards too old to be shown.
    /// </summary>
    /// <remarks>
    /// Returned so the board can say what it is leaving out. Hiding rows
    /// without saying so is how somebody concludes a task was lost.
    /// </remarks>
    int HiddenCompletedCount);

/* -------------------------------------------------------------------------- */
/* Task detail                                                                 */
/* -------------------------------------------------------------------------- */

public sealed record TaskDetailResponse(
    Guid Id,
    int ReferenceNumber,
    string Title,
    string? Description,
    Guid TaskColumnId,
    string ColumnName,
    TaskStatusCategory StatusCategory,
    TaskPriority Priority,
    TaskCategory Category,
    TaskSource Source,
    Guid PropertyId,
    string PropertyName,
    Guid? UnitId,
    string? UnitNumber,
    string? ReportedByTenantName,
    LocalDate? DueDate,
    bool IsOverdue,
    int? DaysUntilDue,
    Instant? StartedAt,
    Instant? CompletedAt,
    decimal? EstimatedCost,
    decimal? ActualCost,
    string Currency,
    Guid? VendorId,
    string? VendorName,
    Guid? ParentTaskId,
    string? ParentTaskTitle,
    int? RecurrenceIntervalDays,
    Instant LastActivityAt,
    Instant CreatedAt,
    IReadOnlyList<TaskAssigneeResponse> Assignees,
    IReadOnlyList<TaskCommentResponse> Comments,
    IReadOnlyList<TaskActivityResponse> Activity,
    IReadOnlyList<TaskSubtaskResponse> Subtasks);

public sealed record TaskAssigneeResponse(
    Guid? UserId,
    Guid? VendorId,
    string DisplayName,
    bool IsLead,
    Instant AssignedAt);

public sealed record TaskCommentResponse(
    Guid Id,
    string AuthorDisplayName,
    string Body,
    bool IsInternal,
    Instant CreatedAt);

public sealed record TaskActivityResponse(
    long Id,
    string ActorDisplayName,
    string ActorKind,
    string ActivityType,
    string Summary,
    Instant CreatedAt);

public sealed record TaskSubtaskResponse(
    Guid Id,
    int ReferenceNumber,
    string Title,
    TaskStatusCategory StatusCategory);

/* -------------------------------------------------------------------------- */
/* Requests                                                                    */
/* -------------------------------------------------------------------------- */

public sealed record CreateTaskRequest(
    Guid PropertyId,
    string Title,
    Guid? UnitId,
    string? Description,
    TaskPriority Priority,
    TaskCategory Category,
    TaskSource Source,
    LocalDate? DueDate,
    Guid? ReportedByTenantId,
    decimal? EstimatedCost,
    Guid? VendorId,
    Guid? ParentTaskId,
    int? RecurrenceIntervalDays);

public sealed class CreateTaskRequestValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskRequestValidator()
    {
        RuleFor(request => request.PropertyId).NotEmpty();

        RuleFor(request => request.Title)
            .NotEmpty().WithMessage("A task needs a title.")
            .MaximumLength(300);

        RuleFor(request => request.Description).MaximumLength(20000);
        RuleFor(request => request.Priority).IsInEnum();
        RuleFor(request => request.Category).IsInEnum();
        RuleFor(request => request.Source).IsInEnum();

        RuleFor(request => request.EstimatedCost)
            .InclusiveBetween(0m, 99_999_999.99m)
            .When(request => request.EstimatedCost.HasValue);

        RuleFor(request => request.RecurrenceIntervalDays)
            .InclusiveBetween(1, 3650)
            .When(request => request.RecurrenceIntervalDays.HasValue)
            .WithMessage("A recurrence interval must be between 1 and 3650 days.");

        // A tenant report that names no tenant loses the one fact that makes
        // the acknowledgement and completion notifications possible.
        RuleFor(request => request.ReportedByTenantId)
            .NotEmpty()
            .When(request => request.Source == TaskSource.TenantReport)
            .WithMessage(
                "A tenant report needs the reporting tenant, so they can be told when " +
                "it is received and when it is finished.");
    }
}

/// <summary>
/// Moving a card.
///
/// <para>
/// The two neighbour ids, not a position. The client knows which cards the
/// drop landed between; it does not reliably know their positions, because
/// somebody else may have reordered the column since the board was loaded.
/// Sending neighbours lets the server recompute from current truth, so two
/// simultaneous drags merge instead of colliding.
/// </para>
/// </summary>
public sealed record MoveTaskRequest(
    Guid TargetColumnId,
    Guid? PreviousTaskId,
    Guid? NextTaskId);

public sealed class MoveTaskRequestValidator : AbstractValidator<MoveTaskRequest>
{
    public MoveTaskRequestValidator()
    {
        RuleFor(request => request.TargetColumnId).NotEmpty();

        // The same card cannot be both neighbours, which would mean the client
        // sent a nonsensical drop target.
        RuleFor(request => request)
            .Must(request =>
                request.PreviousTaskId is null
                || request.NextTaskId is null
                || request.PreviousTaskId != request.NextTaskId)
            .WithMessage("The two neighbouring cards cannot be the same card.");
    }
}

public sealed record AssignTaskRequest(Guid? UserId, Guid? VendorId, bool IsLead);

public sealed class AssignTaskRequestValidator : AbstractValidator<AssignTaskRequest>
{
    public AssignTaskRequestValidator()
    {
        // Exactly one. A task is owned either by a member of the workspace or
        // by an external vendor, and the database enforces the same.
        RuleFor(request => request)
            .Must(request => (request.UserId is null) != (request.VendorId is null))
            .WithMessage("Assign to exactly one of a workspace member or a vendor.");
    }
}

public sealed record AddTaskCommentRequest(string Body, bool IsInternal);

public sealed class AddTaskCommentRequestValidator : AbstractValidator<AddTaskCommentRequest>
{
    public AddTaskCommentRequestValidator()
    {
        RuleFor(request => request.Body)
            .NotEmpty().WithMessage("A comment cannot be empty.")
            .MaximumLength(10000);
    }
}

public sealed record UpdateTaskRequest(
    string? Title,
    string? Description,
    TaskPriority? Priority,
    TaskCategory? Category,
    LocalDate? DueDate,

    // Explicit, because null cannot distinguish "leave the due date alone"
    // from "remove it".
    bool ClearDueDate,

    decimal? EstimatedCost,
    decimal? ActualCost,
    Guid? VendorId);

public sealed class UpdateTaskRequestValidator : AbstractValidator<UpdateTaskRequest>
{
    public UpdateTaskRequestValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty().MaximumLength(300)
            .When(request => request.Title is not null);

        RuleFor(request => request.Description).MaximumLength(20000);

        RuleFor(request => request.Priority).IsInEnum()
            .When(request => request.Priority.HasValue);

        RuleFor(request => request.Category).IsInEnum()
            .When(request => request.Category.HasValue);

        RuleFor(request => request.EstimatedCost)
            .InclusiveBetween(0m, 99_999_999.99m)
            .When(request => request.EstimatedCost.HasValue);

        RuleFor(request => request.ActualCost)
            .InclusiveBetween(0m, 99_999_999.99m)
            .When(request => request.ActualCost.HasValue);

        RuleFor(request => request)
            .Must(request => !(request.ClearDueDate && request.DueDate.HasValue))
            .WithMessage("Choose one: clear the due date, or set it.");
    }
}

/* -------------------------------------------------------------------------- */
/* Vendors                                                                     */
/* -------------------------------------------------------------------------- */

public sealed record VendorResponse(
    Guid Id,
    string Name,
    string? Company,
    string? Email,
    string? Phone,
    IReadOnlyList<TaskCategory> Trades,
    decimal? HourlyRate,
    string Currency,
    bool Active,
    LocalDate? InsuranceExpiresOn,
    bool InsuranceExpiringSoon,
    int OpenTaskCount);

public sealed record UpsertVendorRequest(
    string Name,
    string? Company,
    string? Email,
    string? Phone,
    TaskCategory[]? Trades,
    decimal? HourlyRate,
    LocalDate? InsuranceExpiresOn,
    string? Notes);

public sealed class UpsertVendorRequestValidator : AbstractValidator<UpsertVendorRequest>
{
    public UpsertVendorRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("A vendor needs a name.")
            .MaximumLength(200);

        RuleFor(request => request.Company).MaximumLength(200);

        RuleFor(request => request.Email)
            .EmailAddress().MaximumLength(320)
            .When(request => !string.IsNullOrEmpty(request.Email));

        RuleFor(request => request.Phone).MaximumLength(40);

        RuleFor(request => request.HourlyRate)
            .InclusiveBetween(0m, 99_999_999.99m)
            .When(request => request.HourlyRate.HasValue);

        RuleFor(request => request.Notes).MaximumLength(10000);
    }
}

/* -------------------------------------------------------------------------- */
/* Notification rules                                                          */
/* -------------------------------------------------------------------------- */

public sealed record TaskNotificationRuleResponse(
    Guid Id,
    Guid? PropertyId,
    string Name,
    TaskRuleTrigger TriggerKind,
    TaskEventType? EventType,
    TaskScheduleBasis? ScheduleBasis,
    int? ScheduleOffsetDays,
    bool RepeatDaily,
    int? MaxRepeats,
    IReadOnlyList<TaskPriority>? OnlyPriorities,
    IReadOnlyList<DeliveryChannel> Channels,
    LocalTime? SendLocalTime,
    bool Enabled,
    Instant? PausedAt,
    string? PausedReason,

    // A one-line restatement of the rule, so the settings screen does not
    // make the reader assemble trigger, offset, and recipients themselves.
    string Summary,

    IReadOnlyList<TaskRecipientRole> RecipientRoles);

public sealed record ToggleTaskNotificationRuleRequest(bool Enabled);

/* -------------------------------------------------------------------------- */
/* Notification centre                                                         */
/* -------------------------------------------------------------------------- */

public sealed record NotificationResponse(
    Guid Id,
    string SubjectType,
    Guid? SubjectId,
    string Title,
    string Body,
    string? LinkPath,
    TaskRecipientRole? RecipientRole,
    Instant? ReadAt,
    Instant CreatedAt);

public sealed record NotificationListResponse(
    int UnreadCount,
    IReadOnlyList<NotificationResponse> Notifications);

public sealed record MarkNotificationsReadRequest(Guid[]? NotificationIds);
