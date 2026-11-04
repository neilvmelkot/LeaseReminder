using System.Security.Cryptography;
using System.Text;

namespace LeaseReminder.Infrastructure.Security;

/// <summary>
/// Encrypts and decrypts the Gmail refresh token with AES-256-GCM
/// (section 4.E).
///
/// <para><b>Why authenticated encryption.</b> GCM provides confidentiality and
/// integrity together. Without the authentication tag, someone able to write to
/// the database could flip bits in the ciphertext and the application would
/// decrypt it to a different value without noticing. Here, tampering fails
/// loudly as a <see cref="CryptographicException"/>.
/// </para>
///
/// <para><b>Why the account id is additional authenticated data.</b> The
/// account id is bound into the tag but not encrypted. That makes it
/// impossible to take the ciphertext belonging to one connected mailbox and
/// re-use it on another row: decryption of a moved ciphertext fails. Without
/// AAD, swapping two rows' ciphertexts would silently send one
/// organization's mail through another's mailbox.
/// </para>
///
/// <para><b>Why keys are versioned.</b> Rotation needs both keys to exist at
/// once (section 4.E asks for a planned re-encryption process). Every
/// ciphertext records the version that produced it, so rotation can re-encrypt
/// row by row while the old key still decrypts what has not been migrated.
/// </para>
///
/// <para>
/// The key never reaches PostgreSQL: it lives in the process environment, and
/// the database stores only opaque bytes. That is what keeps a database backup
/// from being a set of working Gmail credentials.
/// </para>
/// </summary>
public sealed class AesGcmTokenProtector : ITokenProtector
{
    /// <summary>AES-256. Any other length is a configuration error, not a variant.</summary>
    private const int KeySizeBytes = 32;

    /// <summary>96 bits, the size GCM is specified and optimised for.</summary>
    private const int NonceSizeBytes = 12;

    /// <summary>128 bits, the full GCM tag. Truncated tags weaken the integrity guarantee.</summary>
    private const int TagSizeBytes = 16;

    private readonly IReadOnlyDictionary<int, byte[]> _keysByVersion;

    public AesGcmTokenProtector(IReadOnlyDictionary<int, byte[]> keysByVersion, int currentKeyVersion)
    {
        ArgumentNullException.ThrowIfNull(keysByVersion);

        if (keysByVersion.Count == 0)
        {
            throw new ArgumentException("At least one encryption key is required.", nameof(keysByVersion));
        }

        foreach (var (version, key) in keysByVersion)
        {
            if (version < 1)
            {
                throw new ArgumentException(
                    $"Key version {version} is invalid; versions start at 1.",
                    nameof(keysByVersion));
            }

            if (key.Length != KeySizeBytes)
            {
                // The length is reported, never the key. A short key is a
                // configuration mistake worth naming precisely, because the
                // alternative is AES-128 security under an AES-256 name.
                throw new ArgumentException(
                    $"Key version {version} is {key.Length} bytes; AES-256-GCM requires " +
                    $"exactly {KeySizeBytes}. Generate one with: openssl rand -base64 32",
                    nameof(keysByVersion));
            }
        }

        if (!keysByVersion.ContainsKey(currentKeyVersion))
        {
            throw new ArgumentException(
                $"Current key version {currentKeyVersion} is not among the supplied keys.",
                nameof(currentKeyVersion));
        }

        _keysByVersion = keysByVersion;
        CurrentKeyVersion = currentKeyVersion;
    }

    /// <summary>The version new ciphertexts are produced under.</summary>
    public int CurrentKeyVersion { get; }

    /// <summary>
    /// Builds a protector from configuration values of the form
    /// <c>version:base64key</c>, or a bare base64 key meaning version 1.
    /// </summary>
    public static AesGcmTokenProtector FromConfiguration(
        string encodedKeys,
        int currentKeyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedKeys);

        var keys = new Dictionary<int, byte[]>();

