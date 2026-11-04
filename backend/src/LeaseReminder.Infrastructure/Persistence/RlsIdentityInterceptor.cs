using System.Data.Common;
using LeaseReminder.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// Stamps the verified caller identity onto every database connection, so the
/// Row Level Security policies in db/migrations/0009 can see who is asking.
///
/// <para>
/// The policies read <c>app.current_auth_user_id()</c>, which reads the
/// <c>app.auth_user_id</c> setting. Something has to set it, and that
/// something has to be impossible to bypass. An interceptor on connection open
/// is that place: every query EF Core issues, including ones added later by
/// someone who has never read this file, runs on a connection that has already
/// been stamped.
/// </para>
///
/// <para><b>Why the identity is set with a parameter, never by interpolation.</b>
/// The value arrives as an <see cref="NpgsqlParameter"/> of type uuid. Even if
/// a crafted subject claim somehow reached this code, it could not alter the
/// statement: it is data, not SQL. A string-concatenated
/// <c>SET app.auth_user_id = '...'</c> would be an injection point at the exact
/// layer that decides what rows the caller can see.
/// </para>
///
/// <para><b>Why connection-scoped rather than transaction-scoped.</b>
/// Setting it transaction-locally would be tighter, but it would also mean
/// every read outside an explicit transaction silently loses its identity and
/// returns nothing. Connection scope is safe here because Npgsql resets
/// session state when a connection returns to the pool, so one request's
/// identity cannot leak into the next. That reset is a load-bearing assumption,
/// so <see cref="NpgsqlDataSourceFactory"/> refuses to start if the connection
/// string disables it.
/// </para>
/// </summary>
public sealed partial class RlsIdentityInterceptor(
    IRequestIdentity identity,
    ILogger<RlsIdentityInterceptor> logger)
    : DbConnectionInterceptor
{
    /// <summary>
    /// set_config with is_local = false: the setting lives for the session,
    /// and Npgsql clears it on pool return.
    ///
    /// <para>
    /// The <c>::text</c> cast is required, not cosmetic. set_config's value
    /// parameter is text, so a uuid-typed parameter resolves to
    /// <c>set_config(unknown, uuid, boolean)</c>, which does not exist. The
    /// parameter is still declared as uuid rather than as a string, because
    /// that is what makes a malformed subject claim a type error at the
    /// protocol level instead of something the server has to parse.
    /// </para>
    ///
    /// <para>
    /// When the parameter is NULL, the cast yields NULL and set_config leaves
    /// the setting empty, so <c>app.current_auth_user_id()</c> returns NULL
    /// and every policy matches no rows. Failing closed is the correct
    /// outcome for an unauthenticated connection.
    /// </para>
    /// </summary>
    private const string SetIdentitySql =
        "SELECT set_config('app.auth_user_id', @auth_user_id::text, false)";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyIdentityAsync(connection, cancellationToken).ConfigureAwait(false);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        // The synchronous path exists because EF Core can still take it (for
        // example during a synchronous SaveChanges). Leaving it unimplemented
        // would mean a query on that path ran with no identity and silently
        // returned nothing, which is a confusing failure to diagnose.
        ApplyIdentityAsync(connection, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        base.ConnectionOpened(connection, eventData);
    }

    private async Task ApplyIdentityAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SetIdentitySql;

        var parameter = new NpgsqlParameter("auth_user_id", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            // DBNull, not an empty string: app.current_auth_user_id() returns
            // NULL for it, and every policy then matches no rows. An
            // unauthenticated connection reaching the database is not an error
            // here, it is simply a connection that can see nothing.
            Value = identity.AuthUserId.HasValue ? identity.AuthUserId.Value : DBNull.Value,
        };

        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The subject itself is never logged: it is a user identifier, and
        // section 4.K asks that logs avoid unnecessary personal data. Whether
        // a request was authenticated is enough to diagnose "why did this
        // query return nothing".
        LogIdentityApplied(logger, identity.CorrelationId, identity.AuthUserId.HasValue);
    }

    /// <summary>
    /// Source-generated so the message template is parsed at compile time and
    /// nothing is allocated when debug logging is off. This runs on every
    /// connection open, so the hot path matters.
    /// </summary>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Database identity applied for correlation {CorrelationId}; authenticated: {IsAuthenticated}")]
    private static partial void LogIdentityApplied(
        ILogger logger,
        Guid correlationId,
        bool isAuthenticated);
}
