using System.Text.RegularExpressions;

using LeaseReminder.Application.Documents;

using Xunit;

namespace LeaseReminder.UnitTests.Documents;

/// <summary>
/// Tests for the storage key builder.
/// </summary>
public sealed partial class DocumentStoragePathTests
{
    /// <summary>
    /// The regex from <c>lease_documents_storage_path_shape_check</c> in
    /// migration 0003, copied verbatim.
    /// </summary>
    /// <remarks>
    /// Duplicating it is deliberate. The database is the real enforcement, and
    /// this test exists to catch a divergence between the two at build time
    /// rather than on an insert in production. If this pattern and the
    /// migration ever disagree, that is exactly the bug worth failing on.
    /// </remarks>
    [GeneratedRegex(
        @"^org/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/documents/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(pdf|docx|jpg|png)$")]
    private static partial Regex DatabaseConstraint { get; }

    [Theory]
    [InlineData(DocumentFormat.Pdf, "pdf")]
    [InlineData(DocumentFormat.Docx, "docx")]
    [InlineData(DocumentFormat.Jpeg, "jpg")]
    [InlineData(DocumentFormat.Png, "png")]
    public void Builds_a_path_the_database_will_accept(DocumentFormat format, string extension)
    {
        var organizationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var path = DocumentStoragePath.Build(organizationId, documentId, format);

        Assert.Matches(DatabaseConstraint, path);
        Assert.EndsWith($".{extension}", path, StringComparison.Ordinal);
        Assert.Equal($"org/{organizationId:D}/documents/{documentId:D}.{extension}", path);
    }

    /// <summary>
    /// JPEG's extension is "jpg" while its media type is "image/jpeg". Both
    /// spellings appear in the database constraints, in different columns, and
    /// mixing them up produces a row that fails one check or the other.
    /// </summary>
    [Fact]
    public void Uses_jpg_for_the_extension_and_image_jpeg_for_the_media_type()
    {
        var path = DocumentStoragePath.Build(Guid.NewGuid(), Guid.NewGuid(), DocumentFormat.Jpeg);

        Assert.EndsWith(".jpg", path, StringComparison.Ordinal);
        Assert.Equal("image/jpeg", FileInspectionResult.MimeTypeFor(DocumentFormat.Jpeg));
    }

    /// <summary>
    /// The database regex requires lowercase hex. A GUID formatted by a
    /// culture or format specifier that produced uppercase would fail the
    /// insert.
    /// </summary>
    [Fact]
    public void Writes_identifiers_in_lowercase()
    {
        var path = DocumentStoragePath.Build(Guid.NewGuid(), Guid.NewGuid(), DocumentFormat.Pdf);

        Assert.Equal(path.ToLowerInvariant(), path);
    }

    /// <summary>
    /// The path carries no trace of what the user called the file. This is the
    /// whole point of the class.
    /// </summary>
    [Fact]
    public void Contains_nothing_derived_from_a_filename()
    {
        var organizationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var path = DocumentStoragePath.Build(organizationId, documentId, DocumentFormat.Pdf);

        // Everything in the path is accounted for by the two identifiers and
        // the fixed structure, so there is no room for user input in it.
        var remainder = path
            .Replace(organizationId.ToString("D"), string.Empty, StringComparison.Ordinal)
            .Replace(documentId.ToString("D"), string.Empty, StringComparison.Ordinal);

        Assert.Equal("org//documents/.pdf", remainder);
    }

    [Fact]
    public void Two_documents_never_share_a_path()
    {
        var organizationId = Guid.NewGuid();

        var paths = Enumerable.Range(0, 500)
            .Select(_ => DocumentStoragePath.Build(
                organizationId, Guid.NewGuid(), DocumentFormat.Pdf))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(500, paths.Count);
    }

    [Fact]
    public void Refuses_an_empty_organization_id()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentStoragePath.Build(Guid.Empty, Guid.NewGuid(), DocumentFormat.Pdf));
    }

    [Fact]
    public void Refuses_an_empty_document_id()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentStoragePath.Build(Guid.NewGuid(), Guid.Empty, DocumentFormat.Pdf));
    }

    // ---------------------------------------------------------------------
    // Ownership, checked before a signed URL is issued
    // ---------------------------------------------------------------------

    [Fact]
    public void Recognises_a_path_belonging_to_the_organization()
    {
        var organizationId = Guid.NewGuid();
        var path = DocumentStoragePath.Build(organizationId, Guid.NewGuid(), DocumentFormat.Pdf);

        Assert.True(DocumentStoragePath.BelongsToOrganization(path, organizationId));
    }

    [Fact]
    public void Rejects_a_path_belonging_to_another_organization()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var path = DocumentStoragePath.Build(theirs, Guid.NewGuid(), DocumentFormat.Pdf);

        Assert.False(DocumentStoragePath.BelongsToOrganization(path, mine));
    }

    /// <summary>
    /// A path that starts with the right prefix but then introduces another
    /// segment is refused. Without the segment check, a crafted path could sit
    /// under the prefix and still address a different object.
    /// </summary>
    [Fact]
    public void Rejects_a_path_that_adds_a_further_segment()
    {
        var organizationId = Guid.NewGuid();
        var nested = $"org/{organizationId:D}/documents/nested/file.pdf";

        Assert.False(DocumentStoragePath.BelongsToOrganization(nested, organizationId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("documents/file.pdf")]
    [InlineData("../org/documents/file.pdf")]
    public void Rejects_paths_that_are_missing_or_malformed(string? path)
    {
        Assert.False(DocumentStoragePath.BelongsToOrganization(path, Guid.NewGuid()));
    }

    [Fact]
    public void Rejects_an_empty_organization_id_when_checking_ownership()
    {
        var path = DocumentStoragePath.Build(Guid.NewGuid(), Guid.NewGuid(), DocumentFormat.Pdf);

        Assert.False(DocumentStoragePath.BelongsToOrganization(path, Guid.Empty));
    }
}