        foreach (var entry in encodedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                     | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(':', StringComparison.Ordinal);

            var version = 1;
            var encodedKey = entry;

            if (separator > 0)
            {
                if (!int.TryParse(
                        entry[..separator],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out version))
                {
                    throw new ArgumentException(
                        "A key entry must look like 'version:base64key'.", nameof(encodedKeys));
                }

                encodedKey = entry[(separator + 1)..];
            }

            byte[] key;

            try
            {
                key = Convert.FromBase64String(encodedKey);
            }
            catch (FormatException)
            {
                // The exception message deliberately omits the value, so a
                // malformed key cannot end up in a log.
                throw new ArgumentException(
                    $"Key version {version} is not valid base64.", nameof(encodedKeys));
            }

            if (!keys.TryAdd(version, key))
            {
                throw new ArgumentException(
                    $"Key version {version} is declared more than once.", nameof(encodedKeys));
            }
        }

        return new AesGcmTokenProtector(keys, currentKeyVersion);
    }

    public ProtectedToken Protect(string plaintextToken, Guid emailAccountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintextToken);

        var key = _keysByVersion[CurrentKeyVersion];
        var plaintext = Encoding.UTF8.GetBytes(plaintextToken);

        var nonce = new byte[NonceSizeBytes];

        // A repeated nonce under the same key is catastrophic for GCM, so it
        // comes from the cryptographic RNG, never from a counter or a clock.
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);

        aes.Encrypt(
            nonce,
            plaintext,
            ciphertext,
            tag,
            associatedData: BuildAssociatedData(emailAccountId, CurrentKeyVersion));

        // The plaintext copy is cleared; the string itself is immutable and
        // beyond reach, which is a known limitation of string-based secrets.
        CryptographicOperations.ZeroMemory(plaintext);

        return new ProtectedToken
        {
            Ciphertext = ciphertext,
            Nonce = nonce,
            AuthenticationTag = tag,
            KeyVersion = CurrentKeyVersion,
        };
    }

    public string Unprotect(ProtectedToken token, Guid emailAccountId)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!_keysByVersion.TryGetValue(token.KeyVersion, out var key))
        {
            throw new CryptographicException(
                $"No key is configured for version {token.KeyVersion}. " +
                "A retired key must stay available until every row has been re-encrypted.");
        }

        if (token.Nonce.Length != NonceSizeBytes)
        {
            throw new CryptographicException(
                $"Stored nonce is {token.Nonce.Length} bytes; expected {NonceSizeBytes}.");
        }

        if (token.AuthenticationTag.Length != TagSizeBytes)
        {
            throw new CryptographicException(
                $"Stored tag is {token.AuthenticationTag.Length} bytes; expected {TagSizeBytes}.");
        }

        var plaintext = new byte[token.Ciphertext.Length];

        using var aes = new AesGcm(key, TagSizeBytes);

        try
        {
            aes.Decrypt(
                token.Nonce,
                token.Ciphertext,
                token.AuthenticationTag,
                plaintext,
                associatedData: BuildAssociatedData(emailAccountId, token.KeyVersion));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);

            // Rethrown with context but WITHOUT any ciphertext or key
            // material, because this message will be logged.
            throw new CryptographicException(
                "The stored Gmail credential failed authentication. It was either modified, " +
                "encrypted under a different key, or copied from another account. " +
                "Reconnect the account rather than retrying.");
        }

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The data bound into the authentication tag but left unencrypted: the
    /// owning account and the key version. Binding the account prevents a
    /// ciphertext being moved between rows; binding the version prevents a
    /// downgrade to an older key by rewriting the version column alone.
    /// </summary>
    private static byte[] BuildAssociatedData(Guid emailAccountId, int keyVersion) =>
        Encoding.UTF8.GetBytes(
            $"email_account={emailAccountId:N};key_version={keyVersion};purpose=gmail_refresh_token");
}
