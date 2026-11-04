using LeaseReminder.Application.Templates;
using LeaseReminder.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Editing the wording of what gets sent.
/// </summary>
/// <remarks>
/// <para>
/// A published version is immutable, enforced by a trigger in migration 0005.
/// Editing therefore means editing a draft, and publishing means promoting
/// that draft and archiving the one it replaces. Old versions are kept, not
/// deleted, because email log entries point at the version that was actually
/// sent — and a log pointing at wording somebody has since changed is not
/// evidence of anything.
/// </para>
/// <para>
/// The preview endpoint runs <see cref="TemplateRenderer"/>, the same renderer
/// the worker uses. A second implementation would agree until it did not, and
/// the symptom would be a preview that lies about what a tenant receives.
/// </para>
/// </remarks>
public static class EmailTemplateEndpoints
{
    public static IEndpointRouteBuilder MapEmailTemplateEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/email-templates");

        MapList(group);
        MapSave(group);
        MapPreview(group);

        return routes;
    }

    private static void MapList(RouteGroupBuilder group)
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

            // Drafts and published versions only. Archived ones exist for the
            // logs that reference them and are not something to edit.
            var templates = await dbContext.Database
                .SqlQuery<EmailTemplateRow>(
                    $"""
                     SELECT
                       t.id            AS "Id",
                       t.template_key  AS "TemplateKey",
                       t.name          AS "Name",
                       t.event_type::text AS "EventType",
                       t.version       AS "Version",
                       t.status::text  AS "Status",
                       t.subject       AS "Subject",
                       t.html_body     AS "HtmlBody",
                       t.text_body     AS "TextBody",
                       t.published_at  AS "PublishedAt",
                       t.updated_at    AS "UpdatedAt"
                     FROM email_templates t
                     WHERE t.organization_id = {organizationId}
                       AND t.status IN ('draft', 'published')
                     ORDER BY t.template_key, t.status DESC
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                templates,
                // The allowlist, sent with the list so the editor can show
                // what may be used. An unknown placeholder is left as written
                // rather than substituted, so offering the real set is the
                // difference between a working template and a confusing one.
                availableVariables = TemplateRenderer.AllowedVariables.OrderBy(
                    name => name, StringComparer.Ordinal),
            });
        })
        .WithName("ListEmailTemplates");
    }

    private static void MapSave(RouteGroupBuilder group)
    {
        group.MapPut("/{templateKey}", async (
            Guid organizationId,
            string templateKey,
            [FromBody] SaveTemplateRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    title: "A template is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Which variables the body actually uses, captured at save time.
            // Stored so a later change to the available set can be checked
            // against what templates rely on, instead of discovering it when
            // a reminder renders with a hole in it.
            var referenced = TemplateRenderer
                .Scan($"{request.Subject}\n{request.HtmlBody}\n{request.TextBody}")
                .KnownVariables
                .ToArray();

            try
            {
                var id = await dbContext.Database
                    .SqlQuery<Guid>(
                        $"""
                         SELECT app.save_email_template_draft(
                           {organizationId}, {templateKey}, {request.Name},
                           {request.Subject}, {request.HtmlBody}, {request.TextBody},
                           {referenced}) AS "Value"
                         """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new { templateId = id });
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
        })
        .WithName("SaveEmailTemplateDraft");

        group.MapPost("/{templateId:guid}/publish", async (
            Guid organizationId,
            Guid templateId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.publish_email_template({templateId})",
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
                return EndpointSupport.NotFound("template");
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                return Results.Problem(
                    title: "That version cannot be published",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("PublishEmailTemplate");

        group.MapDelete("/{templateId:guid}", async (
            Guid organizationId,
            Guid templateId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.discard_email_template_draft({templateId})",
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
                when (exception.SqlState == PostgresErrorCodes.InvalidParameterValue
                      || exception.SqlState == PostgresErrorCodes.NoDataFound)
            {
                return Results.Problem(
                    title: "That version cannot be discarded",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("DiscardEmailTemplateDraft");
    }

    private static void MapPreview(RouteGroupBuilder group)
    {
        // No database access and no persistence: it renders what was typed.
        // The point is to answer "what will a tenant see?" before anything is
        // saved, let alone published.
        group.MapPost("/preview", (
            Guid organizationId,
            [FromBody] PreviewTemplateRequest request) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    title: "A template is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Representative values rather than empty ones. A preview full of
            // blanks shows the layout but not the thing people actually check,
            // which is whether the sentence reads correctly once the numbers
            // are in it.
            var sample = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenant_name"] = "Priya Raman",
                ["property_name"] = "Maple Street",
                ["unit_number"] = "2",
                ["rent_amount"] = "2,400.00",
                ["currency"] = "USD",
                ["due_date"] = "1 June 2026",
                ["days_until_due"] = "3",
                ["lease_start_date"] = "1 June 2026",
                ["lease_end_date"] = "31 May 2027",
                ["landlord_name"] = "Dana Owner",
                ["organization_name"] = "Your workspace",
                ["grace_period_end_date"] = "5 June 2026",
                ["late_fee_amount"] = "50.00",
                ["payment_instructions"] = "Bank transfer, reference MAPLE-2",
            };

            // Only the HTML part escapes. A tenant called "Smith & Co" must
            // appear as typed in the plain-text body and the subject, and as
            // an entity in the HTML one; escaping all three would show the
            // entity to anyone reading the text alternative, and escaping
            // none would let a name introduce markup.
            var subject = TemplateRenderer.Render(
                request.Subject ?? string.Empty, sample, escapeForHtml: false);
            var html = TemplateRenderer.Render(
                request.HtmlBody ?? string.Empty, sample, escapeForHtml: true);
            var text = TemplateRenderer.Render(
                request.TextBody ?? string.Empty, sample, escapeForHtml: false);

            return Results.Ok(new
            {
                subject = subject.Output,
                htmlBody = html.Output,
                textBody = text.Output,

                // Placeholders that are not on the allowlist. Reported rather
                // than silently left in place, because the author almost
                // always meant one that exists and has mistyped it.
                unknownVariables = subject.UnknownVariables
                    .Concat(html.UnknownVariables)
                    .Concat(text.UnknownVariables)
                    .Distinct(StringComparer.Ordinal),
            });
        })
        .WithName("PreviewEmailTemplate");
    }
}

/// <summary>A template version, as the editor sees it.</summary>
public sealed record EmailTemplateRow(
    Guid Id,
    string TemplateKey,
    string Name,
    string? EventType,
    int Version,
    string Status,
    string Subject,
    string HtmlBody,
    string TextBody,
    DateTime? PublishedAt,
    DateTime UpdatedAt);

/// <summary>A draft being saved.</summary>
public sealed record SaveTemplateRequest(
    string Name,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>A template being previewed, saved or not.</summary>
public sealed record PreviewTemplateRequest(
    string? Subject,
    string? HtmlBody,
    string? TextBody);
