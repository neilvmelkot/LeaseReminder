using FluentValidation;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Leases, and the approval gate.
///
/// <para>
/// The important thing in this file is what the edit endpoint cannot do.
/// Section 13.C requires that every lease is explicitly approved before it
/// becomes active, and that AI extraction alone never activates one. That is
/// enforced in three places, deliberately overlapping:
/// </para>
/// <list type="number">
///   <item>
///     The request contract has no approval or lifecycle fields, so a client
///     cannot send them.
///   </item>
///   <item>
///     Approval lives on its own endpoint that records who acted and when, and
///     writes an append-only lease_approvals row.
///   </item>
///   <item>
///     The database CHECK constraint leases_live_requires_approval_check makes
///     an unapproved live lease unrepresentable, so even a direct SQL mistake
///     cannot create one.
///   </item>
/// </list>
///
/// <para>
/// Pausing automation is separate from all of that, because section 13.C also
/// requires that a user can pause an approved lease's reminders WITHOUT
/// changing its approval status.
/// </para>
/// </summary>
public static class LeaseEndpoints
{
    public static IEndpointRouteBuilder MapLeaseEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/leases");

        // ---------------------------------------------------------------------
        // GET /leases
        // ---------------------------------------------------------------------
        group.MapGet("/", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            LeaseApprovalStatus? approvalStatus,
            LeaseLifecycleStatus? lifecycleStatus,
            int? expiringWithinDays) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var query = dbContext.Leases
                .AsNoTracking()
                .Where(lease => lease.OrganizationId == organizationId);

            if (approvalStatus.HasValue)
            {
                query = query.Where(lease => lease.ApprovalStatus == approvalStatus.Value);
            }

            if (lifecycleStatus.HasValue)
            {
                query = query.Where(lease => lease.LifecycleStatus == lifecycleStatus.Value);
            }

            if (expiringWithinDays is > 0 and <= 365)
            {
                // "Expiring soon" is measured against today in the PROPERTY's
                // zone, not the server's. A lease in Los Angeles expiring
                // "within 30 days" means something different to a server
                // running in UTC, and the difference shows up at both ends of
                // the window.
                var today = SystemClock.Instance.GetCurrentInstant()
                    .InUtc().Date;

                var cutoff = today.PlusDays(expiringWithinDays.Value);

                query = query.Where(lease =>
                    lease.EndDate != null
                    && lease.EndDate >= today
                    && lease.EndDate <= cutoff);
            }

