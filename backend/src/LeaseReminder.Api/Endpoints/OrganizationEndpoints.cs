using System.Security.Claims;
using FluentValidation;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Current user, and organization creation.
///
/// <para>
/// Section 13.A and 13.F ask for an organization-ready model with no
/// team-management UI in the first release. So this module covers exactly the
/// two things the first release needs, creating the first workspace and
/// reporting what the caller belongs to, and deliberately omits invitations,
/// role changes, and member removal. The schema and the RLS policies already
/// support all three; adding the endpoints is a later, small change.
/// </para>
/// </summary>
public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // ---------------------------------------------------------------------
        // GET /me
        // The frontend's first call. Provisions the user row on first sign in
        // and reports their organizations, so the client never has to guess
        // whether onboarding is needed.
        // ---------------------------------------------------------------------
        routes.MapGet("/me", async (
            AppDbContext dbContext,
            IRequestIdentity identity,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var email = principal.FindFirst("email")?.Value;

            var user = await EndpointSupport
                .ResolveCurrentUserAsync(dbContext, identity, email, cancellationToken)
                .ConfigureAwait(false);

            if (user is null)
            {
                return Results.Unauthorized();
            }

            // RLS restricts this to the caller's own memberships, so no
            // additional filter by user id is needed or useful.
            // Ordered BEFORE the projection, on the entity, not after it on the
            // constructed record. EF Core cannot translate an ordering over a
            // record it has been asked to build, and fails the whole query at
            // runtime rather than at compile time.
            var organizations = await dbContext.OrganizationMembers
                .AsNoTracking()
                .Where(member => member.UserId == user.Id)
                .OrderBy(member => member.Organization!.Name)
                .Select(member => new OrganizationResponse(
                    member.Organization!.Id,
                    member.Organization.Name,
                    member.Organization.TimeZone,
                    member.Role))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new CurrentUserResponse(
                user.Id, user.Email, user.DisplayName, organizations));
        })
            .WithName("GetCurrentUser")
            .WithSummary("The signed-in user and the organizations they belong to.");

        // ---------------------------------------------------------------------
        // PATCH /organizations/{id}
        //
        // Renaming a workspace, or changing the time zone new properties
        // inherit. Administrators only: both are visible to everybody in it,
        // and the time zone decides what date a reminder is sent on.
        // ---------------------------------------------------------------------
        routes.MapPatch("/organizations/{organizationId:guid}", async (
            Guid organizationId,
            UpdateOrganizationRequest request,
            IValidator<UpdateOrganizationRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            if (EndpointSupport.Validate(validator, request) is { } invalid)
            {
                return invalid;
            }

            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can change workspace settings.");
            }

            var organization = await dbContext.Organizations
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (organization is null)
            {
                return EndpointSupport.NotFound("organization");
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                // Unreachable in practice: the membership check above already
                // resolved this caller. Handled rather than forced, because an
                // audit entry attributed to nobody is worse than a refusal.
                return Results.Unauthorized();
            }

            organization.Name = request.Name.Trim();

            // Existing properties keep the zone they were created with. This
            // is the default a NEW one inherits, and silently moving every
            // property would move every due date with it.
            organization.TimeZone = request.TimeZone;

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "organization.updated", "organization", organizationId,
                EndpointSupport.AuditMetadata(new { name = organization.Name }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(new OrganizationResponse(
                organization.Id, organization.Name, organization.TimeZone, role.Value));
        })
            .WithName("UpdateOrganization")
            .WithSummary("Renames a workspace or changes its default time zone.");

        // ---------------------------------------------------------------------
        // POST /organizations
        // ---------------------------------------------------------------------
        routes.MapPost("/organizations", async (
            CreateOrganizationRequest request,
            IValidator<CreateOrganizationRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
        {
            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var email = principal.FindFirst("email")?.Value;

            var user = await EndpointSupport
                .ResolveCurrentUserAsync(dbContext, identity, email, cancellationToken)
                .ConfigureAwait(false);

            if (user is null)
            {
                return Results.Unauthorized();
            }

            // Who may create a workspace at all.
            //
            // This was open to any authenticated account, which made removal
            // meaningless: somebody taken out of a workspace still had an
            // account, and the first thing the dashboard offered them was
            // "create your workspace". They left with their own tenancy
            // instead of with nothing.
            //
            // Two cases are legitimate. The very first user of a deployment
            // has nobody to invite them — the same exception the sign-in page
            // makes — and somebody who already administers a workspace may
            // want a second. Everyone else joins by invitation.
            //
            // bootstrap_needed is used rather than counting organizations
            // here, because this connection runs under RLS: a user who
            // belongs to nothing SEES no organizations, so a count would
            // report an empty deployment and wave them straight through.
            // That function is SECURITY DEFINER and answers for the whole
            // database.
            var isFirstEverUser = await dbContext.Database
                .SqlQuery<bool>($"SELECT app.bootstrap_needed() AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            var alreadyAdministers = await dbContext.OrganizationMembers
                .AnyAsync(
                    membership => membership.UserId == user.Id
                                  && (membership.Role == OrgRole.Owner
                                      || membership.Role == OrgRole.Admin),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!isFirstEverUser && !alreadyAdministers)
            {
                return EndpointSupport.Forbidden(
                    "Workspaces are created by invitation. Ask an owner or "
                    + "administrator to invite you to theirs.");
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var organization = new Organization
            {
                Id = Guid.CreateVersion7(),
                Name = request.Name.Trim(),
                TimeZone = request.TimeZone,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Organizations.Add(organization);

            // The creator becomes its owner. The bootstrap RLS policy
            // organization_members_insert_first_owner permits exactly this:
            // a row for yourself, as owner, in an organization that has no
            // members yet. It cannot be used to join an existing workspace.
            dbContext.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organization.Id,
                UserId = user.Id,
                Role = OrgRole.Owner,
                CreatedAt = now,
                UpdatedAt = now,
            });

            // One transaction: an organization with no owner would be
            // unadministrable, and the single-owner unique index means a
            // retry could not fix it.
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Recipient groups, a working-day calendar, and the six published
            // email templates. Runs inside the same transaction, so a new
            // organization is never half-configured.
            await dbContext.Database
                .ExecuteSqlInterpolatedAsync(
                    $"SELECT app.seed_organization_defaults({organization.Id})",
                    cancellationToken)
                .ConfigureAwait(false);

            EndpointSupport.Audit(
                dbContext,
                organization.Id,
                user.Id,
                identity.CorrelationId,
                "organization.created",
                "organization",
                organization.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organization.Id}",
                new OrganizationResponse(
                    organization.Id,
                    organization.Name,
                    organization.TimeZone,
                    OrgRole.Owner));
        })
            .WithName("CreateOrganization")
            .WithSummary("Creates a workspace and makes the caller its owner.");

        return routes;
    }
}
