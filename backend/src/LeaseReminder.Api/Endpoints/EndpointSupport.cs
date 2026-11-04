using FluentValidation;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Shared helpers for the endpoint modules.
///
/// <para>
/// The authorization story here is worth stating plainly, because it looks too
/// thin at first glance. Handlers take an organizationId from the route and do
/// not re-derive it from the token. That is safe, and it is safe for one
/// specific reason: Row Level Security filters every statement this process
/// issues. A forged organizationId does not grant access; it selects zero rows
/// and fails any insert.
/// </para>
///
/// <para>
/// <see cref="RequireMembershipAsync"/> exists anyway. Not because RLS is
/// insufficient, but because without it a forged id produces an empty list and
/// a 200, which is indistinguishable from "you have no properties". Checking
/// membership turns a silent empty result into an explicit 403, which is both
/// honest to the caller and visible in the permission-denial monitoring that
/// section 4.K asks for. It is a diagnostics and auditing measure layered on
/// top of the real control, not the real control.
/// </para>
/// </summary>
internal static class EndpointSupport
{
    /// <summary>
    /// Resolves the caller's application user row, creating it on first sign
    /// in.
    ///
    /// <para>
    /// The identity provider owns authentication; this table owns the profile.
    /// A user who has signed up but never called the API has no row yet, and
    /// the RLS policy users_insert_self permits them to create exactly one,
    /// with exactly the subject their verified token carries.
    /// </para>
    /// </summary>
    internal static async Task<User?> ResolveCurrentUserAsync(
        AppDbContext dbContext,
        IRequestIdentity identity,
        string? emailFromToken,
        CancellationToken cancellationToken)
    {
        if (identity.AuthUserId is not { } authUserId)
        {
            return null;
        }

        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.AuthUserId == authUserId, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            return user;
        }

        // The email is only a display value; the subject is the identity. A
        // token with no email claim still produces a usable row.
        user = new User
        {
            Id = Guid.CreateVersion7(),
            AuthUserId = authUserId,
            Email = emailFromToken ?? $"{authUserId:N}@unknown.invalid",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        };

        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return user;
    }

    /// <summary>
    /// The caller, as the database functions need them: an id to attribute the
    /// change to, and a display name to put on the timeline.
    /// </summary>
    internal sealed record ActorInfo(Guid Id, string DisplayName);

    /// <summary>
    /// The caller's own user row, resolved by their verified subject.
    ///
    /// <para>
    /// The filter on AuthUserId is essential, not tidy. The users RLS policy
    /// deliberately lets a member read the rows of people sharing an
    /// organization, so that "approved by" can show a name. An unfiltered
    /// FirstOrDefault would therefore return an arbitrary colleague, and every
    /// task timeline entry and audit record would be attributed to the wrong
    /// person.
    /// </para>
    ///
    /// <para>
    /// The display name is persisted onto the timeline rather than joined at
    /// read time, so a comment still shows an author after that user is
    /// removed from the workspace.
    /// </para>
    /// </summary>
    internal static async Task<ActorInfo?> CurrentUserAsync(
        AppDbContext dbContext,
        IRequestIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.AuthUserId is not { } authUserId)
        {
            return null;
        }

        var actor = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.AuthUserId == authUserId)
            .Select(user => new { user.Id, user.DisplayName, user.Email })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (actor is null)
        {
            return null;
        }

        // Falls back to the email, then to a placeholder: a timeline entry
        // reading "null commented" is worse than one reading an address.
        return new ActorInfo(
            actor.Id,
            actor.DisplayName ?? actor.Email ?? "Unknown user");
    }

    /// <summary>
    /// Confirms the caller belongs to the organization, and returns their role.
    /// See the class remarks for why this is a diagnostics layer rather than
    /// the isolation boundary.
    /// </summary>
    internal static async Task<OrgRole?> RequireMembershipAsync(
        AppDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        // This query is itself RLS-filtered, so it can only ever see
        // memberships of organizations the caller belongs to.
        var membership = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.OrganizationId == organizationId)
            .Select(member => (OrgRole?)member.Role)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return membership;
    }

    /// <summary>Roles permitted to create or modify portfolio data.</summary>
    internal static bool CanWrite(OrgRole role) =>
        role is OrgRole.Owner or OrgRole.Admin or OrgRole.PropertyManager;

    /// <summary>Roles permitted to change organization settings and connected accounts.</summary>
    internal static bool CanAdminister(OrgRole role) =>
        role is OrgRole.Owner or OrgRole.Admin;

    /// <summary>
    /// Runs a validator and returns a problem result on failure.
    ///
    /// Returns null on success, so a handler reads as
    /// <c>if (Validate(...) is { } problem) return problem;</c>
    /// </summary>
    internal static IResult? Validate<T>(IValidator<T> validator, T request)
    {
        var result = validator.Validate(request);

        if (result.IsValid)
        {
            return null;
        }

        // Grouped by field, which is what a form needs to show messages inline.
        var errors = result.Errors
            .GroupBy(error => error.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        return Results.ValidationProblem(errors);
    }

    /// <summary>
    /// A 403 that does not confirm whether the resource exists.
    ///
    /// Used for a membership failure. Returning 404 would be marginally more
    /// private, but it would also hide genuine permission problems from the
    /// user and from the monitoring in section 4.K; a caller who is not a
    /// member learns only that they are not a member.
    /// </summary>
    internal static IResult Forbidden(string detail) =>
        Results.Problem(
            title: "Forbidden",
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden);

    internal static IResult NotFound(string what) =>
        Results.Problem(
            title: "Not found",
            detail: $"No {what} was found.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Records a privileged action in the audit log (section 4.I).
    ///
    /// <para>
    /// The metadata parameter is named to make the rule obvious at the call
    /// site: identifiers and before/after summaries only. Never document text,
    /// never a token, never a full email body.
    /// </para>
    /// </summary>
    /// <summary>
    /// Serialises audit metadata.
    ///
    /// <para>
    /// Hand-building this JSON by string concatenation was the first version
    /// and was a mistake twice over: a value containing a quote would produce
    /// invalid JSON and fail the jsonb cast, and a value containing braces
    /// could alter the document's shape. Serialising means the caller passes
    /// an anonymous object and escaping is not their problem.
    /// </para>
    ///
    /// <para>
    /// What goes in is still the caller's responsibility: identifiers and
    /// before/after summaries, never document text, tokens, or email bodies.
    /// </para>
    /// </summary>
    internal static string AuditMetadata(object metadata) =>
        System.Text.Json.JsonSerializer.Serialize(metadata);

    internal static void Audit(
        AppDbContext dbContext,
        Guid organizationId,
        Guid actorUserId,
        Guid correlationId,
        string action,
        string entityType,
        Guid? entityId,
        string safeMetadataJson = "{}")
    {
        dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            ActorKind = "user",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            SafeMetadataJson = safeMetadataJson,
            CorrelationId = correlationId,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });
    }
}
