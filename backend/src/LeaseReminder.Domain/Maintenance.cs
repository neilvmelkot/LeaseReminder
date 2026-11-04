using NodaTime;

namespace LeaseReminder.Domain;

// =============================================================================
// The maintenance and operations module.
//
// Enum member names map to the PostgreSQL labels in db/migrations/0013 and
// 0014 through PostgresEnumNameTranslator, so Every30Days becomes
// every_30_days and NoActivity becomes no_activity. StartupEnumMappingCheck
// verifies every pair against the live database at boot, which is what turns a
// forgotten migration into a failed deployment rather than a cast error months
// later.
// =============================================================================

public enum TaskPriority
{
    Urgent,
    High,
    Medium,
    Low,
}

/// <summary>
/// The fixed meaning behind a user-renameable board column.
///
/// <para>
/// This is the single most important type in the module. Users rename columns
/// and add their own, so no rule, metric, or overdue check can be written
/// against a column's name: a board whose last column is called "Done",
/// "Closed", or "Finito" must still stop chasing completed work. Every column
/// declares a category from this set, and all automation reads the category.
/// The name is for people; this is for code.
/// </para>
/// </summary>
public enum TaskStatusCategory
{
    /// <summary>Captured, not yet triaged.</summary>
    Backlog,

    /// <summary>Triaged, awaiting assignment.</summary>
    New,

    /// <summary>Has an owner, not started.</summary>
    Assigned,

    InProgress,

    /// <summary>Waiting on a part, a tenant, or an approval.</summary>
    Blocked,

    /// <summary>Done, awaiting inspection or sign-off.</summary>
    Review,

    Completed,

    /// <summary>Abandoned. Not completed, and must never be counted as such.</summary>
    Cancelled,
}

public enum TaskCategory
{
    Plumbing,
    Electrical,
    Hvac,
    Appliance,
    Structural,
    Roofing,
    Pest,
    Landscaping,
    Cleaning,
    Safety,
    Inspection,
    Turnover,
    Administrative,
    Other,
}

/// <summary>Who raised the task. Affects what the reporter is told, and how much.</summary>
public enum TaskSource
{
    TenantReport,
    Inspection,
    PreventiveMaintenance,
    Staff,
    Vendor,
    System,
}

public enum TaskEventType
{
    TaskCreated,
    TaskAssigned,
    TaskUnassigned,
    TaskReassigned,
    TaskStatusChanged,
    TaskPriorityChanged,
    TaskDueDateChanged,
    TaskCommentAdded,
    TaskAttachmentAdded,
    TaskCompleted,
    TaskReopened,
    TaskCancelled,
    TaskCostUpdated,

    // Time-based, raised by the worker's sweep rather than by a user action.
    TaskDueSoon,
    TaskDueToday,
    TaskOverdue,
    TaskStale,
}

public enum TaskRuleTrigger
{
    Event,
    Schedule,
}

public enum TaskScheduleBasis
{
    BeforeDue,
    OnDue,
    AfterDue,
    NoActivity,
}

/// <summary>
/// Who a rule notifies. Resolved against the task at send time, so a rule
/// saying "notify the assignee" follows a reassignment instead of continuing to
/// email whoever handed the job over.
/// </summary>
public enum TaskRecipientRole
{
    Assignee,
    TaskCreator,
    ReportingTenant,
    PropertyManager,
    PropertyOwner,
    Vendor,
    CustomRecipient,
    RecipientGroup,
}

public enum OutboxStatus
{
    Pending,
    Claimed,
    Processed,
    Failed,
    DeadLetter,
}

public enum DeliveryChannel
{
    Email,
    InApp,
}

public enum DeliveryStatus
{
    Pending,
    Sent,
    Failed,
    Suppressed,
    Skipped,
}

// =============================================================================
// Entities
// =============================================================================