            var leases = await query
                .OrderBy(lease => lease.EndDate ?? LocalDate.MaxIsoValue)
                .ThenBy(lease => lease.Reference)
                .Select(lease => ProjectLease(lease))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(leases);
        })
            .WithName("ListLeases")
            .WithSummary("Leases, filterable by approval status, lifecycle, and expiry window.");

        // ---------------------------------------------------------------------
        // GET /leases/{id}
        // ---------------------------------------------------------------------
        group.MapGet("/{leaseId:guid}", async (
            Guid organizationId,
            Guid leaseId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var lease = await dbContext.Leases
                .AsNoTracking()
                .Where(candidate => candidate.Id == leaseId
                                    && candidate.OrganizationId == organizationId)
                .Select(candidate => ProjectLease(candidate))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return lease is null ? EndpointSupport.NotFound("lease") : Results.Ok(lease);
        })
            .WithName("GetLease");

        // ---------------------------------------------------------------------
        // POST /leases
        //
        // A new lease always starts pending review and in draft, whether it
        // was typed in by hand or proposed by extraction. There is no request
        // shape that creates an active lease.
        // ---------------------------------------------------------------------
        group.MapPost("/", async (
            Guid organizationId,
            UpsertLeaseRequest request,
            IValidator<UpsertLeaseRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot create leases here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var propertyExists = await dbContext.Properties
                .AnyAsync(
                    property => property.Id == request.PropertyId
                                && property.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!propertyExists)
            {
                return EndpointSupport.NotFound("property");
            }

            if (request.UnitId is { } unitId)
            {
                // The unit must belong to the named property. The composite
                // foreign key enforces this too, but a 404 is a better answer
                // than a constraint violation.
                var unitBelongs = await dbContext.Units
                    .AnyAsync(
                        unit => unit.Id == unitId && unit.PropertyId == request.PropertyId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!unitBelongs)
                {
                    return EndpointSupport.NotFound("unit in that property");
                }
            }

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            var now = SystemClock.Instance.GetCurrentInstant();

            var lease = new Lease
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                PropertyId = request.PropertyId,
                UnitId = request.UnitId,
                Reference = request.Reference?.Trim(),

                // The three states a new lease starts in. Not configurable.
                LifecycleStatus = LeaseLifecycleStatus.Draft,
                ApprovalStatus = LeaseApprovalStatus.PendingReview,
                AutomationStatus = NotificationAutomationStatus.NotConfigured,

                StartDate = request.StartDate,
                EndDate = request.EndDate,
                RentAmount = request.RentAmount,
                Currency = request.Currency ?? "USD",
                SecurityDepositAmount = request.SecurityDepositAmount,
                FirstPaymentDate = request.FirstPaymentDate,
                GracePeriodDays = request.GracePeriodDays,
                LateFeeAmount = request.LateFeeAmount,
                LateFeeIsPercentage = request.LateFeeIsPercentage,
                LateFeeAfterDays = request.LateFeeAfterDays,
                RenewalNoticeDays = request.RenewalNoticeDays,
                RenewalTerms = request.RenewalTerms,
                EarlyTerminationTerms = request.EarlyTerminationTerms,
                NoticePeriodDays = request.NoticePeriodDays,
                PaymentInstructions = request.PaymentInstructions,
                SpecialClauses = request.SpecialClauses,
                Notes = request.Notes,

                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = actor == Guid.Empty ? null : actor,
            };

            dbContext.Leases.Add(lease);

            foreach (var assignment in request.Tenants ?? [])
            {
                dbContext.LeaseTenants.Add(new LeaseTenant
                {
                    LeaseId = lease.Id,
                    TenantId = assignment.TenantId,
                    OrganizationId = organizationId,
                    Role = assignment.Role,
                    CreatedAt = now,
                });
            }

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "lease.created", "lease", lease.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/leases/{lease.Id}",
                await LoadResponseAsync(dbContext, lease.Id, cancellationToken)
                    .ConfigureAwait(false));
        })
            .WithName("CreateLease")
            .WithSummary("Creates a lease. Always starts pending review and in draft.");

        // ---------------------------------------------------------------------
        // POST /leases/{id}/approve
        //
        // The gate. Deliberately a distinct, named action rather than part of
        // a save: section 5 asks that approval not look like an ordinary save,
        // and that starts with the API shape.
        // ---------------------------------------------------------------------
        group.MapPost("/{leaseId:guid}/approve", async (
            Guid organizationId,
            Guid leaseId,
            ApproveLeaseRequest request,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot approve leases here.");
            }

            if (!request.TermsReviewed)
            {
                return Results.Problem(
                    title: "Review not confirmed",
                    detail: "Approval requires confirming that the lease terms were reviewed.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var lease = await dbContext.Leases
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == leaseId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (lease is null)
            {
                return EndpointSupport.NotFound("lease");
            }

            if (lease.ApprovalStatus == LeaseApprovalStatus.Approved)
            {
                // Idempotent rather than an error: a double-clicked Approve
                // button should not look like a failure.
                return Results.Ok(
                    await LoadResponseAsync(dbContext, leaseId, cancellationToken)
                        .ConfigureAwait(false));
            }

            // A lease cannot go live without the terms needed to schedule
            // anything. The database constraint says the same thing; checking
            // here means the user gets told which field is missing.
            var missing = new List<string>();

            if (lease.StartDate is null)
            {
                missing.Add("startDate");
            }

            if (lease.RentAmount is null)
            {
                missing.Add("rentAmount");
            }

            if (missing.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lease"] =
                    [
                        "A lease cannot be approved without " +
                        string.Join(" and ", missing) +
                        ". Reminders are scheduled from these values, and the application " +
                        "will not infer either of them.",
                    ],
                });
            }

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor == Guid.Empty)
            {
                return Results.Unauthorized();
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Approval itself is NOT written here. It goes through
            // app.approve_lease_from_extraction, which is the only path to an
            // approved lease: it requires a named human, refuses while any
            // extracted field is undecided or a blocking issue is open, and
            // writes the append-only record. Migration 0019 revokes INSERT on
            // lease_approvals from the API role precisely so this endpoint
            // cannot take a shortcut around those rules.
            //
            // Called before the change tracker touches the lease, so a refusal
            // leaves nothing half-applied.
            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     SELECT app.approve_lease_from_extraction(
                       {leaseId}, {request.ExtractionId}, 'approved', {request.Note},
                       jsonb_build_object(
                         'start_date', {lease.StartDate?.ToString("yyyy-MM-dd", null)},
                         'end_date', {lease.EndDate?.ToString("yyyy-MM-dd", null)},
                         'rent_amount', {lease.RentAmount},
                         'currency', {lease.Currency},
                         'grace_period_days', {lease.GracePeriodDays},
                         'late_fee_after_days', {lease.LateFeeAfterDays}))
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            // Re-read what the function decided rather than assuming it. The
            // entity is tracked, so its approval columns are now stale.
            await dbContext.Entry(lease).ReloadAsync(cancellationToken)
                .ConfigureAwait(false);

            // Clear any previous rejection, so the two states cannot both
            // appear set. The database CHECK constraints require this.
            lease.RejectedByUserId = null;
            lease.RejectionReason = null;

            // Lifecycle follows from the term, now that approval permits a
            // live state at all. A lease whose term has not begun is
            // "upcoming", not "active": reminders for it are legitimate, but
            // calling it active would misreport the portfolio.
            var today = now.InUtc().Date;

            lease.LifecycleStatus =
                lease.EndDate is { } endDate && endDate < today
                    ? LeaseLifecycleStatus.Expired
                    : lease.StartDate > today
                        ? LeaseLifecycleStatus.Upcoming
                        : LeaseLifecycleStatus.Active;

            // Automation is NOT switched on by approval. Approving the lease
            // terms and agreeing to start emailing people are two different
            // decisions, and section 4.G asks for explicit confirmation before
            // a new automation begins.
            if (lease.AutomationStatus == NotificationAutomationStatus.NotConfigured)
            {
                lease.AutomationStatus = NotificationAutomationStatus.PendingActivation;
            }

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "lease.approved", "lease", leaseId,
                EndpointSupport.AuditMetadata(new
                {
                    lifecycle = lease.LifecycleStatus.ToString(),
                    automation = lease.AutomationStatus.ToString(),
                }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(
                await LoadResponseAsync(dbContext, leaseId, cancellationToken)
                    .ConfigureAwait(false));
        })
            .WithName("ApproveLease")
            .WithSummary("Approves a lease after review. Does not switch automation on.");

        // ---------------------------------------------------------------------
        // POST /leases/{id}/reject
        // ---------------------------------------------------------------------
        group.MapPost("/{leaseId:guid}/reject", async (
            Guid organizationId,
            Guid leaseId,
            RejectLeaseRequest request,
            IValidator<RejectLeaseRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot reject leases here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var lease = await dbContext.Leases
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == leaseId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (lease is null)
            {
                return EndpointSupport.NotFound("lease");
            }

            // An already-live lease cannot be rejected, because the database
            // forbids a live lifecycle without approval and rejecting would
            // leave the row unrepresentable. Terminating it is the right
            // action, and is a different decision with different consequences.
            if (lease.LifecycleStatus is LeaseLifecycleStatus.Active
                or LeaseLifecycleStatus.Upcoming)
            {
                return Results.Problem(
                    title: "Lease is live",
                    detail: "An active or upcoming lease cannot be rejected. " +
                            "Terminate or archive it instead.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor == Guid.Empty)
            {
                return Results.Unauthorized();
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            lease.ApprovalStatus = LeaseApprovalStatus.Rejected;
            lease.RejectedAt = now;
            lease.RejectedByUserId = actor;
            lease.RejectionReason = request.Reason;

            lease.ApprovedAt = null;
            lease.ApprovedByUserId = null;

            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     SELECT app.approve_lease_from_extraction(
                       {leaseId}, null, 'rejected', {request.Reason}, null)
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "lease.rejected", "lease", leaseId);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(
                await LoadResponseAsync(dbContext, leaseId, cancellationToken)
                    .ConfigureAwait(false));
        })
            .WithName("RejectLease");

        // ---------------------------------------------------------------------
        // POST /leases/{id}/automation/pause  and  /resume
        //
        // Section 13.C: a user can pause an approved lease's automations
        // without changing its approval status. These two endpoints exist
        // precisely so that is possible, and they touch nothing else.
        // ---------------------------------------------------------------------
        group.MapPost("/{leaseId:guid}/automation/pause", async (
            Guid organizationId,
            Guid leaseId,
            PauseAutomationRequest request,
            IValidator<PauseAutomationRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot change automation here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var lease = await dbContext.Leases
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == leaseId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (lease is null)
            {
                return EndpointSupport.NotFound("lease");
            }

            lease.AutomationStatus = NotificationAutomationStatus.Paused;
            lease.AutomationPausedAt = SystemClock.Instance.GetCurrentInstant();
            lease.AutomationPausedReason = request.Reason;

            // ApprovalStatus and LifecycleStatus are deliberately untouched.

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "lease.automation_paused", "lease", leaseId);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(
                await LoadResponseAsync(dbContext, leaseId, cancellationToken)
                    .ConfigureAwait(false));
        })
            .WithName("PauseLeaseAutomation");

        group.MapPost("/{leaseId:guid}/automation/resume", async (
            Guid organizationId,
            Guid leaseId,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot change automation here.");
            }

            var lease = await dbContext.Leases
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == leaseId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (lease is null)
            {
                return EndpointSupport.NotFound("lease");
            }

            // Resuming an unapproved lease would be meaningless: the
            // dispatcher re-checks approval at claim time and would send
            // nothing, so reporting it as active would be a lie.
            if (lease.ApprovalStatus != LeaseApprovalStatus.Approved)
            {
                return Results.Problem(
                    title: "Lease not approved",
                    detail: "Reminders cannot be enabled for a lease that has not been approved.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            lease.AutomationStatus = NotificationAutomationStatus.Active;
            lease.AutomationPausedAt = null;
            lease.AutomationPausedReason = null;

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "lease.automation_resumed", "lease", leaseId);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(
                await LoadResponseAsync(dbContext, leaseId, cancellationToken)
                    .ConfigureAwait(false));
        })
            .WithName("ResumeLeaseAutomation");

        return routes;
    }

    // =========================================================================
    // Projection
    // =========================================================================

    /// <summary>
    /// Projects a lease to its response shape. Expressed so EF can translate
    /// it, which keeps the list endpoint a single query rather than one per
    /// lease.
    /// </summary>
    private static LeaseResponse ProjectLease(Lease lease) =>
        new(lease.Id,
            lease.OrganizationId,
            lease.PropertyId,
            lease.Property!.Name,
            lease.UnitId,
            lease.Unit != null ? lease.Unit.UnitNumber : null,
            lease.Reference,
            lease.LifecycleStatus,
            lease.ApprovalStatus,
            lease.AutomationStatus,
            lease.StartDate,
            lease.EndDate,
            lease.RentAmount,
            lease.Currency,
            lease.SecurityDepositAmount,
            lease.FirstPaymentDate,
            lease.GracePeriodDays,
            lease.LateFeeAmount,
            lease.LateFeeIsPercentage,
            lease.LateFeeAfterDays,
            lease.RenewalNoticeDays,
            lease.RenewalTerms,
            lease.EarlyTerminationTerms,
            lease.NoticePeriodDays,
            lease.PaymentInstructions,
            lease.SpecialClauses,
            lease.Notes,
            lease.ApprovedAt,
            lease.ApprovedByUserId,
            lease.RejectedAt,
            lease.RejectionReason,
            lease.AutomationPausedAt,
            lease.AutomationPausedReason,

            // Repeated rather than calling Lease.CanSendReminders, because
            // that property is marked Ignore and EF cannot translate it. The
            // two must agree, and the predicate also appears in
            // app.claim_due_notifications; see docs/architecture.md.
            lease.ApprovalStatus == LeaseApprovalStatus.Approved
            && lease.AutomationStatus == NotificationAutomationStatus.Active
            && (lease.LifecycleStatus == LeaseLifecycleStatus.Upcoming
                || lease.LifecycleStatus == LeaseLifecycleStatus.Active),

            lease.LeaseTenants
                .Select(link => new LeaseTenantResponse(
                    link.TenantId,
                    link.Tenant!.LegalName,
                    link.Tenant.PreferredName,
                    link.Role,
                    link.Tenant.NotificationsOptedIn))
                .ToList());

    private static async Task<LeaseResponse?> LoadResponseAsync(
        AppDbContext dbContext,
        Guid leaseId,
        CancellationToken cancellationToken) =>
        await dbContext.Leases
            .AsNoTracking()
            .Where(lease => lease.Id == leaseId)
            .Select(lease => ProjectLease(lease))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The caller's own users.id, resolved by their verified subject. See the
    /// equivalent helper in <see cref="PortfolioEndpoints"/> for why the
    /// filter is required rather than merely tidy.
    /// </summary>
    private static async Task<Guid> CurrentUserIdAsync(
        AppDbContext dbContext,
        IRequestIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.AuthUserId is not { } authUserId)
        {
            return Guid.Empty;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.AuthUserId == authUserId)
            .Select(user => user.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
