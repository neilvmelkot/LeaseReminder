using System.Globalization;

using LeaseReminder.Application.Scheduling;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using NodaTime;

using Npgsql;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Rent schedules and the reminder rules built on them.
/// </summary>
/// <remarks>
/// <para>
/// The preview endpoint is the interesting one. It runs the same
/// <see cref="RentScheduleCalculator"/> the worker runs, on a definition that
/// has not been saved, and returns the dates it would produce. So the question
/// "what will this actually do?" is answered before anything is committed,
/// by the code that will do it — not by a second implementation that agrees
/// until it does not.
/// </para>
/// </remarks>
public static class RentScheduleEndpoints
{
    public static IEndpointRouteBuilder MapRentScheduleEndpoints(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/organizations/{organizationId:guid}/leases/{leaseId:guid}");

        MapRead(group);
        MapPreview(group);
        MapSave(group);

        return routes;
    }

    // =========================================================================
    // Read
    // =========================================================================

    private static void MapRead(RouteGroupBuilder group)
    {
        group.MapGet("/rent-schedule", async (
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

            var schedule = await dbContext.Database
                .SqlQuery<RentScheduleRow>(
                    $"""
                     SELECT
                       s.id                   AS "Id",
                       s.frequency::text      AS "Frequency",
                       s.due_day_rule::text   AS "DueDayRule",
                       s.due_day_of_month     AS "DueDayOfMonth",
                       s.start_date           AS "StartDate",
                       s.end_date             AS "EndDate",
                       s.amount               AS "Amount",
                       s.currency             AS "Currency",
                       s.timezone             AS "TimeZone",
                       s.short_month_fallback::text AS "ShortMonthFallback",
                       s.business_day_shift::text   AS "BusinessDayShift",
                       s.holiday_calendar_id  AS "HolidayCalendarId",
                       s.generation_version   AS "GenerationVersion"
                     FROM rent_schedules s
                     WHERE s.lease_id = {leaseId} AND s.active
                     LIMIT 1
                     """)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // The occurrences actually on record, which is what a reminder
            // will be attached to. Shown beside the schedule so a user can see
            // that saving produced something, and what.
            var occurrences = await dbContext.Database
                .SqlQuery<RentOccurrenceRow>(
                    $"""
                     SELECT
                       o.id            AS "Id",
                       o.sequence_number AS "SequenceNumber",
                       o.nominal_due_date AS "NominalDueDate",
                       o.due_date      AS "DueDate",
                       o.grace_period_end_date AS "GracePeriodEndDate",
                       o.late_fee_date AS "LateFeeDate",
                       o.amount        AS "Amount",
                       o.currency      AS "Currency",
                       o.adjustment_reason AS "AdjustmentReason"
                     FROM rent_occurrences o
                     WHERE o.lease_id = {leaseId}
                       AND o.due_date >= current_date
                     ORDER BY o.due_date
                     LIMIT 24
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { schedule, occurrences });
        })
        .WithName("GetRentSchedule");
    }

    // =========================================================================
    // Preview
    // =========================================================================

    private static void MapPreview(RouteGroupBuilder group)
    {
        group.MapPost("/rent-schedule/preview", (
            Guid organizationId,
            Guid leaseId,
            [FromBody] RentSchedulePreviewRequest request) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    title: "A schedule is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            RentScheduleExpansion expansion;

            try
            {
                var definition = Build(request);

                // Twelve occurrences is what a person can actually check. The
                // worker generates thirteen months; this is a preview, not a
                // second generation.
                var horizon = definition.StartDate.PlusMonths(14);

                expansion = RentScheduleCalculator.Expand(definition, horizon);
            }
            catch (ArgumentException exception)
            {
                // The calculator validates its own input and says what is
                // wrong. Surfacing that is better than a generic message,
                // because it names the field.
                return Results.Problem(
                    title: "That schedule cannot be used",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                occurrences = expansion.Occurrences
                    .Take(12)
                    .Select(occurrence => new
                    {
                        occurrence.SequenceNumber,
                        NominalDueDate = Iso(occurrence.NominalDueDate),
                        DueDate = Iso(occurrence.DueDate),
                        GracePeriodEndDate = occurrence.GracePeriodEndDate is { } grace
                            ? Iso(grace)
                            : null,
                        LateFeeDate = occurrence.LateFeeDate is { } late ? Iso(late) : null,
                        occurrence.Amount,
                        occurrence.Currency,
                        AdjustmentReason = occurrence.AdjustmentReason?.ToString(),
                        // True when the nominal date and the actual one differ,
                        // which is the thing worth drawing attention to: the
                        // lease says the 1st, the reminder goes on the 3rd.
                        WasShifted = occurrence.NominalDueDate != occurrence.DueDate,
                    }),

                // Months where the requested day does not exist. Not an error:
                // "the 31st" in February has no correct answer, only a chosen
                // one, and the user should see which months rely on the choice.
                unresolvedMonths = expansion.UnresolvedMonths.Select(month => new
                {
                    MonthStart = Iso(month.MonthStart),
                    month.RequestedDayOfMonth,
                    month.DaysInMonth,
                }),

                totalGenerated = expansion.Occurrences.Count,
            });
        })
        .WithName("PreviewRentSchedule");
    }

    // =========================================================================
    // Save
    // =========================================================================

    private static void MapSave(RouteGroupBuilder group)
    {
        group.MapPut("/rent-schedule", async (
            Guid organizationId,
            Guid leaseId,
            [FromBody] RentSchedulePreviewRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    title: "A schedule is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Validated by running it. If the calculator will not accept this
            // definition, the worker would fail on it later, in the dark.
            try
            {
                var definition = Build(request);

                RentScheduleCalculator.Expand(definition, definition.StartDate.PlusMonths(2));
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(
                    title: "That schedule cannot be used",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var scheduleId = await dbContext.Database
                    .SqlQuery<Guid>(
                        $"""
                         SELECT app.upsert_rent_schedule(
                           {leaseId}, {request.Frequency}, {request.DueDayRule},
                           {(short?)request.DueDayOfMonth}, {ParseDate(request.StartDate)},
                           {ParseDate(request.EndDate)}, {request.Amount}, {request.Currency},
                           {request.TimeZone}, {request.ShortMonthFallback},
                           {request.BusinessDayShift}, {request.HolidayCalendarId},
                           null, null) AS "Value"
                         """)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Deliberately not generated here. The worker picks this up
                // within seconds, and generating inline would mean a slow
                // request, a second code path, and no claim to stop two
                // requests doing it at once.
                return Results.Ok(new
                {
                    rentScheduleId = scheduleId,
                    generationPending = true,
                });
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("lease");
            }
        })
        .WithName("SaveRentSchedule");
    }

    // =========================================================================
    // Shared
    // =========================================================================

    /// <summary>
    /// Turns a request into the definition the calculator takes.
    /// </summary>
    /// <remarks>
    /// Used by both preview and save, so the thing previewed is exactly the
    /// thing saved. Two separate conversions would be two chances to disagree,
    /// and the symptom would be a preview that lies.
    /// </remarks>
    private static RentScheduleDefinition Build(RentSchedulePreviewRequest request) => new()
    {
        Frequency = Enum.Parse<RentFrequency>(ToPascal(request.Frequency), ignoreCase: true),
        DueDayRule = Enum.Parse<DueDayRule>(ToPascal(request.DueDayRule), ignoreCase: true),
        DueDayOfMonth = request.DueDayOfMonth,
        StartDate = ParseLocalDate(request.StartDate),
        EndDate = string.IsNullOrWhiteSpace(request.EndDate)
            ? null
            : ParseLocalDate(request.EndDate),
        ShortMonthFallback = Enum.Parse<ShortMonthFallback>(
            ToPascal(request.ShortMonthFallback ?? "last_day_of_month"), ignoreCase: true),
        BusinessDayShift = Enum.Parse<BusinessDayShift>(
            ToPascal(request.BusinessDayShift ?? "none"), ignoreCase: true),
        // The preview uses a plain weekend calendar. Holiday dates are a
        // database read, and a preview that blocked on one would make typing
        // in the editor feel slow; the saved schedule uses the real calendar
        // when the worker generates it.
        Calendar = string.Equals(request.BusinessDayShift, "none", StringComparison.Ordinal)
            ? null
            : WorkingDayCalendar.SaturdaySundayWeekend,
        Amount = request.Amount,
        Currency = request.Currency,
        GracePeriodDays = request.GracePeriodDays,
        LateFeeAfterDays = request.LateFeeAfterDays,
    };

    /// <summary>Turns "first_of_month" into "FirstOfMonth".</summary>
    private static string ToPascal(string snake) =>
        string.Concat(snake.Split('_').Select(part =>
            part.Length == 0
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..]));

    private static LocalDate ParseLocalDate(string iso)
    {
        var date = DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new LocalDate(date.Year, date.Month, date.Day);
    }

    private static DateOnly? ParseDate(string? iso) =>
        string.IsNullOrWhiteSpace(iso)
            ? null
            : DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(LocalDate date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>A rent schedule as the editor describes it.</summary>
/// <remarks>
/// Dates cross as ISO strings rather than as a date type. A rent due date is a
/// calendar date in the property's zone, and letting a serializer turn it into
/// an instant is how a tenant in Los Angeles gets told rent is due on the
/// 31st when the lease says the 1st.
/// </remarks>
public sealed record RentSchedulePreviewRequest(
    string Frequency,
    string DueDayRule,
    int? DueDayOfMonth,
    string StartDate,
    string? EndDate,
    decimal Amount,
    string Currency,
    string TimeZone,
    string? ShortMonthFallback,
    string? BusinessDayShift,
    Guid? HolidayCalendarId,
    int? GracePeriodDays,
    int? LateFeeAfterDays);

/// <summary>The saved schedule, as read back.</summary>
public sealed record RentScheduleRow(
    Guid Id,
    string Frequency,
    string DueDayRule,
    short? DueDayOfMonth,
    DateOnly StartDate,
    DateOnly? EndDate,
    decimal? Amount,
    string? Currency,
    string TimeZone,
    string ShortMonthFallback,
    string BusinessDayShift,
    Guid? HolidayCalendarId,
    int GenerationVersion);

/// <summary>One generated occurrence.</summary>
public sealed record RentOccurrenceRow(
    Guid Id,
    int SequenceNumber,
    DateOnly NominalDueDate,
    DateOnly DueDate,
    DateOnly? GracePeriodEndDate,
    DateOnly? LateFeeDate,
    decimal Amount,
    string Currency,
    string? AdjustmentReason);
