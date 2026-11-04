using NodaTime;

namespace LeaseReminder.Domain;

// =============================================================================
// Persistence entities for phase 1.
//
// Every organization-owned entity carries OrganizationId. That is not
// redundancy with the navigation property: the composite foreign keys in the
// database reference (parent_id, organization_id), so the column has to be
// present and correct on the row. It also means the RLS policies never have to
// join to find an owner.
//
// Dates that are calendar dates in a property time zone are LocalDate, and
// instants are Instant. The distinction is load-bearing: a lease start date is
// not a moment in time, and treating it as one is how a lease silently starts a
// day early in some time zones.
// =============================================================================

public class User
{
    public Guid Id { get; set; }

    /// <summary>The identity provider subject, matched against the JWT sub claim.</summary>
    public Guid AuthUserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

public class Organization
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Default IANA zone for new properties. Never used for scheduling directly.</summary>
    public string TimeZone { get; set; } = "UTC";

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public ICollection<OrganizationMember> Members { get; } = [];
}

public class OrganizationMember
{
    public Guid OrganizationId { get; set; }

    public Guid UserId { get; set; }

    public OrgRole Role { get; set; } = OrgRole.ReadOnly;

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Organization? Organization { get; set; }

    public User? User { get; set; }
}

/// <summary>
/// A building or address. Named RentalProperty rather than Property because
/// "property" is a C# language concept, and <c>Property.PropertyType</c> reads
/// badly enough to be worth two extra syllables.
/// </summary>
public class RentalProperty
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public PropertyType PropertyType { get; set; } = PropertyType.Other;

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? Locality { get; set; }

    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    public string CountryCode { get; set; } = "US";

    /// <summary>
    /// The zone every date rule for this property is interpreted in. Required,
    /// because there is no safe default for "when is rent due".
    /// </summary>
    public string TimeZone { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// Set when the property has been withdrawn from use.
    /// </summary>
    /// <remarks>
    /// A property with history cannot be deleted without taking its leases,
    /// rent schedules and audit trail with it, so it is archived instead:
    /// hidden from the portfolio, with everything it carries retained.
    /// Archiving is refused while any lease on it is active or upcoming.
    /// </remarks>
    public Instant? ArchivedAt { get; set; }

    public Guid? ArchivedByUserId { get; set; }

    public ICollection<Unit> Units { get; } = [];
}

public class Unit
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid PropertyId { get; set; }

    public string UnitNumber { get; set; } = string.Empty;

    public UnitStatus Status { get; set; } = UnitStatus.Vacant;

    public short? Bedrooms { get; set; }

    public decimal? Bathrooms { get; set; }

    public int? SquareFeet { get; set; }

    public string? Notes { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public RentalProperty? Property { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string LegalName { get; set; } = string.Empty;

    public string? PreferredName { get; set; }

    public string? PrimaryEmail { get; set; }

    public string? SecondaryEmail { get; set; }

    public string? Phone { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>
    /// Consent gate. A rule targeting tenants must not send when this is
    /// false, whatever the rule says.
    /// </summary>
    public bool NotificationsOptedIn { get; set; } = true;

    public Instant? NotificationsOptInAt { get; set; }

    public Instant? NotificationsOptOutAt { get; set; }

    public string? Notes { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

public class Lease
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>Null for a single-family property with no distinct unit.</summary>
    public Guid? UnitId { get; set; }

    public string? Reference { get; set; }

    // --- Three independent state machines (section 13.E) ---------------------

    public LeaseLifecycleStatus LifecycleStatus { get; set; } = LeaseLifecycleStatus.Draft;

    public LeaseApprovalStatus ApprovalStatus { get; set; } = LeaseApprovalStatus.PendingReview;

    public NotificationAutomationStatus AutomationStatus { get; set; } =
        NotificationAutomationStatus.NotConfigured;

    // --- Term ---------------------------------------------------------------

    public LocalDate? StartDate { get; set; }

    /// <summary>
    /// Null means open-ended. An UNKNOWN end date also stays null, flagged on
    /// the extraction for review; it is never guessed.
    /// </summary>
    public LocalDate? EndDate { get; set; }

    // --- Financial terms ----------------------------------------------------

    public decimal? RentAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public decimal? SecurityDepositAmount { get; set; }

    public LocalDate? FirstPaymentDate { get; set; }

    public short? GracePeriodDays { get; set; }

    public decimal? LateFeeAmount { get; set; }

    public bool LateFeeIsPercentage { get; set; }

    /// <summary>
    /// Days after the due date at which a late fee applies. Deliberately
    /// separate from <see cref="GracePeriodDays"/>.
    /// </summary>
    public short? LateFeeAfterDays { get; set; }

    // --- Renewal and termination --------------------------------------------

    public short? RenewalNoticeDays { get; set; }

    public string? RenewalTerms { get; set; }

    public string? EarlyTerminationTerms { get; set; }

    public short? NoticePeriodDays { get; set; }

    public string? PaymentInstructions { get; set; }

    public string? SpecialClauses { get; set; }

    public string? Notes { get; set; }

    // --- Provenance and approval --------------------------------------------

    public Guid? SourceDocumentId { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public Instant? ApprovedAt { get; set; }

    public Guid? RejectedByUserId { get; set; }

    public Instant? RejectedAt { get; set; }

    public string? RejectionReason { get; set; }

    /// <summary>Pausing automation is independent of approval and lifecycle.</summary>
    public Instant? AutomationPausedAt { get; set; }

    public string? AutomationPausedReason { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public RentalProperty? Property { get; set; }

    public Unit? Unit { get; set; }

    public ICollection<LeaseTenant> LeaseTenants { get; } = [];

    /// <summary>
    /// Whether this lease may generate and send reminders at all. Mirrors the
    /// predicate in app.claim_due_notifications, so the API can explain the
    /// state the dispatcher will independently reach.
    /// </summary>
    public bool CanSendReminders =>
        ApprovalStatus == LeaseApprovalStatus.Approved
        && AutomationStatus == NotificationAutomationStatus.Active
        && LifecycleStatus is LeaseLifecycleStatus.Upcoming or LeaseLifecycleStatus.Active;
}

public class LeaseTenant
{
    public Guid LeaseId { get; set; }

    public Guid TenantId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>One of primary, co_tenant, guarantor, occupant.</summary>
    public string Role { get; set; } = "co_tenant";

    public Instant CreatedAt { get; set; }

    public Lease? Lease { get; set; }

    public Tenant? Tenant { get; set; }
}

/// <summary>
/// Append-only. The database blocks UPDATE and DELETE with a trigger and the
/// API role holds no privilege for either, so there is deliberately no way to
/// amend one of these from application code.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid? ActorUserId { get; set; }

    /// <summary>One of user, worker, system.</summary>
    public string ActorKind { get; set; } = "user";

    /// <summary>Dotted action name, for example lease.approved.</summary>
    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public Guid? EntityId { get; set; }

    /// <summary>
    /// Identifiers and before/after summaries only. Never document text,
    /// tokens, or email bodies. The column name is the reminder.
    /// </summary>
    public string SafeMetadataJson { get; set; } = "{}";

    public Guid? CorrelationId { get; set; }

    public Instant CreatedAt { get; set; }
}
