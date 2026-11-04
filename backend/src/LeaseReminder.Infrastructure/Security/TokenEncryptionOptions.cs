using System.ComponentModel.DataAnnotations;

namespace LeaseReminder.Infrastructure.Security;

/// <summary>
/// The keys that protect stored Gmail refresh tokens.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Infrastructure rather than in either host project because both
/// need it, and for opposite halves of the same job: the API encrypts a token
/// when a mailbox is connected, and the worker decrypts one when it sends. A
/// copy in each would be two places for the key format to drift.
/// </para>
/// <para>
/// The keys are deliberately not in the database. A database backup should
/// not be a working set of Gmail credentials (section 4.E).
/// </para>
/// </remarks>
public sealed class TokenEncryptionOptions
{
    public const string SectionName = "TokenEncryption";

    /// <summary>
    /// One or more AES-256 keys as <c>version:base64</c>, comma separated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several may be present at once, which is what makes rotation possible
    /// without downtime: the new key encrypts, every listed key can decrypt,
    /// and the old one is removed once nothing is left encrypted under it.
    /// </para>
    /// <para>
    /// Null means the Gmail connection flow is unavailable. That is a
    /// supported state rather than a startup failure: everything else in the
    /// application works, and the connection screen says what is missing.
    /// </para>
    /// </remarks>
    public string? Keys { get; init; }

    /// <summary>Which of those keys new tokens are encrypted under.</summary>
    [Range(1, 1000)]
    public int CurrentKeyVersion { get; init; } = 1;

    /// <summary>Whether a token can be encrypted or decrypted at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(this.Keys);
}
