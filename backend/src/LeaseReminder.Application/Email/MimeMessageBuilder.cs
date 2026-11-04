using System.Text;

namespace LeaseReminder.Application.Email;

/// <summary>
/// Builds the RFC 5322 message the Gmail API expects.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than with a MIME library. The shape needed is fixed
/// and small — one multipart/alternative carrying a plain part and an HTML
/// part — and a library would be a dependency and an attack surface for the
/// sake of forty lines.
/// </para>
/// <para>
/// <b>Header injection is the reason this is a separate, tested class.</b>
/// Every header value here is built from data that originates in a lease or a
/// tenant record: a subject rendered from a template, a display name, a
/// recipient address. A value containing a carriage return would terminate its
/// header and let the next line be read as a new one — which is how a subject
/// line becomes a Bcc header, and a rent reminder becomes a mail relay. Every
/// value is stripped of CR and LF before it reaches a header, and the tests
/// try to get past it.
/// </para>
/// </remarks>
public static class MimeMessageBuilder
{
    /// <summary>
    /// Builds a message, base64url encoded for the Gmail API's `raw` field.
    /// </summary>
    /// <param name="fromAddress">The connected mailbox's own address.</param>
    /// <param name="fromDisplayName">Optional display name.</param>
    /// <param name="to">Recipients. At least one.</param>
    /// <param name="cc">Copied recipients, if any.</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="textBody">The plain-text alternative.</param>
    /// <param name="htmlBody">The HTML alternative.</param>
    /// <param name="messageId">
    /// A stable identifier for this message, used as the local part of the
    /// Message-ID header so a resend can be correlated with the original.
    /// </param>
    /// <param name="boundary">
    /// The multipart boundary. Passed in so a test can produce a stable
    /// output; callers in production generate a random one.
    /// </param>
    public static string Build(
        string fromAddress,
        string? fromDisplayName,
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        string subject,
        string textBody,
        string htmlBody,
        string messageId,
        string boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAddress);
        ArgumentNullException.ThrowIfNull(to);

        if (to.Count == 0)
        {
            throw new ArgumentException("A message needs at least one recipient.", nameof(to));
        }

        var builder = new StringBuilder();

        var from = string.IsNullOrWhiteSpace(fromDisplayName)
            ? SanitiseHeader(fromAddress)
            : $"{EncodeHeaderWord(fromDisplayName)} <{SanitiseHeader(fromAddress)}>";

        Header(builder, "From", from);
        Header(builder, "To", JoinAddresses(to));

        if (cc is { Count: > 0 })
        {
            Header(builder, "Cc", JoinAddresses(cc));
        }

        // Encoded rather than merely sanitised: a subject carries a property
        // name or a tenant's name, which may be outside ASCII.
        Header(builder, "Subject", EncodeHeaderWord(subject));

        Header(builder, "MIME-Version", "1.0");
        Header(
            builder,
            "Message-ID",
            $"<{SanitiseHeader(messageId)}@{SanitiseHeader(DomainOf(fromAddress))}>");

        Header(
            builder,
            "Content-Type",
            $"multipart/alternative; boundary=\"{SanitiseHeader(boundary)}\"");

        builder.Append("\r\n");

        // Plain text first. multipart/alternative is ordered least to most
        // preferred, so putting HTML first makes a text-only client show the
        // wrong part.
        Part(builder, boundary, "text/plain", textBody);
        Part(builder, boundary, "text/html", htmlBody);

        builder.Append("--").Append(SanitiseHeader(boundary)).Append("--\r\n");

        return ToBase64Url(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void Header(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").Append(value).Append("\r\n");

    private static void Part(
        StringBuilder builder,
        string boundary,
        string mediaType,
        string content)
    {
        builder.Append("--").Append(SanitiseHeader(boundary)).Append("\r\n");
        builder.Append("Content-Type: ").Append(mediaType).Append("; charset=\"UTF-8\"\r\n");

        // base64 rather than quoted-printable: it has no line-length traps
        // and no characters that need escaping, so a body cannot break the
        // structure however it is composed.
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        builder.Append(ToBase64Lines(content)).Append("\r\n");
    }

    /// <summary>
    /// Joins addresses, discarding any that could break the header.
    /// </summary>
    /// <remarks>
    /// Dropped rather than sanitised. A sanitised address is a different
    /// address, and quietly sending to a different address than was asked for
    /// is worse than not sending at all. Recipient addresses are validated
    /// before they reach the queue, so anything arriving here malformed is a
    /// defect or an attack.
    /// </remarks>
    private static string JoinAddresses(IReadOnlyList<string> addresses) =>
        string.Join(
            ", ",
            addresses
                .Where(address =>
                    !string.IsNullOrWhiteSpace(address)
                    && address.AsSpan().IndexOfAny('\r', '\n') < 0)
                .Select(address => address.Trim()));

    /// <summary>
    /// Removes anything that could terminate a header line.
    /// </summary>
    /// <remarks>
    /// The header-injection defence. Also removes NUL, which some transports
    /// treat as a terminator even though RFC 5322 does not permit it at all.
    /// </remarks>
    public static string SanitiseHeader(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is not ('\r' or '\n' or '\0'))
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Encodes a header value that may contain non-ASCII characters.
    /// </summary>
    /// <remarks>
    /// RFC 2047 encoded-word. A tenant called "Müller" or a property named
    /// "Café Court" would otherwise arrive as mojibake, or be mangled by a
    /// transport that assumes ASCII. Encoding also neutralises anything
    /// structural in the value, because the result is base64.
    /// </remarks>
    public static string EncodeHeaderWord(string? value)
    {
        var safe = SanitiseHeader(value);

        if (safe.Length == 0)
        {
            return string.Empty;
        }

        if (safe.All(char.IsAscii))
        {
            return safe;
        }

        return $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(safe))}?=";
    }

    /// <summary>Base64 wrapped at 76 characters, as MIME requires.</summary>
    private static string ToBase64Lines(string content)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? string.Empty));
        var builder = new StringBuilder(encoded.Length + (encoded.Length / 76 * 2) + 2);

        for (var offset = 0; offset < encoded.Length; offset += 76)
        {
            builder
                .Append(encoded.AsSpan(offset, Math.Min(76, encoded.Length - offset)))
                .Append("\r\n");
        }

        return builder.ToString();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');

        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "localhost";
    }
}
