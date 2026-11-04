using System.Text;

using LeaseReminder.Application.Email;

using Xunit;

namespace LeaseReminder.UnitTests.Email;

/// <summary>
/// Tests for the outgoing message builder.
/// </summary>
/// <remarks>
/// <para>
/// Header injection is the subject of most of this file. Every header value in
/// a rent reminder is built from data that originates outside this system: a
/// subject rendered from a lease, a tenant's name, an address somebody typed.
/// A carriage return in any of them would terminate its header and let the
/// following text be read as a new one, which is how a subject line becomes a
/// Bcc header and a reminder service becomes a mail relay.
/// </para>
/// <para>
/// So the tests below try to do that, in each of the places it could be tried.
/// </para>
/// </remarks>
public sealed class MimeMessageBuilderTests
{
    private const string Boundary = "----=_Part_test";

    // ---------------------------------------------------------------------
    // Header injection
    // ---------------------------------------------------------------------

    /// <summary>
    /// The central case. A subject that tries to close its own header and
    /// open a Bcc must not produce one.
    /// </summary>
    [Fact]
    public void A_subject_cannot_introduce_a_new_header()
    {
        var decoded = BuildAndDecode(
            subject: "Rent due\r\nBcc: everyone@example.test");
        Assert.False(HasHeader(decoded, "Bcc"));
        // The characters are still present, folded into the subject, which
        // is correct: the text was in the subject and deleting it would be a
        // different bug.
        Assert.Contains(
            "Subject: Rent dueBcc: everyone@example.test",
            decoded,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\0")]
    public void No_line_terminator_survives_into_a_subject(string terminator)
    {
        var decoded = BuildAndDecode(subject: $"Rent{terminator}Bcc: x@example.test");

        // The whole subject stays on one line, and no new header appears.
        // A space is deliberately not among the cases above: it cannot
        // terminate a header, and treating it as hostile would mangle every
        // ordinary multi-word subject.
        Assert.False(HasHeader(decoded, "Bcc"));
        Assert.Contains(
            "RentBcc: x@example.test",
            LineStartingWith(decoded, "Subject:"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A display name is attacker-influenced too: it can come from an
    /// organization's own settings, which a compromised member could edit.
    /// </summary>
    [Fact]
    public void A_display_name_cannot_introduce_a_new_header()
    {
        var decoded = BuildAndDecode(
            fromDisplayName: "Landlord\r\nBcc: everyone@example.test");
        Assert.False(HasHeader(decoded, "Bcc"));
    }

    /// <summary>
    /// A malformed recipient is dropped rather than cleaned. A sanitised
    /// address is a different address, and quietly sending somewhere other
    /// than was asked is worse than not sending.
    /// </summary>
    [Fact]
    public void A_recipient_containing_a_line_break_is_dropped_not_repaired()
    {
        var decoded = BuildAndDecode(
            to: ["good@example.test", "bad@example.test\r\nBcc: everyone@example.test"]);

        var toLine = LineStartingWith(decoded, "To:");

        Assert.Contains("good@example.test", toLine, StringComparison.Ordinal);
        Assert.DoesNotContain("bad@example.test", toLine, StringComparison.Ordinal);
        Assert.False(HasHeader(decoded, "Bcc"));
    }

    [Fact]
    public void A_message_id_cannot_introduce_a_new_header()
    {
        var decoded = BuildAndDecode(messageId: "abc\r\nBcc: everyone@example.test");
        Assert.False(HasHeader(decoded, "Bcc"));
    }

    /// <summary>
    /// A body cannot break the structure, because it is base64 encoded. This
    /// is the reason for choosing base64 over quoted-printable: no character
    /// in the input can appear in the output.
    /// </summary>
    [Fact]
    public void A_body_cannot_close_the_multipart_boundary()
    {
        var decoded = BuildAndDecode(
            textBody: $"\r\n--{Boundary}--\r\nContent-Type: text/html\r\n\r\ninjected");

        // Exactly three: one opening each of the two parts, and the closing
        // one. A body that escaped its encoding would add more.
        var boundaryCount = CountOccurrences(decoded, $"--{Boundary}");

        Assert.Equal(3, boundaryCount);
    }

    // ---------------------------------------------------------------------
    // Correctness of the ordinary message
    // ---------------------------------------------------------------------

    [Fact]
    public void Produces_the_expected_headers()
    {
        var decoded = BuildAndDecode();

        Assert.Contains("From: Dana Owner <owner@example.test>", decoded, StringComparison.Ordinal);
        Assert.Contains("To: tenant@example.test", decoded, StringComparison.Ordinal);
        Assert.Contains("MIME-Version: 1.0", decoded, StringComparison.Ordinal);
        Assert.Contains("multipart/alternative", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Omits_the_display_name_when_there_is_none()
    {
        var decoded = BuildAndDecode(fromDisplayName: null);

        Assert.Contains("From: owner@example.test", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("<owner@example.test>", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Includes_a_cc_header_only_when_there_is_one()
    {
        Assert.DoesNotContain("Cc:", BuildAndDecode(), StringComparison.Ordinal);

        Assert.Contains(
            "Cc: manager@example.test",
            BuildAndDecode(cc: ["manager@example.test"]),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// multipart/alternative is ordered least to most preferred. Putting the
    /// HTML part first makes a text-only client display the wrong one.
    /// </summary>
    [Fact]
    public void Places_the_plain_text_part_before_the_html_part()
    {
        var decoded = BuildAndDecode();

        var plainAt = decoded.IndexOf("text/plain", StringComparison.Ordinal);
        var htmlAt = decoded.IndexOf("text/html", StringComparison.Ordinal);

        Assert.True(plainAt > 0 && htmlAt > plainAt);
    }

    [Fact]
    public void Both_bodies_survive_the_round_trip()
    {
        var decoded = BuildAndDecode(
            textBody: "Rent of 2400.00 is due on 1 June.",
            htmlBody: "<p>Rent of <strong>2400.00</strong> is due on 1 June.</p>");

        Assert.Contains("Rent of 2400.00 is due on 1 June.", DecodeParts(decoded), StringComparison.Ordinal);
        Assert.Contains("<strong>2400.00</strong>", DecodeParts(decoded), StringComparison.Ordinal);
    }

    /// <summary>
    /// A tenant called "Müller" must not arrive as mojibake, and a transport
    /// that assumes ASCII must not mangle the header.
    /// </summary>
    [Fact]
    public void Encodes_a_subject_containing_non_ascii_characters()
    {
        var decoded = BuildAndDecode(subject: "Miete für Café Court fällig");

        var subjectLine = LineStartingWith(decoded, "Subject:");

        Assert.StartsWith("Subject: =?UTF-8?B?", subjectLine, StringComparison.Ordinal);

        var encoded = subjectLine["Subject: =?UTF-8?B?".Length..].TrimEnd('?', '=');

        // Trailing '=' is padding the trim above may have taken; restore it.
        var padded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');

        Assert.Equal(
            "Miete für Café Court fällig",
            Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    [Fact]
    public void Leaves_an_ascii_subject_readable()
    {
        var decoded = BuildAndDecode(subject: "Rent is due on 1 June");

        Assert.Contains("Subject: Rent is due on 1 June", decoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Gmail API takes the message in the `raw` field, which must be
    /// base64url: no padding, and '-' and '_' rather than '+' and '/'.
    /// Standard base64 is rejected.
    /// </summary>
    [Fact]
    public void Produces_base64url_without_padding()
    {
        var raw = Build();

        Assert.DoesNotContain('=', raw);
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
    }

    /// <summary>
    /// Base64 lines are wrapped at 76 characters. An unwrapped body is
    /// technically malformed and some transports truncate it.
    /// </summary>
    [Fact]
    public void Wraps_encoded_bodies_at_the_line_limit()
    {
        var decoded = BuildAndDecode(textBody: new string('a', 5000));

        var longest = decoded
            .Split("\r\n", StringSplitOptions.None)
            .Max(line => line.Length);

        Assert.True(longest <= 998, $"The longest line was {longest} characters.");
    }

    // ---------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------

    [Fact]
    public void Refuses_a_message_with_no_recipients()
    {
        Assert.Throws<ArgumentException>(() => Build(to: []));
    }

    [Fact]
    public void Refuses_a_message_with_no_sender()
    {
        Assert.Throws<ArgumentException>(() => Build(fromAddress: "  "));
    }

    // ---------------------------------------------------------------------
    // The sanitiser itself
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  padded  ", "padded")]
    [InlineData("a\r\nb", "ab")]
    [InlineData("a\0b", "ab")]
    public void Sanitises_header_values(string? input, string expected)
    {
        Assert.Equal(expected, MimeMessageBuilder.SanitiseHeader(input));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string Build(
        string fromAddress = "owner@example.test",
        string? fromDisplayName = "Dana Owner",
        IReadOnlyList<string>? to = null,
        IReadOnlyList<string>? cc = null,
        string subject = "Rent is due",
        string textBody = "Rent is due on 1 June.",
        string htmlBody = "<p>Rent is due on 1 June.</p>",
        string messageId = "abc123") =>
        MimeMessageBuilder.Build(
            fromAddress,
            fromDisplayName,
            to ?? ["tenant@example.test"],
            cc ?? [],
            subject,
            textBody,
            htmlBody,
            messageId,
            Boundary);

    private static string BuildAndDecode(
        string fromAddress = "owner@example.test",
        string? fromDisplayName = "Dana Owner",
        IReadOnlyList<string>? to = null,
        IReadOnlyList<string>? cc = null,
        string subject = "Rent is due",
        string textBody = "Rent is due on 1 June.",
        string htmlBody = "<p>Rent is due on 1 June.</p>",
        string messageId = "abc123")
    {
        var raw = Build(fromAddress, fromDisplayName, to, cc, subject, textBody, htmlBody, messageId);

        var padded = raw.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    /// <summary>Decodes every base64 part, so body content can be asserted on.</summary>
    private static string DecodeParts(string message)
    {
        var builder = new StringBuilder();
        var inBody = false;
        var encoded = new StringBuilder();

        foreach (var line in message.Split("\r\n", StringSplitOptions.None))
        {
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                if (encoded.Length > 0)
                {
                    builder.Append(
                        Encoding.UTF8.GetString(Convert.FromBase64String(encoded.ToString())));
                    encoded.Clear();
                }

                inBody = false;

                continue;
            }

            if (line.Length == 0)
            {
                inBody = true;

                continue;
            }

            if (inBody)
            {
                encoded.Append(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether the message has a header with this name.
    /// </summary>
    /// <remarks>
    /// Only the header block is examined, which ends at the first blank line.
    /// Searching the whole message would find the word inside a base64 body
    /// and report an injection that is not there.
    /// </remarks>
    private static bool HasHeader(string message, string name)
    {
        foreach (var line in message.Split("\r\n", StringSplitOptions.None))
        {
            if (line.Length == 0)
            {
                return false;
            }

            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string LineStartingWith(string message, string prefix) =>
        message
            .Split("\r\n", StringSplitOptions.None)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
        ?? string.Empty;

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
