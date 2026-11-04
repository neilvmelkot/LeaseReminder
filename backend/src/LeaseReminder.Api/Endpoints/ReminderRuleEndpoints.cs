using FluentValidation;

using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Identity;
using LeaseReminder.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Who a rent reminder is addressed to, and when it goes out.
///
/// <para>
/// The tables behind this were created in migration 0005 and have carried Row
/// Level Security since 0009. What was missing was any way to write to them,
/// which is why a workspace could have an approved lease, a rent schedule and
/// a connected mailbox and still send nothing: a reminder with no rule has no
/// trigger, and a rule with no recipients has nobody to send to.
/// </para>
///
/// <para>
/// Plain CRUD through the change tracker rather than SECURITY DEFINER
/// functions, unlike the maintenance board. The reason the board uses them is
/// that a task change has to commit with its timeline entry and its outbox
/// event; nothing here raises an event or has a timeline. RLS is the control,
/// and the audit entry is written in the same transaction as the change.
/// </para>
///
/// <para>
/// <b>Everything here is administrator-only, recipients included.</b> Rules
/// were administrator-only from the start and recipients were left at the
/// write tier, on the reasoning that an address is just a contact detail.
/// That was wrong: deleting a recipient cascades to
/// <c>notification_rule_recipients</c>, so a property manager who could not
/// edit a rule could still empty one — silently, and with the same effect as
/// switching rent reminders off. Adding one is harmless on its own, but a
/// screen where half the controls need a different role than the other half
/// is how that gap appeared in the first place.
/// </para>
/// </summary>
public static class ReminderRuleEndpoints
{
    public static IEndpointRouteBuilder MapReminderRuleEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/reminder-rules");
        var recipients = routes.MapGroup("/organizations/{organizationId:guid}/recipients");
        var groups = routes.MapGroup("/organizations/{organizationId:guid}/recipient-groups");

        MapRecipients(recipients);
        MapGroups(groups);
        MapRules(group);

