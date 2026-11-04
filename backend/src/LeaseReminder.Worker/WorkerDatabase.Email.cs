using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

namespace LeaseReminder.Worker;

/// <summary>The one mailbox an organization sends from.</summary>
public sealed record ConnectedEmailAccount
{
    public required Guid EmailAccountId { get; init; }

    public required string EmailAddress { get; init; }

    public string? DisplayName { get; init; }

    public required int DailySendLimit { get; init; }
}

/// <summary>An encrypted refresh token, as stored.</summary>
/// <remarks>
/// Deliberately not a string. The plaintext exists only inside the sender, for
/// the length of one refresh, and is never held on a type that could end up in
/// a log line or a serialised object.
/// </remarks>
public sealed record StoredEmailToken
{
    public required byte[] Ciphertext { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] AuthenticationTag { get; init; }

    public required int KeyVersion { get; init; }

    public required string EmailAddress { get; init; }
}

/// <summary>
/// The Gmail half of the worker's database surface.
/// </summary>
/// <remarks>
/// <c>app.read_email_account_token</c> is granted to the worker role alone,
/// and column-level grants in migration 0010 put the token columns out of the
/// API role's reach entirely. So this file is the only place in the
/// application where an encrypted Gmail credential is read, and the worker is
/// the only process that can do it.
/// </remarks>
public sealed partial class WorkerDatabase
{
    /// <summary>
    /// Finds the organization's connected mailbox.
    /// </summary>
    /// <returns>
    /// Null when nothing is connected. That is the disconnect path: with no
    /// account there is no address to send from, and there is deliberately no
    /// fallback.
    /// </returns>
    public async Task<ConnectedEmailAccount?> ReadConnectedEmailAccountAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT email_account_id, email_address, display_name, daily_send_limit
            FROM app.connected_email_account($1)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ConnectedEmailAccount
        {
            EmailAccountId = reader.GetGuid(0),
            EmailAddress = reader.GetString(1),
            DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
            DailySendLimit = reader.GetInt32(3),
        };
    }

    /// <summary>
    /// Reads the encrypted refresh token for an account.
    /// </summary>
    /// <returns>
    /// Null when the account is not connected. The function filters on
    /// connection status itself, which is what makes "disconnect stops
    /// sending" a property of the database rather than of this code
    /// remembering to check.
    /// </returns>
    public async Task<StoredEmailToken?> ReadEmailAccountTokenAsync(
        Guid emailAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            """
            SELECT ciphertext, nonce, auth_tag, key_version, email_address::text
            FROM app.read_email_account_token($1)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = emailAccountId });

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            return null;
        }

        return new StoredEmailToken
        {
            Ciphertext = reader.GetFieldValue<byte[]>(0),
            Nonce = reader.GetFieldValue<byte[]>(1),
            AuthenticationTag = reader.GetFieldValue<byte[]>(2),
            KeyVersion = reader.GetInt32(3),
            EmailAddress = reader.GetString(4),
        };
    }

    /// <summary>
    /// Appends to the send record.
    /// </summary>
    /// <remarks>
    /// Written whatever happened. A log that records only successes answers
    /// none of the questions it would actually be consulted for.
    /// </remarks>
    public async Task<Guid> RecordEmailLogAsync(
        Guid organizationId,
        Guid emailAccountId,
        string senderEmail,
        IReadOnlyList<string> recipients,
        string subject,
        string status,
        string errorCategory,
        string? errorMessage,
        string? providerMessageId,
        CancellationToken cancellationToken)
    {
        var recipientsJson = new JsonArray();

        foreach (var recipient in recipients)
        {
            recipientsJson.Add(recipient);
        }

        await using var command = DataSource.CreateCommand(
            """
            SELECT app.record_email_log($1, $2, $3, $4, $5, $6, $7, $8, $9, 1, null, null)
            """);

        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
        command.Parameters.Add(new NpgsqlParameter { Value = emailAccountId });
        command.Parameters.Add(new NpgsqlParameter { Value = senderEmail });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = recipientsJson.ToJsonString(),
        });
        command.Parameters.Add(new NpgsqlParameter { Value = subject });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = errorCategory });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)errorMessage ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)providerMessageId ?? DBNull.Value,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is Guid id ? id : Guid.Empty;
    }

    /// <summary>
    /// How many messages this mailbox has had accepted today.
    /// </summary>
    /// <remarks>
    /// Checked before sending so the application stops at its own limit.
    /// Running into Google's limit instead turns a decision into an incident.
    /// </remarks>
    public async Task<int> CountSendsTodayAsync(
        Guid emailAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT app.email_account_sends_today($1)");

        command.Parameters.Add(new NpgsqlParameter { Value = emailAccountId });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is int count ? count : 0;
    }
}
