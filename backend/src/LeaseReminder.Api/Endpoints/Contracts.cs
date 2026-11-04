using FluentValidation;
using LeaseReminder.Domain;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

// =============================================================================
// Request and response contracts.
//
// Requests are separate types from entities on purpose. If a handler bound
// straight onto Lease, a caller could post approvalStatus or approvedAt and
// approve their own lease without review, which is precisely what section 13.C
// forbids. The fields that are absent from these records are as deliberate as
// the ones present.
//
// Validators mirror the database CHECK constraints. The constraint is the real
// guarantee; the validator exists so a user gets a readable message naming the
// field instead of a 500 from a constraint violation.
// =============================================================================

// -----------------------------------------------------------------------------
// Organization
// -----------------------------------------------------------------------------

public sealed record CreateOrganizationRequest(string Name, string TimeZone);

public sealed class CreateOrganizationRequestValidator
    : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("An organization name is required.")
            .MaximumLength(200);

        RuleFor(request => request.TimeZone)
            .NotEmpty()
            .Must(BeAKnownTimeZone)
            .WithMessage("'{PropertyValue}' is not a known IANA time zone, for example America/New_York.");
    }

    internal static bool BeAKnownTimeZone(string? timeZone) =>
        !string.IsNullOrWhiteSpace(timeZone)
        && DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) is not null;
}

/// <summary>Renaming a workspace, or changing its default time zone.</summary>
public sealed record UpdateOrganizationRequest(string Name, string TimeZone);

public sealed class UpdateOrganizationRequestValidator
    : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("A workspace name is required.")
            .MaximumLength(200);

        RuleFor(request => request.TimeZone)
            .NotEmpty()
            .Must(CreateOrganizationRequestValidator.BeAKnownTimeZone)
            .WithMessage("'{PropertyValue}' is not a known IANA time zone, for example America/New_York.");
    }
}

public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string TimeZone,
    OrgRole CallerRole);

public sealed record CurrentUserResponse(
    Guid UserId,
    string Email,
    string? DisplayName,
    IReadOnlyList<OrganizationResponse> Organizations);

// -----------------------------------------------------------------------------
// Property
// -----------------------------------------------------------------------------

public sealed record UpsertPropertyRequest(
    string Name,
    PropertyType PropertyType,
    string TimeZone,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? Region,
    string? PostalCode,
    string? CountryCode,
    string? Notes);

public sealed class UpsertPropertyRequestValidator : AbstractValidator<UpsertPropertyRequest>
{
    public UpsertPropertyRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("A property name is required.")
            .MaximumLength(200);

        RuleFor(request => request.PropertyType).IsInEnum();

        // Required, with no default offered. Section 6.F is explicit that
        // there is no safe assumption about when rent is due, and the zone is
        // what every date rule for this property is interpreted in.
        RuleFor(request => request.TimeZone)
            .NotEmpty().WithMessage(
                "A time zone is required: every rent date for this property is interpreted in it.")
            .Must(CreateOrganizationRequestValidator.BeAKnownTimeZone)
            .WithMessage("'{PropertyValue}' is not a known IANA time zone.");

        RuleFor(request => request.AddressLine1).MaximumLength(300);
        RuleFor(request => request.AddressLine2).MaximumLength(300);
        RuleFor(request => request.Locality).MaximumLength(200);
        RuleFor(request => request.Region).MaximumLength(200);
        RuleFor(request => request.PostalCode).MaximumLength(40);

        RuleFor(request => request.CountryCode)
            .Matches("^[A-Z]{2}$")
            .When(request => !string.IsNullOrEmpty(request.CountryCode))
            .WithMessage("Country code must be two upper-case letters, for example US.");

        RuleFor(request => request.Notes).MaximumLength(10000);
    }
}

public sealed record PropertyResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    PropertyType PropertyType,
    string TimeZone,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? Region,
    string? PostalCode,
    string CountryCode,
    string? Notes,
    int UnitCount,
    int ActiveLeaseCount,
    /// <summary>Non-null when the property has been withdrawn from use.</summary>
    Instant? ArchivedAt);

// -----------------------------------------------------------------------------
// Unit
// -----------------------------------------------------------------------------

public sealed record UpsertUnitRequest(
    string UnitNumber,
    UnitStatus Status,
    short? Bedrooms,
    decimal? Bathrooms,
    int? SquareFeet,
    string? Notes);

public sealed class UpsertUnitRequestValidator : AbstractValidator<UpsertUnitRequest>
{
    public UpsertUnitRequestValidator()
    {
        RuleFor(request => request.UnitNumber)
            .NotEmpty().WithMessage("A unit identifier is required.")
            .MaximumLength(60);

        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Bedrooms).InclusiveBetween((short)0, (short)100);
        RuleFor(request => request.Bathrooms).InclusiveBetween(0m, 100m);
        RuleFor(request => request.SquareFeet).InclusiveBetween(0, 10_000_000);
        RuleFor(request => request.Notes).MaximumLength(10000);
    }
}