public class TaskColumn
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The fixed semantics behind <see cref="Name"/>. See <see cref="TaskStatusCategory"/>.</summary>
    public TaskStatusCategory Category { get; set; }

    /// <summary>
    /// Fractional, so inserting a column between two others is one update
    /// rather than a renumber of the board.
    /// </summary>
    public decimal Position { get; set; }

    /// <summary>
    /// Advisory cap shown as a warning. Not enforced: refusing to accept an
    /// urgent repair because a column is full would be worse than the overload
    /// it prevents.
    /// </summary>
    public int? WorkInProgressLimit { get; set; }

    public string? Colour { get; set; }

    public bool CollapsedByDefault { get; set; }

    /// <summary>Set on exactly one column: where a newly created task lands.</summary>
    public bool IsDefaultIntake { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

public class Vendor
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Company { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>Which categories this vendor handles, for suggesting an assignee.</summary>
    public TaskCategory[] Trades { get; set; } = [];

    public decimal? HourlyRate { get; set; }

    public string Currency { get; set; } = "USD";

    public string? Notes { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>
    /// A real operational date somebody needs reminding about, which is why it
    /// is a column rather than a line in the notes.
    /// </summary>
    public LocalDate? InsuranceExpiresOn { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

public class MaintenanceTask
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Sequential within the organization. The number people quote to each
    /// other on the phone; nobody reads a UUID aloud.
    /// </summary>
    public int ReferenceNumber { get; set; }

    public Guid PropertyId { get; set; }

    public Guid? UnitId { get; set; }

    /// <summary>
    /// The tenant who raised it. A fact about the report, which is why it
    /// survives the tenancy ending; a task is never bound to a lease.
    /// </summary>
    public Guid? ReportedByTenantId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid TaskColumnId { get; set; }

    /// <summary>
    /// Derived from the column by a database trigger, so a task's displayed
    /// status can never disagree with the column it sits in.
    /// </summary>
    public TaskStatusCategory StatusCategory { get; set; }

    public decimal Position { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public TaskCategory Category { get; set; } = TaskCategory.Other;

    public TaskSource Source { get; set; } = TaskSource.Staff;

    public LocalDate? DueDate { get; set; }

    public Instant? StartedAt { get; set; }

    public Instant? CompletedAt { get; set; }

    /// <summary>
    /// Set when the task has been withdrawn from the board.
    /// </summary>
    /// <remarks>
    /// Separate from completion on purpose. Completing says the work is done;
    /// archiving says the task should not be on the board — a duplicate, or
    /// something logged in error. Collapsing the two would force somebody to
    /// mark work "done" that was never done, in a record other people read.
    /// </remarks>
    public Instant? ArchivedAt { get; set; }

    public Guid? ArchivedByUserId { get; set; }

    public decimal? EstimatedCost { get; set; }

    public decimal? ActualCost { get; set; }

    public string Currency { get; set; } = "USD";

    public Guid? VendorId { get; set; }

    public Guid? ParentTaskId { get; set; }

    /// <summary>
    /// Null means one-off. A recurrence spawns its successor on completion
    /// rather than pre-generating a year of them, so cancelling the series
    /// leaves nothing to clean up.
    /// </summary>
    public int? RecurrenceIntervalDays { get; set; }

    public Guid? RecurrenceParentId { get; set; }

    /// <summary>
    /// Advanced by real activity only, which is what makes the no-activity
    /// escalation rule meaningful. UpdatedAt would be reset by a nightly job
    /// rewriting a denormalised field.
    /// </summary>
    public Instant LastActivityAt { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public RentalProperty? Property { get; set; }

    public Unit? Unit { get; set; }

    public TaskColumn? Column { get; set; }

    public Vendor? Vendor { get; set; }

    public Tenant? ReportedByTenant { get; set; }

    public ICollection<TaskAssignee> Assignees { get; } = [];

    /// <summary>
    /// Open means not finished and not abandoned. Cancelled is deliberately
    /// excluded from both "open" and "completed": a cancelled task did not get
    /// done, and counting it as completed would flatter every metric.
    /// </summary>
    public bool IsOpen =>
        StatusCategory is not (TaskStatusCategory.Completed or TaskStatusCategory.Cancelled);
}

public class TaskAssignee
{
    public Guid TaskId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>Set when the work is owned by a member of the workspace.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Set when the work is contracted out. Exactly one of the two.</summary>
    public Guid? VendorId { get; set; }

    public Instant AssignedAt { get; set; }

    public Guid? AssignedByUserId { get; set; }

    /// <summary>The primary owner, when several are assigned.</summary>
    public bool IsLead { get; set; }

    public User? User { get; set; }

    public Vendor? Vendor { get; set; }
}

public class TaskComment
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? AuthorUserId { get; set; }

    public Guid? AuthorVendorId { get; set; }

    /// <summary>Preserved, so a deleted user's comments still show a name.</summary>
    public string AuthorDisplayName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// An internal note is never shown to a reporting tenant and never
    /// included in a notification sent to one. Without this distinction a
    /// manager's candid note about a tenant could be emailed to that tenant.
    /// </summary>
    public bool IsInternal { get; set; }

    public Instant? EditedAt { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>
/// The user-facing task timeline. Append-only.
///
/// <para>
/// Distinct from <see cref="AuditLogEntry"/> on purpose. That is an operator's
/// security record, readable only by administrators; this is shown on the card
/// to anybody who can see the task. Merging them would mean either exposing the
/// security log or hiding the timeline.
/// </para>
/// </summary>
public class TaskActivityEntry
{
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string ActorDisplayName { get; set; } = string.Empty;

    public string ActorKind { get; set; } = "user";

    public string ActivityType { get; set; } = string.Empty;

    /// <summary>Display-ready, for example "moved this from New to In Progress".</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Structured before and after. Identifiers and values only.</summary>
    public string Detail { get; set; } = "{}";

    public Instant CreatedAt { get; set; }
}

public class TaskNotificationRule
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>Null means organization-wide; a value narrows it to one property.</summary>
    public Guid? PropertyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public TaskRuleTrigger TriggerKind { get; set; }

    public TaskEventType? EventType { get; set; }

    public TaskScheduleBasis? ScheduleBasis { get; set; }

    public int? ScheduleOffsetDays { get; set; }

    /// <summary>
    /// Daily escalation while the condition holds, bounded by
    /// <see cref="MaxRepeats"/>. Unbounded repetition is how a forgotten task
    /// turns into a hundred identical emails.
    /// </summary>
    public bool RepeatDaily { get; set; }

    public int? MaxRepeats { get; set; }

    /// <summary>Null means any priority. An unset filter must not exclude everything.</summary>
    public TaskPriority[]? OnlyPriorities { get; set; }

    public TaskCategory[]? OnlyCategories { get; set; }

    public TaskStatusCategory[]? OnlyStatusCategories { get; set; }

    public DeliveryChannel[] Channels { get; set; } = [DeliveryChannel.Email, DeliveryChannel.InApp];

    public Guid? EmailTemplateId { get; set; }

    public LocalTime? SendLocalTime { get; set; }

    public string? TimeZone { get; set; }

    public bool Enabled { get; set; } = true;

    public int MaxSendsPerTaskPerDay { get; set; } = 5;

    public Instant? PausedAt { get; set; }

    public string? PausedReason { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

/// <summary>
/// One notification, to one recipient, through one channel.
///
/// <para>
/// This doubles as the unified notification centre: an in-app notification IS
/// a delivery with <see cref="Channel"/> of <see cref="DeliveryChannel.InApp"/>.
/// Keeping both in one table means the centre shows rent reminders, lease
/// expiries, and task updates together without a union across three schemas,
/// and "was this person told?" has one answer.
/// </para>
/// </summary>
public class NotificationDelivery
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public long? OutboxId { get; set; }

    public Guid? ScheduledNotificationId { get; set; }

    public string SubjectType { get; set; } = string.Empty;

    public Guid? SubjectId { get; set; }

    public DeliveryChannel Channel { get; set; }

    public Guid? RecipientUserId { get; set; }

    public string? RecipientEmail { get; set; }

    /// <summary>Why this person was chosen, so the UI can answer "why am I getting this?".</summary>
    public TaskRecipientRole? RecipientRole { get; set; }

    public Guid? TaskNotificationRuleId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Relative path only. An absolute URL would make the notification centre
    /// an open-redirect surface, which is especially dangerous here because a
    /// notification is a link somebody clicks from an email.
    /// </summary>
    public string? LinkPath { get; set; }

    public DeliveryStatus Status { get; set; }

    public EmailErrorCategory ErrorCategory { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ProviderMessageId { get; set; }

    /// <summary>
    /// When the provider accepted the request. Deliberately not named
    /// DeliveredAt: acceptance is not delivery (section 6.J).
    /// </summary>
    public Instant? ProviderAcceptedAt { get; set; }

    public Instant? ReadAt { get; set; }

    public Instant? DismissedAt { get; set; }

    public Instant CreatedAt { get; set; }

    public bool IsUnread => Channel == DeliveryChannel.InApp && ReadAt is null;
}
