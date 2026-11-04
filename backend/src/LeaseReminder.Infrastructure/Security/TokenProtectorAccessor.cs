namespace LeaseReminder.Infrastructure.Security;

/// <summary>
/// Holds the token protector, which may legitimately not be configured yet.
///
/// <para>
/// Phase 1 has no Gmail connection flow, so requiring an encryption key to
/// start the API would block every other feature on a secret that nothing yet
/// uses. Equally, registering a nullable service and letting handlers inject
/// <c>ITokenProtector?</c> does not work: dependency injection would fail to
/// resolve it rather than hand over null.
/// </para>
///
/// <para>
/// So the optionality is made explicit. A caller asks
/// <see cref="IsConfigured"/> if it wants to degrade gracefully, or calls
/// <see cref="Require"/> and gets an error that names the missing setting
/// instead of a null reference somewhere inside the OAuth callback.
/// </para>
/// </summary>
public sealed class TokenProtectorAccessor(ITokenProtector? protector)
{
    /// <summary>True when an encryption key is configured.</summary>
    public bool IsConfigured => protector is not null;

    /// <summary>
    /// The protector, or an error explaining exactly what to configure.
    /// </summary>
    public ITokenProtector Require() =>
        protector ?? throw new InvalidOperationException(
            "No token encryption key is configured, so an OAuth refresh token cannot be " +
            "stored. Set TokenEncryption:Keys (environment variable " +
            "TOKEN_ENCRYPTION_KEY) to a base64-encoded 32-byte key. Generate one with: " +
            "openssl rand -base64 32. The key must not be stored in the database.");
}