public sealed record UnitResponse(
    Guid Id,
    Guid PropertyId,
    string UnitNumber,
    UnitStatus Status,
    short? Bedrooms,
    decimal? Bathrooms,
    int? SquareFeet,
    string? Notes,
    Guid? ActiveLeaseId,
    string? CurrentTenantName);

// -----------------------------------------------------------------------------
// Tenant
// -----------------------------------------------------------------------------

public sealed record UpsertTenantRequest(
    string LegalName,
    string? PreferredName,
    string? PrimaryEmail,
    string? SecondaryEmail,
    string? Phone,
    TenantStatus Status,
    bool NotificationsOptedIn,
    string? Notes);

public sealed class UpsertTenantRequestValidator : AbstractValidator<UpsertTenantRequest>
{
    public UpsertTenantRequestValidator()
    {
        RuleFor(request => request.LegalName)
            .NotEmpty().WithMessage("A legal name is required.")
            .MaximumLength(200);

        RuleFor(request => request.PreferredName).MaximumLength(200);

        RuleFor(request => request.PrimaryEmail)
            .EmailAddress()
            .MaximumLength(320)
            .When(request => !string.IsNullOrEmpty(request.PrimaryEmail));

        RuleFor(request => request.SecondaryEmail)
            .EmailAddress()
            .MaximumLength(320)
            .When(request => !string.IsNullOrEmpty(request.SecondaryEmail));

        RuleFor(request => request.Phone).MaximumLength(40);
        RuleFor(request => request.Status).IsInEnum();
        RuleFor(request => request.Notes).MaximumLength(10000);
    }
}

public sealed record TenantResponse(
    Guid Id,
    string LegalName,
    string? PreferredName,
    string? PrimaryEmail,
    string? SecondaryEmail,
    string? Phone,
    TenantStatus Status,
    bool NotificationsOptedIn,
    string? Notes,
    int LeaseCount);

// -----------------------------------------------------------------------------
// Lease
//
// Note what is NOT here: approvalStatus, approvedAt, approvedByUserId,
// lifecycleStatus, automationStatus. Those change only through the dedicated
// approve, reject, and pause endpoints, each of which records who acted and
// when (section 13.C). A lease cannot be approved by editing it.
// -----------------------------------------------------------------------------

public sealed record UpsertLeaseRequest(
    Guid PropertyId,
    Guid? UnitId,
    string? Reference,
    LocalDate? StartDate,
    LocalDate? EndDate,
    decimal? RentAmount,
    string? Currency,
    decimal? SecurityDepositAmount,
    LocalDate? FirstPaymentDate,
    short? GracePeriodDays,
    decimal? LateFeeAmount,
    bool LateFeeIsPercentage,
    short? LateFeeAfterDays,
    short? RenewalNoticeDays,
    string? RenewalTerms,
    string? EarlyTerminationTerms,
    short? NoticePeriodDays,
    string? PaymentInstructions,
    string? SpecialClauses,
    string? Notes,
    IReadOnlyList<LeaseTenantAssignment>? Tenants);

public sealed record LeaseTenantAssignment(Guid TenantId, string Role);

public sealed class UpsertLeaseRequestValidator : AbstractValidator<UpsertLeaseRequest>
{
    private static readonly string[] ValidTenantRoles =
        ["primary", "co_tenant", "guarantor", "occupant"];

