using LeaseReminder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Liveness and readiness, for Render's health check and for alerting
/// (sections 4.K and 8).
///
/// <para>
/// These are the only unauthenticated endpoints in the API, and they are
/// written so that being unauthenticated costs nothing: they return a fixed
/// shape with no version numbers, no schema details, and no error text.
/// A health endpoint that reports "connection refused to
/// db.PROJECT.supabase.co:5432" is a free reconnaissance tool.
/// </para>
///
/// <para>
/// Liveness and readiness are separate because they answer different
/// questions. Liveness asks "is this process wedged", and must not depend on
/// the database, or a brief database blip would cause the platform to restart
/// a perfectly healthy container and make the outage worse. Readiness asks
/// "can this instance serve traffic", and must check the database, because an
/// instance that cannot reach PostgreSQL can serve nothing.
/// </para>
/// </summary>
public static partial class HealthEndpoints
{
    /// <summary>
    /// Source-generated, so the readiness check allocates nothing on the happy
    /// path. The exception is logged in full for operators; the caller gets a
    /// bare "not_ready".
    /// </summary>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Readiness check failed: the database could not be reached")]
    private static partial void LogReadinessFailed(ILogger logger, Exception exception);

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/health").AllowAnonymous();

        group.MapGet("/live", () => Results.Ok(new { status = "live" }))
            .WithName("HealthLive")
            .WithSummary("Process liveness. Deliberately does not touch the database.");

        group.MapGet("/ready", async (
            AppDbContext dbContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                // A trivial round trip. Enough to prove the pool can open a
                // connection and the server answers; cheap enough to poll.
                await dbContext.Database
                    .ExecuteSqlRawAsync("SELECT 1", cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new { status = "ready" });
            }
            catch (Exception exception)
            {
                // Logged in full for operators, summarised to nothing for the
                // caller.
                LogReadinessFailed(loggerFactory.CreateLogger("Health"), exception);

                return Results.Json(
                    new { status = "not_ready" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .WithName("HealthReady")
            .WithSummary("Readiness. Verifies the database is reachable.");

        return routes;
    }
}
