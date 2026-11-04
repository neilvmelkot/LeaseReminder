using FluentValidation;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Properties, units, and tenants.
///
/// <para>
/// All three follow the same shape, so the pattern is worth reading once:
/// check membership, check the write tier for a mutation, validate, act,
/// audit. Isolation is not in that list because it is not this layer's job;
/// see <see cref="EndpointSupport"/>.
/// </para>
///
/// <para>
/// Deletes are restricted rather than cascading. The database uses ON DELETE
/// RESTRICT from leases to units and tenants on purpose: section 6.B requires
/// lease history to be preserved rather than overwritten, and silently
/// removing a tenant along with their lease record would destroy exactly the
/// history a landlord needs later.
/// </para>
/// </summary>
public static class PortfolioEndpoints
{
    // =========================================================================
    // Properties
    // =========================================================================

    public static IEndpointRouteBuilder MapPropertyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/properties");

        group.MapGet("/", async (
            Guid organizationId,
            // Archived properties are excluded by default: they are not part
            // of the portfolio any more. They stay reachable so somebody can
            // find one and restore it, which is the only reason to ask.
            bool? includeArchived,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var properties = await dbContext.Properties
                .AsNoTracking()
                .Where(property => property.OrganizationId == organizationId)
                .Where(property => includeArchived == true || property.ArchivedAt == null)
                .OrderBy(property => property.Name)
                .Select(property => new PropertyResponse(
                    property.Id,
                    property.OrganizationId,
                    property.Name,
                    property.PropertyType,
                    property.TimeZone,
                    property.AddressLine1,
                    property.AddressLine2,
                    property.Locality,
                    property.Region,
                    property.PostalCode,
                    property.CountryCode,
                    property.Notes,
                    dbContext.Units.Count(unit => unit.PropertyId == property.Id),

                    // "Active" means a lease that is both approved and in a
                    // live lifecycle state. A pending lease is not an active
                    // one, however complete its terms look.
                    dbContext.Leases.Count(lease =>
                        lease.PropertyId == property.Id
                        && lease.ApprovalStatus == LeaseApprovalStatus.Approved
                        && lease.LifecycleStatus == LeaseLifecycleStatus.Active),
                    property.ArchivedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(properties);
        })
            .WithName("ListProperties");

        group.MapPost("/", async (
            Guid organizationId,
            UpsertPropertyRequest request,
            IValidator<UpsertPropertyRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            if (!EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden(
                    $"The {role} role cannot create properties.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var currentUser = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            var now = SystemClock.Instance.GetCurrentInstant();

            var property = new RentalProperty
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Name = request.Name.Trim(),
                PropertyType = request.PropertyType,
                TimeZone = request.TimeZone,
                AddressLine1 = request.AddressLine1,
                AddressLine2 = request.AddressLine2,
                Locality = request.Locality,
                Region = request.Region,
                PostalCode = request.PostalCode,
                CountryCode = request.CountryCode ?? "US",
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = currentUser == Guid.Empty ? null : currentUser,
            };

            dbContext.Properties.Add(property);

            EndpointSupport.Audit(
                dbContext, organizationId, currentUser, identity.CorrelationId,
                "property.created", "property", property.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/properties/{property.Id}",
                ToResponse(property, unitCount: 0, activeLeaseCount: 0));
        })
            .WithName("CreateProperty");

        group.MapGet("/{propertyId:guid}", async (
            Guid organizationId,
            Guid propertyId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var property = await dbContext.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == propertyId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (property is null)
            {
                return EndpointSupport.NotFound("property");
            }

            var unitCount = await dbContext.Units
                .CountAsync(unit => unit.PropertyId == propertyId, cancellationToken)
                .ConfigureAwait(false);

            var activeLeaseCount = await dbContext.Leases
                .CountAsync(
                    lease => lease.PropertyId == propertyId
                             && lease.ApprovalStatus == LeaseApprovalStatus.Approved
                             && lease.LifecycleStatus == LeaseLifecycleStatus.Active,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToResponse(property, unitCount, activeLeaseCount));
        })
            .WithName("GetProperty");

        group.MapPatch("/{propertyId:guid}", async (
            Guid organizationId,
            Guid propertyId,
            UpsertPropertyRequest request,
            IValidator<UpsertPropertyRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot modify properties here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var property = await dbContext.Properties
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == propertyId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (property is null)
            {
                return EndpointSupport.NotFound("property");
            }

            // Changing the zone changes how every existing date rule for this
            // property is read. Existing rent schedules snapshot their own
            // zone precisely so that past due dates do not move underneath
            // them, but future generation will follow the new value, so the
            // change is audited with both sides recorded.
            var previousTimeZone = property.TimeZone;

            property.Name = request.Name.Trim();
            property.PropertyType = request.PropertyType;
            property.TimeZone = request.TimeZone;
            property.AddressLine1 = request.AddressLine1;
            property.AddressLine2 = request.AddressLine2;
            property.Locality = request.Locality;
            property.Region = request.Region;
            property.PostalCode = request.PostalCode;
            property.CountryCode = request.CountryCode ?? property.CountryCode;
            property.Notes = request.Notes;

            var actor = await CurrentUserIdAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            EndpointSupport.Audit(
                dbContext, organizationId, actor, identity.CorrelationId,
                "property.updated", "property", property.Id,
                previousTimeZone == request.TimeZone
                    ? "{}"
                    : EndpointSupport.AuditMetadata(new
                    {
                        timezone = new { from = previousTimeZone, to = request.TimeZone },
                    }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(ToResponse(property, unitCount: 0, activeLeaseCount: 0));
        })
            .WithName("UpdateProperty");

        // ---------------------------------------------------------------------
        // Removing a property
        //
        // Three routes rather than one, because "get rid of this" is two
        // different operations and the caller should not have to guess which
        // one they are getting.
        //
        // DELETE removes a property that nothing references — the one added
        // with a typo in its name. The archive routes handle a property with
        // history, which cannot be deleted without taking its leases, rent
        // schedules and audit trail with it.
        //
        // Neither decision is made here. app.delete_property counts what
        // points at the property and refuses if anything does;
        // app.archive_property refuses while a lease on it is active or
        // upcoming, because reminders would otherwise keep going out for a
        // property the workspace believes is retired. This layer translates
        // the refusal into a status code and passes the database's own
        // sentence through, since it names the counts.
        // ---------------------------------------------------------------------

        group.MapPost("/{propertyId:guid}/archive", async (
            Guid propertyId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunPropertyLifecycleAsync(
                dbContext,
                $"SELECT app.archive_property({propertyId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("ArchiveProperty");

        group.MapPost("/{propertyId:guid}/restore", async (
            Guid propertyId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunPropertyLifecycleAsync(
                dbContext,
                $"SELECT app.restore_property({propertyId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("RestoreProperty");

        group.MapDelete("/{propertyId:guid}", async (
            Guid propertyId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
            await RunPropertyLifecycleAsync(
                dbContext,
                $"SELECT app.delete_property({propertyId})",
                cancellationToken)
                .ConfigureAwait(false))
            .WithName("DeleteProperty");

        return routes;
    }

    /// <summary>
    /// Calls one of the property lifecycle functions and maps its refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RaiseException is the interesting case. The function uses it for "this
    /// property still has things attached to it", and the message it raises
    /// names the counts — which is the whole value of the refusal. So the
    /// message is passed through to the caller rather than replaced with a
    /// generic one.
    /// </para>
    /// <para>
    /// That is safe to surface here: these messages are written by the
    /// migration and contain counts and nouns, never row contents. A message
    /// from any other error code is not passed through.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RunPropertyLifecycleAsync(
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
            return EndpointSupport.NotFound("property");
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.RaiseException)
        {
            // 409: the request was understood and is refusable rather than
            // wrong. A 400 would suggest the caller sent something malformed.
            return Results.Problem(
                detail: exception.MessageText,
                statusCode: StatusCodes.Status409Conflict,
                title: "That is not possible yet");
        }
    }

    // =========================================================================
    // Units
    // =========================================================================

    public static IEndpointRouteBuilder MapUnitEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup(
            "/organizations/{organizationId:guid}/properties/{propertyId:guid}/units");

        group.MapGet("/", async (
            Guid organizationId,
            Guid propertyId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var units = await dbContext.Units
                .AsNoTracking()
                .Where(unit => unit.PropertyId == propertyId
                               && unit.OrganizationId == organizationId)
                .OrderBy(unit => unit.UnitNumber)
                .Select(unit => new UnitResponse(
                    unit.Id,
                    unit.PropertyId,
                    unit.UnitNumber,
                    unit.Status,
                    unit.Bedrooms,
                    unit.Bathrooms,
                    unit.SquareFeet,
                    unit.Notes,

                    // The current lease, and only if it is genuinely live.
                    dbContext.Leases
                        .Where(lease => lease.UnitId == unit.Id
                                        && lease.ApprovalStatus == LeaseApprovalStatus.Approved
                                        && lease.LifecycleStatus == LeaseLifecycleStatus.Active)
                        .Select(lease => (Guid?)lease.Id)
                        .FirstOrDefault(),

                    dbContext.Leases
                        .Where(lease => lease.UnitId == unit.Id
                                        && lease.ApprovalStatus == LeaseApprovalStatus.Approved
                                        && lease.LifecycleStatus == LeaseLifecycleStatus.Active)
                        .SelectMany(lease => lease.LeaseTenants)
                        .OrderByDescending(leaseTenant => leaseTenant.Role == "primary")
                        .Select(leaseTenant =>
                            leaseTenant.Tenant!.PreferredName ?? leaseTenant.Tenant.LegalName)
                        .FirstOrDefault()))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(units);
        })
            .WithName("ListUnits");

        group.MapPost("/", async (
            Guid organizationId,
            Guid propertyId,
            UpsertUnitRequest request,
            IValidator<UpsertUnitRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot create units here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            // Confirms the property exists AND belongs to this organization.
            // The composite foreign key would reject a mismatch anyway, but as
            // a constraint violation rather than a readable 404.
            var propertyExists = await dbContext.Properties
                .AnyAsync(
                    property => property.Id == propertyId
                                && property.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!propertyExists)
            {
                return EndpointSupport.NotFound("property");
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var unit = new Unit
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                PropertyId = propertyId,
                UnitNumber = request.UnitNumber.Trim(),
                Status = request.Status,
                Bedrooms = request.Bedrooms,
                Bathrooms = request.Bathrooms,
                SquareFeet = request.SquareFeet,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Units.Add(unit);

            EndpointSupport.Audit(
                dbContext, organizationId,
                await CurrentUserIdAsync(dbContext, identity, cancellationToken).ConfigureAwait(false),
                identity.CorrelationId,
                "unit.created", "unit", unit.Id);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is Npgsql.PostgresException
                      { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                return Results.Problem(
                    title: "Duplicate unit",
                    detail: $"Unit '{request.UnitNumber}' already exists in this property.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/properties/{propertyId}/units/{unit.Id}",
                new UnitResponse(
                    unit.Id, unit.PropertyId, unit.UnitNumber, unit.Status,
                    unit.Bedrooms, unit.Bathrooms, unit.SquareFeet, unit.Notes,
                    null, null));
        })
            .WithName("CreateUnit");

        return routes;
    }

    // =========================================================================
    // Tenants
    // =========================================================================

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/tenants");

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

            // Only organization members ever reach this. Section 6.C requires
            // that one tenant's details are never exposed to another, and the
            // reason that holds is that tenants are not users of this system
            // at all: there is no tenant-facing authentication.
            var tenants = await dbContext.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.OrganizationId == organizationId)
                .OrderBy(tenant => tenant.LegalName)
                .Select(tenant => new TenantResponse(
                    tenant.Id,
                    tenant.LegalName,
                    tenant.PreferredName,
                    tenant.PrimaryEmail,
                    tenant.SecondaryEmail,
                    tenant.Phone,
                    tenant.Status,
                    tenant.NotificationsOptedIn,
                    tenant.Notes,
                    dbContext.LeaseTenants.Count(link => link.TenantId == tenant.Id)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(tenants);
        })
            .WithName("ListTenants");

        group.MapPost("/", async (
            Guid organizationId,
            UpsertTenantRequest request,
            IValidator<UpsertTenantRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot create tenants here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var tenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                LegalName = request.LegalName.Trim(),
                PreferredName = request.PreferredName?.Trim(),
                PrimaryEmail = request.PrimaryEmail?.Trim(),
                SecondaryEmail = request.SecondaryEmail?.Trim(),
                Phone = request.Phone?.Trim(),
                Status = request.Status,
                NotificationsOptedIn = request.NotificationsOptedIn,

                // Consent is dated when it is given, so a later dispute can be
                // answered from the record rather than from recollection.
                NotificationsOptInAt = request.NotificationsOptedIn ? now : null,
                NotificationsOptOutAt = request.NotificationsOptedIn ? null : now,

                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Tenants.Add(tenant);

            EndpointSupport.Audit(
                dbContext, organizationId,
                await CurrentUserIdAsync(dbContext, identity, cancellationToken).ConfigureAwait(false),
                identity.CorrelationId,
                "tenant.created", "tenant", tenant.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/tenants/{tenant.Id}",
                new TenantResponse(
                    tenant.Id, tenant.LegalName, tenant.PreferredName,
                    tenant.PrimaryEmail, tenant.SecondaryEmail, tenant.Phone,
                    tenant.Status, tenant.NotificationsOptedIn, tenant.Notes, 0));
        })
            .WithName("CreateTenant");

        return routes;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static PropertyResponse ToResponse(
        RentalProperty property,
        int unitCount,
        int activeLeaseCount) =>
        new(property.Id,
            property.OrganizationId,
            property.Name,
            property.PropertyType,
            property.TimeZone,
            property.AddressLine1,
            property.AddressLine2,
            property.Locality,
            property.Region,
            property.PostalCode,
            property.CountryCode,
            property.Notes,
            unitCount,
            activeLeaseCount,
            property.ArchivedAt);

    /// <summary>
    /// The caller's own users.id, resolved by their verified subject.
    ///
    /// <para>
    /// The filter on AuthUserId is essential and not merely tidy. The users
    /// RLS policy deliberately lets a member read the rows of people sharing
    /// an organization, so that "approved by" can show a name. An unfiltered
    /// FirstOrDefault would therefore return an arbitrary co-member, and every
    /// audit entry would be attributed to the wrong person. The audit_logs
    /// INSERT policy would reject it, so the bug would surface as a failed
    /// write rather than falsified history, but relying on that would be
    /// leaving a trap for the next change.
    /// </para>
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