    public UpsertLeaseRequestValidator()
    {
        RuleFor(request => request.PropertyId).NotEmpty();

        RuleFor(request => request.Reference).MaximumLength(200);

        RuleFor(request => request.EndDate)
            .GreaterThanOrEqualTo(request => request.StartDate!.Value)
            .When(request => request.StartDate.HasValue && request.EndDate.HasValue)
            .WithMessage("The lease end date cannot precede its start date.");

        RuleFor(request => request.RentAmount)
            .InclusiveBetween(0m, 99_999_999.99m)
            .When(request => request.RentAmount.HasValue);

        RuleFor(request => request.Currency)
            .Matches("^[A-Z]{3}$")
            .When(request => !string.IsNullOrEmpty(request.Currency))
            .WithMessage("Currency must be a three-letter code, for example USD.");

        RuleFor(request => request.SecurityDepositAmount)
            .GreaterThanOrEqualTo(0m)
            .When(request => request.SecurityDepositAmount.HasValue);

        RuleFor(request => request.GracePeriodDays)
            .InclusiveBetween((short)0, (short)365)
            .When(request => request.GracePeriodDays.HasValue);

        RuleFor(request => request.LateFeeAfterDays)
            .InclusiveBetween((short)0, (short)365)
            .When(request => request.LateFeeAfterDays.HasValue);

        // A percentage late fee above 100 is almost certainly a currency
        // amount typed into the wrong field.
        RuleFor(request => request.LateFeeAmount)
            .InclusiveBetween(0m, 100m)
            .When(request => request.LateFeeIsPercentage && request.LateFeeAmount.HasValue)
            .WithMessage("A percentage late fee must be between 0 and 100.");

        RuleFor(request => request.LateFeeAmount)
            .GreaterThanOrEqualTo(0m)
            .When(request => !request.LateFeeIsPercentage && request.LateFeeAmount.HasValue);

        RuleFor(request => request.RenewalNoticeDays)
            .InclusiveBetween((short)0, (short)3650)
            .When(request => request.RenewalNoticeDays.HasValue);

        RuleFor(request => request.NoticePeriodDays)
            .InclusiveBetween((short)0, (short)3650)
            .When(request => request.NoticePeriodDays.HasValue);

        RuleFor(request => request.RenewalTerms).MaximumLength(10000);
        RuleFor(request => request.EarlyTerminationTerms).MaximumLength(10000);
        RuleFor(request => request.PaymentInstructions).MaximumLength(10000);
        RuleFor(request => request.SpecialClauses).MaximumLength(50000);
        RuleFor(request => request.Notes).MaximumLength(10000);

        RuleForEach(request => request.Tenants)
            .ChildRules(tenant =>
            {
                tenant.RuleFor(assignment => assignment.TenantId).NotEmpty();

                tenant.RuleFor(assignment => assignment.Role)
                    .Must(role => ValidTenantRoles.Contains(role, StringComparer.Ordinal))
                    .WithMessage(
                        "Tenant role must be one of: " + string.Join(", ", ValidTenantRoles));
            })
            .When(request => request.Tenants is not null);

        RuleFor(request => request.Tenants)
            .Must(tenants => tenants!.Count(t => t.Role == "primary") <= 1)
            .When(request => request.Tenants is not null)
            .WithMessage("A lease can name at most one primary tenant.");
    }
}

public sealed record LeaseResponse(
    Guid Id,
    Guid OrganizationId,
    Guid PropertyId,
    string PropertyName,
    Guid? UnitId,
    string? UnitNumber,
    string? Reference,

    // Three independent states, reported separately so the UI never has to
    // infer one from another (section 13.E).
    LeaseLifecycleStatus LifecycleStatus,
    LeaseApprovalStatus ApprovalStatus,
    NotificationAutomationStatus AutomationStatus,

    LocalDate? StartDate,
    LocalDate? EndDate,
    decimal? RentAmount,
    string Currency,
    decimal? SecurityDepositAmount,
    LocalDate? FirstPaymentDate,
    short? GracePeriodDays,
    decimal? LateFeeAmount,
    bool LateFeeIsPercentage,
    short? LateFeeAfterDays,
    short? RenewalNoticeDays,
    string? RenewalTerms,
    string? EarlyTerminationTerms,
    short? NoticePeriodDays,
    string? PaymentInstructions,
    string? SpecialClauses,
    string? Notes,

    Instant? ApprovedAt,
    Guid? ApprovedByUserId,
    Instant? RejectedAt,
    string? RejectionReason,
    Instant? AutomationPausedAt,
    string? AutomationPausedReason,

    // Whether reminders can actually be generated and sent. Computed from the
    // three statuses so the UI shows one honest answer rather than making the
    // reader combine them.
    bool CanSendReminders,

    IReadOnlyList<LeaseTenantResponse> Tenants);

public sealed record LeaseTenantResponse(
    Guid TenantId,
    string LegalName,
    string? PreferredName,
    string Role,
    bool NotificationsOptedIn);

public sealed record ApproveLeaseRequest(
    // Confirms the reviewer actually looked at the extracted terms. Required,
    // so approval cannot quietly become an empty POST.
    bool TermsReviewed,
    string? Note,
    // The extraction being signed off, when the lease came from a document.
    // Null for a lease entered by hand, which has no fields to have reviewed.
    // When present, the database refuses the approval unless every one of its
    // fields has been decided and no blocking issue remains open.
    Guid? ExtractionId = null);

public sealed record RejectLeaseRequest(string Reason);

public sealed class RejectLeaseRequestValidator : AbstractValidator<RejectLeaseRequest>
{
    public RejectLeaseRequestValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("A rejection reason is required so the decision is auditable.")
            .MaximumLength(2000);
    }
}

public sealed record PauseAutomationRequest(string Reason);

public sealed class PauseAutomationRequestValidator : AbstractValidator<PauseAutomationRequest>
{
    public PauseAutomationRequestValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("A reason is required so the paused state is explainable.")
            .MaximumLength(1000);
    }
}
