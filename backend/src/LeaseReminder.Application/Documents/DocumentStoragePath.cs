using System.Globalization;

namespace LeaseReminder.Application.Documents;

/// <summary>
/// Builds the object key for a stored lease document.
/// </summary>
/// <remarks>
/// <para>
/// Section 4.C: "Generate random storage object names; do not use
/// user-provided filenames as paths." Both halves matter, and for different
/// reasons.
/// </para>
/// <para>
/// <b>Not the user's filename.</b> A filename is attacker-controlled text that
/// would otherwise be concatenated into a path. That is how traversal
/// (<c>../../other-org/</c>), absolute-path escapes, and null-byte truncation
/// get in. It also leaks: a bucket listing of
/// <c>Smith v Jones settlement - FINAL.pdf</c> tells anyone who sees it
/// something the file's contents were meant to keep.
/// </para>
/// <para>
/// <b>Random, not sequential.</b> A guessable key turns any flaw in a signed
/// URL into an enumeration of the whole bucket. A v4 GUID is 122 random bits,
/// so there is nothing to walk.
/// </para>
/// <para>
/// The original filename is still kept, in
/// <c>lease_documents.original_filename</c>, where it is data to be displayed
/// and escaped rather than a path to be resolved.
/// </para>
/// <para>
/// The shape produced here is enforced independently by two CHECK constraints
/// on <c>lease_documents</c>: one pins the exact format with a regex, the
/// other requires the path to sit under its own row's organization prefix. So
/// a mistake in this method cannot write a row; it fails the insert.
/// </para>
/// </remarks>
public static class DocumentStoragePath
{
    /// <summary>
    /// Builds a storage key for a new document.
    /// </summary>
    /// <param name="organizationId">The owning organization.</param>
    /// <param name="documentId">
    /// The document's own id. Passed in rather than generated here so the
    /// caller can use the same value for the database row's primary key,
    /// which keeps the object and the row trivially correlatable during an
    /// incident.
    /// </param>
    /// <param name="format">The format established from the file's content.</param>
    public static string Build(Guid organizationId, Guid documentId, DocumentFormat format)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty organization id would place the object outside any tenant prefix.",
                nameof(organizationId));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty document id is not random and would collide.",
                nameof(documentId));
        }

        var extension = FileInspectionResult.Accepted(format).Extension
            ?? throw new ArgumentOutOfRangeException(nameof(format));

        // "D" format, lowercase, hyphenated: 8-4-4-4-12. This is exactly what
        // the database regex expects, and ToString("D") on .NET is already
        // lowercase, but the invariant culture is named explicitly so a
        // culture with different digit shaping cannot alter it.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"org/{organizationId:D}/documents/{documentId:D}.{extension}");
    }

    /// <summary>
    /// Checks that a path belongs to the given organization.
    /// </summary>
    /// <remarks>
    /// Used before issuing a signed URL. The database's Row Level Security
    /// already prevents reading another organization's document row, so by the
    /// time a path is in hand it should be the caller's. This asserts that
    /// rather than assuming it: issuing a signed URL is the one operation that
    /// hands out access outside the database's reach, so it is worth one
    /// string comparison to be sure a bug upstream cannot turn into a
    /// cross-tenant read.
    /// </remarks>
    public static bool BelongsToOrganization(string? storagePath, Guid organizationId)
    {
        if (string.IsNullOrEmpty(storagePath) || organizationId == Guid.Empty)
        {
            return false;
        }

        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"org/{organizationId:D}/documents/");

        return storagePath.StartsWith(prefix, StringComparison.Ordinal)
            // Nothing further may introduce another path segment: the object
            // name is a single file directly under the prefix.
            && !storagePath.AsSpan(prefix.Length).Contains('/');
    }
}
