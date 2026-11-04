namespace LeaseReminder.Infrastructure.Security;

/// <summary>
/// Protects an OAuth refresh token at rest.
///
/// <para>
/// An interface rather than a concrete class so that a managed KMS can replace
/// the in-process implementation later (section 4.E allows either) without
/// touching the OAuth flow or the dispatcher. The contract is deliberately
/// narrow: no key material crosses it, and nothing here returns the key.
/// </para>
/// </summary>
public interface ITokenProtector
{
    /// <summary>The key version new ciphertexts are produced under.</summary>
    int CurrentKeyVersion { get; }

    /// <summary>
    /// Encrypts a refresh token, binding it to the account it belongs to so
    /// the result cannot be re-used on another row.
    /// </summary>
    ProtectedToken Protect(string plaintextToken, Guid emailAccountId);

    /// <summary>
    /// Decrypts a refresh token. Throws if the ciphertext was modified,
    /// encrypted under an unavailable key, or bound to a different account.
    /// </summary>
    string Unprotect(ProtectedToken token, Guid emailAccountId);
}

/// <summary>
/// An encrypted token, in the three parts the email_accounts table stores
/// separately. Keeping them as distinct columns rather than one blob means
/// nothing has to re-parse a packed format, and the CHECK constraints in
/// db/migrations/0005 can assert the nonce and tag lengths.
/// </summary>
public sealed record ProtectedToken
{
    public required byte[] Ciphertext { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] AuthenticationTag { get; init; }

    /// <summary>
    /// Which key version produced this. Required for rotation: a retired key
    /// must remain available until every row has been re-encrypted.
    /// </summary>
    public required int KeyVersion { get; init; }
}
