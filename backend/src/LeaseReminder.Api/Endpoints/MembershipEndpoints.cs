using System.Security.Cryptography;
using System.Text;

using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Who is in a workspace, and how they got there.
/// </summary>
/// <remarks>
/// <para>
/// Joining is by invitation. The exception is the very first user of a fresh
/// deployment, who has nobody to invite them: <c>app.bootstrap_needed</c>
/// returns true only while no user exists at all, and the sign-in page offers
/// self-registration only then.
/// </para>
/// <para>
/// <b>The token is generated here and never stored.</b> Only its SHA-256
/// digest reaches the database, so a database read — a backup, a support
/// query, a leaked dump — yields nothing redeemable. The token itself is
/// returned once, to the inviter, and is their responsibility from then on.
/// </para>
/// </remarks>
public static class MembershipEndpoints
{
    public static IEndpointRouteBuilder MapMembershipEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapBootstrap(routes);
        MapMembers(routes);
        MapInvitations(routes);

        return routes;
    }

    // =========================================================================
    // Bootstrap
    // =========================================================================

    private static void MapBootstrap(IEndpointRouteBuilder routes)
    {
        // Anonymous by necessity: the sign-in page asks before anybody has
        // signed in. It discloses one bit, whether this deployment has been
        // set up, which is apparent from the screen regardless.
        routes.MapGet("/bootstrap", async (
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var needed = await dbContext.Database
                .SqlQuery<bool>($"SELECT app.bootstrap_needed() AS \"Value\"")
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { needsFirstUser = needed });
        })
        .AllowAnonymous()
        .WithName("GetBootstrapStatus");
    }

    // =========================================================================
    // Members
    // =========================================================================

    private static void MapMembers(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/organizations/{organizationId:guid}/members", async (
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

            var members = await dbContext.Database
                .SqlQuery<MemberRow>(
                    $"""
                     SELECT
                       u.id           AS "UserId",
                       u.email::text  AS "Email",
                       u.display_name AS "DisplayName",
                       m.role         AS "Role",
                       m.created_at   AS "JoinedAt"
                     FROM organization_members m
                     JOIN users u ON u.id = m.user_id
                     WHERE m.organization_id = {organizationId}
                     ORDER BY m.created_at
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(members);
        })
        .WithName("ListMembers");

        routes.MapDelete("/organizations/{organizationId:guid}/members/{userId:guid}", async (
            Guid organizationId,
            Guid userId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.remove_member({organizationId}, {userId})",
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NoDataFound
                      || exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                return Results.Problem(
                    title: "That member could not be removed",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("RemoveMember");

        // Changing a role.
        //
        // Every rule lives in app.change_member_role rather than here:
        // ownership cannot be granted, the owner cannot be demoted, and
        // nobody may change their own role. Putting them in the function
        // means they hold for a direct call as well as for the dropdown.
        routes.MapPatch("/organizations/{organizationId:guid}/members/{userId:guid}", async (
            Guid organizationId,
            Guid userId,
            [FromBody] ChangeMemberRoleRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                // Cast for the same reason as create_invitation: the function
                // takes text, and sending the enum lets Npgsql apply the name
                // translation so 'property_manager' arrives rather than the
                // browser's camelCase.
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.change_member_role({organizationId}, {userId}, {request.Role}::text)",
                        cancellationToken)
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
                return EndpointSupport.NotFound("member");
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.RaiseException
                      || exception.SqlState == "22023")
            {
                return Results.Problem(
                    title: "That role could not be set",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("ChangeMemberRole");
    }

    // =========================================================================
    // Invitations
    // =========================================================================

    private static void MapInvitations(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/organizations/{organizationId:guid}/invitations", async (
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

            // The digest is deliberately absent from this projection. It is
            // not a usable credential, but there is no reason for it to leave
            // the database either.
            var invitations = await dbContext.Database
                .SqlQuery<InvitationRow>(
                    $"""
                     SELECT
                       i.id          AS "Id",
                       i.email::text AS "Email",
                       i.role        AS "Role",
                       i.created_at  AS "CreatedAt",
                       i.expires_at  AS "ExpiresAt",
                       i.accepted_at AS "AcceptedAt",
                       i.revoked_at  AS "RevokedAt"
                     FROM organization_invitations i
                     WHERE i.organization_id = {organizationId}
                     ORDER BY i.created_at DESC
                     LIMIT 50
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(invitations);
        })
        .WithName("ListInvitations");

        routes.MapPost("/organizations/{organizationId:guid}/invitations", async (
            Guid organizationId,
            [FromBody] CreateInvitationRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.Problem(
                    title: "An email address is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 32 bytes of randomness, base64url. The token exists in this
            // method and in the response; only its digest is persisted.
            var token = ToBase64Url(RandomNumberGenerator.GetBytes(32));
            var digest = SHA256.HashData(Encoding.ASCII.GetBytes(token));

            try
            {
                // The role is cast to text because app.create_invitation
                // takes text, not app.org_role. It is still SENT as the enum,
                // so Npgsql applies the same name translation it uses
                // everywhere else and the cast yields 'property_manager' —
                // where passing the raw request string sent the browser's
                // camelCase "propertyManager" and the enum input rejected it.
                var id = await dbContext.Database
                    .SqlQuery<Guid>(
                        $"""
                         SELECT app.create_invitation(
                           {organizationId}, {request.Email.Trim()},
                           {request.Role ?? OrgRole.ReadOnly}::text, {digest},
                           {request.ExpiresInDays ?? 7}) AS "Value"
                         """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new
                {
                    invitationId = id,
                    // Returned once. There is no endpoint that will produce it
                    // again, because the server no longer has it.
                    token,
                });
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                      || exception.SqlState == PostgresErrorCodes.InvalidParameterValue
                      || exception.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return Results.Problem(
                    title: "That invitation could not be created",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("CreateInvitation");

        routes.MapDelete("/invitations/{invitationId:guid}", async (
            Guid invitationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.revoke_invitation({invitationId})",
                        cancellationToken)
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
                return EndpointSupport.NotFound("invitation");
            }
        })
        .WithName("RevokeInvitation");

        // What the invite page shows before anybody signs in.
        routes.MapGet("/invitations/describe", async (
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            [FromQuery] string? token) =>
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.Problem(
                    title: "A token is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var digest = SHA256.HashData(Encoding.ASCII.GetBytes(token));

            var described = await dbContext.Database
                .SqlQuery<DescribedInvitation>(
                    $"""
                     SELECT
                       organization_name AS "OrganizationName",
                       email             AS "Email",
                       role::app.org_role AS "Role",
                       expires_at        AS "ExpiresAt",
                       is_valid          AS "IsValid"
                     FROM app.describe_invitation({digest})
                     """)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // An unknown token and a spent one give the same answer, so this
            // cannot be used to discover which tokens exist.
            if (described is null || !described.IsValid)
            {
                return Results.Ok(new { isValid = false });
            }

            return Results.Ok(new
            {
                isValid = true,
                described.OrganizationName,
                described.Email,
                described.Role,
                described.ExpiresAt,
            });
        })
        .AllowAnonymous()
        .WithName("DescribeInvitation");

        routes.MapPost("/invitations/accept", async (
            [FromBody] AcceptInvitationRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.Problem(
                    title: "A token is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var digest = SHA256.HashData(Encoding.ASCII.GetBytes(request.Token));

            try
            {
                var organizationId = await dbContext.Database
                    .SqlQuery<Guid>(
                        $"SELECT app.accept_invitation({digest}) AS \"Value\"")
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new { organizationId });
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege
                      || exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                // The database gives one message for every invalid case, so
                // this cannot distinguish "wrong address" from "expired" for
                // somebody probing. It does distinguish them for the person
                // who was actually invited, which is the point.
                return Results.Problem(
                    title: "That invitation could not be accepted",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("AcceptInvitation");
    }

    /// <summary>Base64url: no padding, URL-safe alphabet.</summary>
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>A member of a workspace.</summary>
public sealed record MemberRow(
    Guid UserId,
    string Email,
    string? DisplayName,
    // The enum, not role::text. These projections returned the raw database
    // spelling while the rest of the API serialises OrgRole in camelCase, so
    // the frontend's roleLabel() matched "owner" and "admin" -- identical in
    // both -- and fell through to the raw value for the other three. An
    // invitee was told they had been invited as "property_manager".
    OrgRole Role,
    DateTime JoinedAt);

/// <summary>A pending or settled invitation. Carries no token.</summary>
public sealed record InvitationRow(
    Guid Id,
    string Email,
    OrgRole Role,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? RevokedAt);

/// <summary>What the invite page needs before sign-in.</summary>
public sealed record DescribedInvitation(
    string OrganizationName,
    string Email,
    OrgRole Role,
    DateTime ExpiresAt,
    bool IsValid);

/// <param name="Role">
/// Anything but owner. Ownership is not transferable by invitation.
/// </param>
public sealed record CreateInvitationRequest(
    string Email,
    /// <summary>
    /// Typed as the enum, not as a string.
    /// </summary>
    /// <remarks>
    /// As a string this was interpolated straight into SQL, and the two
    /// vocabularies do not match: the API serialises this enum in camelCase,
    /// so the browser sends "propertyManager", while app.org_role spells it
    /// "property_manager". Inviting an administrator or an accountant worked
    /// because those two happen to be identical in both; inviting a property
    /// manager or a read-only member failed with
    /// <c>22P02: invalid input value for enum org_role</c>, which the global
    /// handler reported as "An unexpected error occurred."
    ///
    /// Typed, the value is parsed on the way in and Npgsql applies the same
    /// name translation it uses everywhere else, so the two spellings cannot
    /// drift apart again.
    /// </remarks>
    OrgRole? Role,
    int? ExpiresInDays);

public sealed record AcceptInvitationRequest(string Token);

/// <summary>A role change for one member.</summary>
public sealed record ChangeMemberRoleRequest(OrgRole Role);
