namespace LeaseReminder.Domain;

// =============================================================================
// Enumerations mirroring the PostgreSQL enum types in db/migrations/0001.
//
// The [PgName] values are supplied by the EF Core configuration in the
// Infrastructure project rather than by attributes here, so this project keeps
// no dependency on Npgsql. The C# member names are deliberately identical to
// the PostgreSQL labels modulo casing, so the mapping is mechanical and a
// mismatch is obvious on sight.
//
// Section 13.E is the reason there are five separate status enums rather than
// one. A lease can be approved and active while its automation is paused, and
// a Gmail failure must not be representable as a lease state.
// =============================================================================

/// <summary>Role of a user within an organization. Drives every authorization decision.</summary>
public enum OrgRole
{
    Owner,
    Admin,
    PropertyManager,
    Accounting,
    ReadOnly,
}

public enum PropertyType
{
    SingleFamily,
    MultiFamily,
    Condo,
    Apartment,
    Townhouse,
    Commercial,
    Other,
}

/// <summary>Physical state of a unit. Independent of any lease status.</summary>
public enum UnitStatus
{
    Vacant,
    Occupied,
    Unavailable,
    Maintenance,
}

public enum TenantStatus
{
    Prospective,
    Active,
    Past,
    Archived,
}

/// <summary>Where a lease sits in its term (section 13.E.3).</summary>
public enum LeaseLifecycleStatus
{
    Draft,
    Upcoming,
    Active,
    Expired,
    Terminated,
    Archived,
}

/// <summary>
/// The human review gate (section 13.E.2). Reminders are never generated or
/// sent unless this is <see cref="Approved"/>.
/// </summary>
public enum LeaseApprovalStatus
{
    PendingReview,
    Approved,
    Rejected,
}

/// <summary>Document pipeline state (section 13.E.1). Separate from lease approval.</summary>
public enum DocumentProcessingStatus
{
    Uploaded,
    Validating,
    Quarantined,
    Queued,
    Extracting,
    ReviewReady,
    Failed,
}

public enum MalwareScanStatus
{
    Pending,
    Clean,
    Infected,
    Skipped,
    Error,
}

public enum ExtractionReviewStatus
{
    Pending,
    Accepted,
    Corrected,
    Rejected,
}

public enum FieldReviewStatus
{
    Pending,
    Accepted,
    Corrected,
    Rejected,
    Flagged,
}

/// <summary>
/// Keeps a fact stated in the lease distinct from a value the application
/// derived (section 13.D). A reviewer needs to know which they are looking at.
/// </summary>
public enum ValueProvenance
{
    StatedInDocument,
    InferredByCalculation,
    UserEntered,
    DefaultApplied,
}

public enum RentFrequency
{
    Monthly,
    Weekly,
    Biweekly,
    Every30Days,
    OneTime,
    Custom,
}

public enum DueDayRule
{
    FirstOfMonth,
    LastOfMonth,
    SpecificDay,
    FirstBusinessDay,
    CustomSequence,
}

/// <summary>
/// What to do when the configured due day does not exist in a month, for
/// example the 31st in February (section 6.F). There is no default that is
/// right for every lease, so the choice is explicit.
/// </summary>
public enum ShortMonthFallback
{
    /// <summary>Use the last calendar day of the short month.</summary>
    LastDayOfMonth,

    /// <summary>Generate no occurrence for that month.</summary>
    SkipMonth,

    /// <summary>
    /// Generate nothing and require the user to add an explicit exception.
    /// The generator reports the month as needing attention rather than
    /// guessing a date.
    /// </summary>
    ManualException,
}

/// <summary>
/// Weekend and holiday handling. Never assumed: <see cref="None"/> is the
/// default and nothing shifts until a user both chooses a shift and supplies a
/// working-day calendar (section 6.F).
/// </summary>
public enum BusinessDayShift
{
    None,
    NextBusinessDay,
    PreviousBusinessDay,
}

/// <summary>
/// The event a notification rule hangs off. Rent due, grace period end, and
/// late fee trigger are deliberately three different events: they fall on
/// different dates and mean different things to the reader.
/// </summary>
public enum NotificationEventType
{
    RentDue,
    GracePeriodEnd,
    LateFeeTrigger,
    LeaseExpiration,
    RenewalNoticeDeadline,
    RentIncrease,
    EarlyTerminationDeadline,
    Custom,
}

public enum NotificationTriggerType
{
    Before,
    On,
    After,
}

public enum OffsetUnit
{
    Minutes,
    Hours,
    Days,
}

/// <summary>
/// How a rule relates to the rules above it, so the UI can show whether a rule
/// is inherited, overridden, added, or switched off (section 6.G).
/// </summary>
public enum RuleInheritanceMode
{
    Inherited,
    Overridden,
    Additional,
    Disabled,
}

public enum RecipientKind
{
    To,
    Cc,
    Bcc,
}

/// <summary>Notification automation state (section 13.E.4). Independent of lease approval.</summary>
public enum NotificationAutomationStatus
{
    NotConfigured,
    PendingActivation,
    Active,
    Paused,
    Error,
}

/// <summary>Gmail connection state (section 13.E.5). A failure here changes no lease status.</summary>
public enum EmailConnectionStatus
{
    NotConnected,
    Connected,
    ReauthorizationRequired,
    Revoked,
    Error,
}

public enum EmailTemplateStatus
{
    Draft,
    Published,
    Archived,
}

public enum ScheduledNotificationStatus
{
    Scheduled,
    Claimed,
    Sent,
    Failed,
    DeadLetter,
    Skipped,
    Cancelled,
    Superseded,
}

/// <summary>
/// Outcome of a send attempt. <see cref="Accepted"/> means the provider took
/// the request; it is not evidence of inbox delivery (section 6.J).
/// </summary>
public enum EmailSendStatus
{
    Accepted,
    Failed,
    Suppressed,
}

public enum EmailErrorCategory
{
    None,
    Authorization,
    Quota,
    InvalidRecipient,
    Transient,
    Permanent,
    Configuration,
    Unknown,
}

public enum JobKind
{
    DocumentValidate,
    DocumentExtractText,
    DocumentOcr,
    DocumentAiExtract,
    ScheduleRegenerate,
    NotificationDispatch,
    Cleanup,
}

public enum JobStatus
{
    Queued,
    Claimed,
    Succeeded,
    Failed,
    DeadLetter,
    Cancelled,
}

/// <summary>Why an occurrence date differs from the date the rule produced.</summary>
public enum OccurrenceAdjustment
{
    ShortMonthFallback,
    WeekendShift,
    HolidayShift,
    UserException,
    Prorated,
}