        return routes;
    }

    // =========================================================================
    // Recipients
    // =========================================================================

    private static void MapRecipients(RouteGroupBuilder group)
    {
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

            var rows = await dbContext.Recipients
                .AsNoTracking()
                .Where(recipient => recipient.OrganizationId == organizationId)
                .OrderBy(recipient => recipient.DisplayName ?? recipient.Email)
                .Select(recipient => new RecipientResponse(
                    recipient.Id,
                    recipient.Email,
                    recipient.DisplayName,
                    recipient.TenantId,
                    // Named so the list can say whose address this is without
                    // a second request.
                    dbContext.Tenants
                        .Where(tenant => tenant.Id == recipient.TenantId)
                        .Select(tenant => tenant.PreferredName ?? tenant.LegalName)
                        .FirstOrDefault(),
                    // Consent lives on the tenant, not here. Surfaced so the
                    // UI can warn that adding this address to a rule will not
                    // actually reach them.
                    dbContext.Tenants
                        .Where(tenant => tenant.Id == recipient.TenantId)
                        .Select(tenant => (bool?)tenant.NotificationsOptedIn)
                        .FirstOrDefault(),
                    recipient.Active,
                    dbContext.RecipientGroupMembers
                        .Count(member => member.RecipientId == recipient.Id),
                    dbContext.NotificationRuleRecipients
                        .Count(target => target.RecipientId == recipient.Id)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(rows);
        })
            .WithName("ListRecipients");

        group.MapPost("/", async (
            Guid organizationId,
            SaveRecipientRequest request,
            IValidator<SaveRecipientRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot add recipients here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var recipient = new Recipient
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Email = request.Email.Trim(),
                DisplayName = Blank(request.DisplayName),
                TenantId = request.TenantId,
                Active = true,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Recipients.Add(recipient);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient.created", "recipient", recipient.Id);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (IsUniqueViolation(exception))
            {
                return Results.Problem(
                    title: "That address is already a recipient",
                    detail: "This workspace already has a recipient with that email "
                            + "address. Edit the existing one instead.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/recipients/{recipient.Id}",
                new { id = recipient.Id });
        })
            .WithName("CreateRecipient");

        group.MapPatch("/{recipientId:guid}", async (
            Guid organizationId,
            Guid recipientId,
            SaveRecipientRequest request,
            IValidator<SaveRecipientRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot change recipients here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var recipient = await dbContext.Recipients
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == recipientId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (recipient is null)
            {
                return EndpointSupport.NotFound("recipient");
            }

            recipient.Email = request.Email.Trim();
            recipient.DisplayName = Blank(request.DisplayName);
            recipient.TenantId = request.TenantId;
            recipient.Active = request.Active ?? recipient.Active;
            recipient.UpdatedAt = SystemClock.Instance.GetCurrentInstant();

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient.updated", "recipient", recipient.Id);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (IsUniqueViolation(exception))
            {
                return Results.Problem(
                    title: "That address is already a recipient",
                    detail: "Another recipient in this workspace already uses that "
                            + "email address.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.NoContent();
        })
            .WithName("UpdateRecipient");

        // Deleting a recipient removes it from every group and every rule by
        // ON DELETE CASCADE, which is why the list reports both counts: the
        // consequence should be visible before the click, not after it.
        group.MapDelete("/{recipientId:guid}", async (
            Guid organizationId,
            Guid recipientId,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot remove recipients here.");
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var recipient = await dbContext.Recipients
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == recipientId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (recipient is null)
            {
                return EndpointSupport.NotFound("recipient");
            }

            dbContext.Recipients.Remove(recipient);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient.deleted", "recipient", recipientId,
                EndpointSupport.AuditMetadata(new { email = recipient.Email }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("DeleteRecipient");
    }

    // =========================================================================
    // Groups
    // =========================================================================

    private static void MapGroups(RouteGroupBuilder group)
    {
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

            var rows = await dbContext.RecipientGroups
                .AsNoTracking()
                .Where(recipientGroup => recipientGroup.OrganizationId == organizationId)
                .OrderBy(recipientGroup => recipientGroup.Name)
                .Select(recipientGroup => new RecipientGroupResponse(
                    recipientGroup.Id,
                    recipientGroup.Name,
                    recipientGroup.Kind,
                    recipientGroup.Description,
                    dbContext.RecipientGroupMembers
                        .Where(member => member.RecipientGroupId == recipientGroup.Id)
                        .Select(member => member.RecipientId)
                        .ToList(),
                    dbContext.NotificationRuleRecipients
                        .Count(target => target.RecipientGroupId == recipientGroup.Id)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(rows);
        })
            .WithName("ListRecipientGroups");

        // Create and replace-membership in one call.
        //
        // Membership is the whole point of a group, and a create followed by
        // N member calls is N chances to end up with a half-built group if
        // one of them fails. One request, one transaction.
        group.MapPost("/", async (
            Guid organizationId,
            SaveRecipientGroupRequest request,
            IValidator<SaveRecipientGroupRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot add recipient groups here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var created = new RecipientGroup
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                Name = request.Name.Trim(),
                Kind = request.Kind ?? "custom",
                Description = Blank(request.Description),
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.RecipientGroups.Add(created);

            foreach (var recipientId in (request.RecipientIds ?? []).Distinct())
            {
                dbContext.RecipientGroupMembers.Add(new RecipientGroupMember
                {
                    RecipientGroupId = created.Id,
                    RecipientId = recipientId,
                    OrganizationId = organizationId,
                    CreatedAt = now,
                });
            }

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient_group.created", "recipient_group", created.Id);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (IsUniqueViolation(exception))
            {
                return Results.Problem(
                    title: "That group already exists",
                    detail: "This workspace already has a recipient group with that name.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/recipient-groups/{created.Id}",
                new { id = created.Id });
        })
            .WithName("CreateRecipientGroup");

        group.MapPut("/{groupId:guid}", async (
            Guid organizationId,
            Guid groupId,
            SaveRecipientGroupRequest request,
            IValidator<SaveRecipientGroupRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot change recipient groups here.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var existing = await dbContext.RecipientGroups
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == groupId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return EndpointSupport.NotFound("recipient group");
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            existing.Name = request.Name.Trim();
            existing.Kind = request.Kind ?? existing.Kind;
            existing.Description = Blank(request.Description);
            existing.UpdatedAt = now;

            // Membership is replaced wholesale rather than diffed. The set is
            // small, the request carries the intended state, and a diff is
            // one more place for the two to disagree.
            var current = await dbContext.RecipientGroupMembers
                .Where(member => member.RecipientGroupId == groupId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.RecipientGroupMembers.RemoveRange(current);

            foreach (var recipientId in (request.RecipientIds ?? []).Distinct())
            {
                dbContext.RecipientGroupMembers.Add(new RecipientGroupMember
                {
                    RecipientGroupId = groupId,
                    RecipientId = recipientId,
                    OrganizationId = organizationId,
                    CreatedAt = now,
                });
            }

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient_group.updated", "recipient_group", groupId);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("UpdateRecipientGroup");

        group.MapDelete("/{groupId:guid}", async (
            Guid organizationId,
            Guid groupId,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden("You cannot remove recipient groups here.");
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var existing = await dbContext.RecipientGroups
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == groupId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return EndpointSupport.NotFound("recipient group");
            }

            dbContext.RecipientGroups.Remove(existing);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "recipient_group.deleted", "recipient_group", groupId,
                EndpointSupport.AuditMetadata(new { name = existing.Name }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("DeleteRecipientGroup");
    }

    // =========================================================================
    // Rules
    // =========================================================================

    private static void MapRules(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken,
            Guid? propertyId,
            Guid? leaseId) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var query = dbContext.NotificationRules
                .AsNoTracking()
                .Where(rule => rule.OrganizationId == organizationId);

            // Asking about a lease means asking what will actually fire for
            // it, which includes the global and property rules it inherits —
            // not only the rules written at its own level. A list that showed
            // just the lease's own rules would read as "nothing is set up"
            // for a lease covered perfectly well from above.
            if (leaseId.HasValue)
            {
                query = query.Where(rule =>
                    rule.LeaseId == leaseId.Value
                    || (rule.LeaseId == null
                        && (rule.PropertyId == null
                            || dbContext.Leases.Any(lease =>
                                lease.Id == leaseId.Value
                                && lease.PropertyId == rule.PropertyId))));
            }
            else if (propertyId.HasValue)
            {
                query = query.Where(rule =>
                    rule.LeaseId == null
                    && (rule.PropertyId == propertyId.Value || rule.PropertyId == null));
            }

            var rules = await query
                // Broadest first, so the list reads the way inheritance
                // resolves: global, then property, then lease.
                .OrderBy(rule => rule.Scope == "global" ? 0 : rule.Scope == "property" ? 1 : 2)
                .ThenBy(rule => rule.EventType)
                .ThenBy(rule => rule.Priority)
                .Select(rule => new ReminderRuleResponse(
                    rule.Id,
                    rule.Scope,
                    rule.PropertyId,
                    dbContext.Properties
                        .Where(property => property.Id == rule.PropertyId)
                        .Select(property => property.Name)
                        .FirstOrDefault(),
                    rule.LeaseId,
                    rule.InheritanceMode,
                    rule.OverridesRuleId,
                    rule.EventType,
                    rule.TriggerType,
                    rule.OffsetValue,
                    rule.OffsetUnit,
                    rule.SendLocalTime,
                    rule.TimeZone,
                    rule.SendSeparately,
                    rule.MaxSendsPerDay,
                    rule.Description,
                    rule.Enabled,
                    rule.PausedAt,
                    rule.PausedReason,
                    dbContext.NotificationRuleRecipients
                        .Where(target => target.NotificationRuleId == rule.Id)
                        .Select(target => new RuleTargetResponse(
                            target.RecipientId,
                            target.RecipientGroupId,
                            target.RecipientKind,
                            target.RecipientId != null
                                ? dbContext.Recipients
                                    .Where(r => r.Id == target.RecipientId)
                                    .Select(r => r.DisplayName ?? r.Email)
                                    .FirstOrDefault()
                                : dbContext.RecipientGroups
                                    .Where(g => g.Id == target.RecipientGroupId)
                                    .Select(g => g.Name)
                                    .FirstOrDefault()))
                        .ToList()))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(rules);
        })
            .WithName("ListReminderRules");

        group.MapPost("/", async (
            Guid organizationId,
            SaveReminderRuleRequest request,
            IValidator<SaveReminderRuleRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            // Administrator, not write tier. A rule decides what lands in a
            // tenant's inbox in the landlord's name, on a schedule nobody is
            // watching — which is a different kind of decision from logging
            // a repair.
            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can change reminder rules.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // A lease-level rule has to carry its property too, because the
            // scope column is generated from both and the composite foreign
            // keys are per organization. Resolved here rather than trusted
            // from the request.
            Guid? propertyId = request.PropertyId;

            if (request.LeaseId.HasValue)
            {
                propertyId = await dbContext.Leases
                    .Where(lease => lease.Id == request.LeaseId.Value
                                    && lease.OrganizationId == organizationId)
                    .Select(lease => (Guid?)lease.PropertyId)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (propertyId is null)
                {
                    return EndpointSupport.NotFound("lease");
                }
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            var rule = new NotificationRule
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                PropertyId = propertyId,
                LeaseId = request.LeaseId,
                InheritanceMode = request.InheritanceMode,
                OverridesRuleId = request.OverridesRuleId,
                EventType = request.EventType,
                TriggerType = request.TriggerType,
                OffsetValue = request.OffsetValue,
                OffsetUnit = request.OffsetUnit,
                SendLocalTime = request.SendLocalTime,
                TimeZone = Blank(request.TimeZone),
                SendSeparately = request.SendSeparately,
                MaxSendsPerDay = request.MaxSendsPerDay ?? 10,
                Description = Blank(request.Description),
                Enabled = request.Enabled,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = actor.Id,
            };

            dbContext.NotificationRules.Add(rule);

            AddTargets(dbContext, rule.Id, organizationId, request.Targets, now);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "notification_rule.created", "notification_rule", rule.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/organizations/{organizationId}/reminder-rules/{rule.Id}",
                new { id = rule.Id });
        })
            .WithName("CreateReminderRule");

        group.MapPut("/{ruleId:guid}", async (
            Guid organizationId,
            Guid ruleId,
            SaveReminderRuleRequest request,
            IValidator<SaveReminderRuleRequest> validator,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can change reminder rules.");
            }

            if (EndpointSupport.Validate(validator, request) is { } problem)
            {
                return problem;
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var rule = await dbContext.NotificationRules
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == ruleId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rule is null)
            {
                return EndpointSupport.NotFound("reminder rule");
            }

            var now = SystemClock.Instance.GetCurrentInstant();

            // The scope is deliberately not editable. Moving a rule between
            // levels changes which leases it covers, which is a different
            // rule; the editor offers delete and create instead, so the
            // change is visible as two deliberate acts.
            rule.InheritanceMode = request.InheritanceMode;
            rule.OverridesRuleId = request.OverridesRuleId;
            rule.EventType = request.EventType;
            rule.TriggerType = request.TriggerType;
            rule.OffsetValue = request.OffsetValue;
            rule.OffsetUnit = request.OffsetUnit;
            rule.SendLocalTime = request.SendLocalTime;
            rule.TimeZone = Blank(request.TimeZone);
            rule.SendSeparately = request.SendSeparately;
            rule.MaxSendsPerDay = request.MaxSendsPerDay ?? rule.MaxSendsPerDay;
            rule.Description = Blank(request.Description);
            rule.Enabled = request.Enabled;
            rule.UpdatedAt = now;

            var current = await dbContext.NotificationRuleRecipients
                .Where(target => target.NotificationRuleId == ruleId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.NotificationRuleRecipients.RemoveRange(current);

            AddTargets(dbContext, ruleId, organizationId, request.Targets, now);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "notification_rule.updated", "notification_rule", ruleId);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("UpdateReminderRule");

        group.MapDelete("/{ruleId:guid}", async (
            Guid organizationId,
            Guid ruleId,
            AppDbContext dbContext,
            IRequestIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null || !EndpointSupport.CanAdminister(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Only an owner or administrator can remove reminder rules.");
            }

            var actor = await EndpointSupport
                .CurrentUserAsync(dbContext, identity, cancellationToken)
                .ConfigureAwait(false);

            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var rule = await dbContext.NotificationRules
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == ruleId
                                 && candidate.OrganizationId == organizationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rule is null)
            {
                return EndpointSupport.NotFound("reminder rule");
            }

            dbContext.NotificationRules.Remove(rule);

            EndpointSupport.Audit(
                dbContext, organizationId, actor.Id, identity.CorrelationId,
                "notification_rule.deleted", "notification_rule", ruleId,
                EndpointSupport.AuditMetadata(new
                {
                    scope = rule.Scope,
                    eventType = rule.EventType.ToString(),
                }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        })
            .WithName("DeleteReminderRule");
    }

    // =========================================================================
    // Shared
    // =========================================================================

    private static void AddTargets(
        AppDbContext dbContext,
        Guid ruleId,
        Guid organizationId,
        IReadOnlyList<SaveRuleTargetRequest>? targets,
        Instant now)
    {
        foreach (var target in targets ?? [])
        {
            dbContext.NotificationRuleRecipients.Add(new NotificationRuleRecipient
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                NotificationRuleId = ruleId,
                RecipientId = target.RecipientId,
                RecipientGroupId = target.RecipientGroupId,
                RecipientKind = target.RecipientKind,
                CreatedAt = now,
            });
        }
    }

    /// <summary>Empty strings become null, so a blank box clears a value.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A unique-index violation, which here always means "that name or
    /// address is taken" rather than anything the caller cannot fix.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
        };
}
