using System.Security.Cryptography;

using LeaseReminder.Application.Documents;
using LeaseReminder.Domain;
using LeaseReminder.Infrastructure.Persistence;
using LeaseReminder.Infrastructure.Storage;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace LeaseReminder.Api.Endpoints;

/// <summary>
/// Lease document upload, review, and approval.
/// </summary>
/// <remarks>
/// <para>
/// Reads use EF, filtered by Row Level Security. Every mutation goes through
/// a SECURITY DEFINER function in db/migrations/0019, for the same reason the
/// maintenance module does: the write has to be atomic, and in the approval
/// case because that function is the single enforced path to a lease that can
/// send mail.
/// </para>
/// <para>
/// <b>The upload path treats the file as hostile.</b> The filename is never
/// used to build a path, the declared content type is never believed, and the
/// bytes are inspected before anything is stored. Section 4.C.
/// </para>
/// </remarks>
public static class DocumentEndpoints
{
    /// <summary>
    /// The largest upload accepted, matching the validator and the database
    /// CHECK constraint on <c>file_size_bytes</c>.
    /// </summary>
    private const long MaximumUploadBytes = FileSignatureInspector.MaximumFileSizeBytes;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapUpload(routes);
        MapReads(routes);
        MapReview(routes);

        return routes;
    }

    // =========================================================================
    // Upload
    // =========================================================================

    private static void MapUpload(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/organizations/{organizationId:guid}/documents", async (
            Guid organizationId,
            IFormFile file,
            // An optional label, from a form field beside the file. Blank or
            // absent leaves the document showing its filename, which is the
            // same thing the list falls back to.
            [FromForm] string? name,
            AppDbContext dbContext,
            IDocumentStorage storage,
            ILeaseExtractor extractor,
            CancellationToken cancellationToken) =>
        {
            var role = await EndpointSupport
                .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                .ConfigureAwait(false);

            if (role is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            if (!EndpointSupport.CanWrite(role.Value))
            {
                return EndpointSupport.Forbidden(
                    "Your role does not permit uploading documents.");
            }

            if (!storage.IsConfigured)
            {
                // Refused before reading the body. Accepting a 50 MB upload
                // and then discovering there is nowhere to put it wastes the
                // user's time and the server's memory.
                return Results.Problem(
                    title: "Document storage is not configured",
                    detail: "Lease documents cannot be uploaded until storage is set up.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (file is null || file.Length == 0)
            {
                return Results.Problem(
                    title: "No file was uploaded",
                    detail: "Attach a PDF, Word document, or photograph of the lease.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > MaximumUploadBytes)
            {
                return Results.Problem(
                    title: "The file is too large",
                    detail: $"The limit is {MaximumUploadBytes / 1024 / 1024} MB.",
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // Buffered to a temporary file rather than held in memory. A 50 MB
            // upload per concurrent request is a denial-of-service waiting to
            // happen on a small instance, and the inspector needs to seek
            // anyway. DeleteOnClose means the file is gone when the stream is
            // disposed, including if this request throws.
            await using var buffer = new FileStream(
                Path.GetTempFileName(),
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous,
                });

            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            // The content decides what this file is. The filename and the
            // declared type are both attacker-controlled and neither is
            // trusted; the declaration is used only to detect disagreement.
            var inspection = await FileSignatureInspector
                .InspectAsync(buffer, file.ContentType, cancellationToken)
                .ConfigureAwait(false);

            if (!inspection.IsAccepted)
            {
                return Results.Problem(
                    title: "This file cannot be used as a lease document",
                    detail: inspection.RejectionDetail,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var documentId = Guid.CreateVersion7();

            // Server-generated, random, and under this organization's prefix.
            // Nothing the user supplied reaches it.
            var storagePath = DocumentStoragePath.Build(
                organizationId, documentId, inspection.Format!.Value);

            buffer.Position = 0;
            var sha256 = await SHA256.HashDataAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            buffer.Position = 0;

            // Storage first, then the row. The other order would leave a row
            // pointing at an object that does not exist, which the worker
            // would claim and fail on; this order can at worst orphan an
            // object, which a sweep can find.
            var stored = await storage
                .UploadAsync(storagePath, buffer, inspection.DetectedMimeType!, cancellationToken)
                .ConfigureAwait(false);

            if (!stored.IsSuccess)
            {
                return Results.Problem(
                    title: "The document could not be stored",
                    detail: stored.Error,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"""
                         SELECT app.create_lease_document(
                           {organizationId}, {storage.BucketName}, {storagePath},
                           {file.FileName}, {file.ContentType ?? "application/octet-stream"},
                           {inspection.DetectedMimeType}, {file.Length}, {sha256},
                           {documentId})
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // The row failed, so the object is orphaned. Remove it rather
                // than leaving a file nothing references.
                await storage.DeleteAsync(storagePath, cancellationToken).ConfigureAwait(false);

                return EndpointSupport.Forbidden(exception.MessageText);
            }

            // Named in a second statement rather than as a tenth argument to
            // create_lease_document, which is already at its limit of
            // positional parameters and whose signature is relied on by the
            // worker's own tests. A failure here is not worth failing the
            // upload over: the document exists and can be renamed.
            var trimmedName = name?.Trim();

            if (!string.IsNullOrEmpty(trimmedName))
            {
                try
                {
                    await dbContext.Database
                        .ExecuteSqlInterpolatedAsync(
                            $"SELECT app.rename_lease_document({documentId}, {trimmedName})",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PostgresException)
                {
                    // Deliberately swallowed. The upload succeeded, and
                    // reporting a failure now would suggest otherwise.
                    trimmedName = null;
                }
            }

            return Results.Created(
                $"/documents/{documentId}",
                new
                {
                    id = documentId,
                    originalFilename = file.FileName,
                    displayName = trimmedName,
                    detectedMimeType = inspection.DetectedMimeType,
                    fileSizeBytes = file.Length,
                    processingStatus = "queued",
                    // So the UI can say "this will need entering by hand"
                    // immediately rather than after a worker cycle.
                    automaticExtractionAvailable = extractor.IsConfigured,
                });
        })
        .DisableAntiforgery()
        .WithName("UploadLeaseDocument");
    }

    // =========================================================================
    // Reads
    // =========================================================================

    private static void MapReads(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/organizations/{organizationId:guid}/documents", async (
            Guid organizationId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointSupport
                    .RequireMembershipAsync(dbContext, organizationId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return EndpointSupport.Forbidden("You are not a member of this organization.");
            }

            var documents = await dbContext.LeaseDocuments
                .AsNoTracking()
                .Where(document => document.OrganizationId == organizationId
                                   && document.DeletedAt == null)
                .OrderByDescending(document => document.UploadedAt)
                .Select(document => new
                {
                    document.Id,
                    document.OriginalFilename,
                    document.DisplayName,
                    document.DetectedMimeType,
                    document.FileSizeBytes,
                    document.PageCount,
                    document.ProcessingStatus,
                    document.ProcessingError,
                    document.LeaseId,
                    document.UploadedAt,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(documents);
        })
        .WithName("ListLeaseDocuments");

        // The review screen's payload: the document, its latest extraction,
        // every field, and every issue, in one request.
        routes.MapGet("/documents/{documentId:guid}", async (
            Guid documentId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var document = await dbContext.LeaseDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            // Row Level Security already hides another organization's
            // document, so "not found" here covers both "no such document" and
            // "not yours" -- which is the right answer to give in either case.
            if (document is null || document.DeletedAt is not null)
            {
                return EndpointSupport.NotFound("document");
            }

            var extraction = await dbContext.LeaseExtractions
                .AsNoTracking()
                .Where(e => e.LeaseDocumentId == documentId)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (extraction is null)
            {
                return Results.Ok(new
                {
                    document = Describe(document),
                    extraction = (object?)null,
                    fields = Array.Empty<object>(),
                    issues = Array.Empty<object>(),
                    catalogue = DescribeCatalogue(),
                });
            }

            var fields = await dbContext.LeaseExtractedFields
                .AsNoTracking()
                .Where(f => f.ExtractionId == extraction.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var issues = await dbContext.LeaseExtractionIssues
                .AsNoTracking()
                .Where(i => i.ExtractionId == extraction.Id)
                .Select(i => new
                {
                    i.Id,
                    i.IssueCode,
                    i.Severity,
                    i.Message,
                    i.SourcePages,
                    IsResolved = i.ResolvedAt != null,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Ordered by the catalogue, not by the database. The review screen
            // reads top to bottom in the order a person thinks about a lease:
            // who and where, then when, then how much.
            var order = LeaseFieldCatalog.FieldNames
                .Select((name, index) => (name, index))
                .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);

            var ordered = fields
                .OrderBy(f => order.TryGetValue(f.FieldName, out var index) ? index : int.MaxValue)
                .Select(f => new
                {
                    f.Id,
                    f.FieldName,
                    f.ValueType,
                    f.ExtractedValue,
                    f.NormalizedValue,
                    f.Confidence,
                    f.Provenance,
                    f.SourcePage,
                    f.SourceExcerpt,
                    f.ReviewStatus,
                    f.CorrectedValue,
                    f.IssueCodes,
                })
                .ToList();

            return Results.Ok(new
            {
                document = Describe(document),
                extraction = new
                {
                    extraction.Id,
                    extraction.Provider,
                    extraction.Model,
                    extraction.ExtractionVersion,
                    extraction.ReviewStatus,
                    extraction.CompletedAt,
                    extraction.ErrorMessage,
                    // What still stands between this and approval. Computed
                    // here so the UI does not have to re-derive the rule the
                    // database will enforce anyway.
                    PendingFieldCount = fields.Count(f =>
                        f.ReviewStatus is FieldReviewStatus.Pending or FieldReviewStatus.Flagged),
                    BlockingIssueCount = issues.Count(i =>
                        i.Severity == "blocking" && !i.IsResolved),
                },
                fields = ordered,
                issues,
                catalogue = DescribeCatalogue(),
            });
        })
        .WithName("GetLeaseDocument");

        // A short-lived URL for the viewer. The bytes never pass through this
        // application: proxying a 50 MB file would tie up a request thread for
        // the length of a download.
        routes.MapGet("/documents/{documentId:guid}/url", async (
            Guid documentId,
            AppDbContext dbContext,
            IDocumentStorage storage,
            CancellationToken cancellationToken) =>
        {
            var document = await dbContext.LeaseDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            if (document is null || document.DeletedAt is not null)
            {
                return EndpointSupport.NotFound("document");
            }

            // Authorization happened above: Row Level Security would not have
            // returned this row to a caller outside the organization. The
            // ownership check here is a second assertion before handing out a
            // credential that works outside the database's reach.
            if (!DocumentStoragePath.BelongsToOrganization(
                    document.StoragePath, document.OrganizationId))
            {
                return EndpointSupport.NotFound("document");
            }

            var signed = await storage
                .CreateSignedUrlAsync(document.StoragePath, cancellationToken)
                .ConfigureAwait(false);

            if (signed is null)
            {
                return Results.Problem(
                    title: "The document could not be opened",
                    detail: "A viewing link could not be created. Try again shortly.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new { url = signed.Url, expiresAt = signed.ExpiresAt });
        })
        .WithName("GetLeaseDocumentUrl");
    }

    // =========================================================================
    // Review and approval
    // =========================================================================

    private static void MapReview(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/extracted-fields/{fieldId:guid}/review", async (
            Guid fieldId,
            [FromBody] FieldReviewRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    title: "A decision is required",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"""
                         SELECT app.correct_extracted_field(
                           {fieldId}, {request.Decision}, {request.CorrectedValue}::jsonb)
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("field");
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                return Results.Problem(
                    title: "That decision cannot be recorded",
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        })
        .WithName("ReviewExtractedField");

        // There is no approval endpoint here on purpose. Approval lives on
        // the lease, at POST /organizations/{id}/leases/{id}/approve, and that
        // endpoint also settles lifecycle and automation. A second route that
        // approved a lease would be a second place for the rules to drift.

        routes.MapDelete("/documents/{documentId:guid}", async (
            Guid documentId,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.soft_delete_lease_document({documentId})",
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("document");
            }
        })
        .WithName("DeleteLeaseDocument");

        // Renaming. A label change, so it sits at write level rather than
        // administrator level: the people who may upload a document may name
        // it. Deleting one stays administrator-only.
        //
        // An empty name clears the label rather than storing a blank, and the
        // document falls back to showing its filename.
        routes.MapPatch("/documents/{documentId:guid}", async (
            Guid documentId,
            RenameDocumentRequest request,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                await dbContext.Database
                    .ExecuteSqlInterpolatedAsync(
                        $"SELECT app.rename_lease_document({documentId}, {request.Name})",
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.NoContent();
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return EndpointSupport.Forbidden(exception.MessageText);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.NoDataFound)
            {
                return EndpointSupport.NotFound("document");
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.StringDataRightTruncation)
            {
                return Results.Problem(
                    detail: exception.MessageText,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "That name is too long");
            }
        })
        .WithName("RenameLeaseDocument");
    }

    // =========================================================================
    // Shaping
    // =========================================================================

    private static object Describe(LeaseDocument document) => new
    {
        document.Id,
        document.OrganizationId,
        document.LeaseId,
        document.OriginalFilename,
        document.DisplayName,
        document.DetectedMimeType,
        document.FileSizeBytes,
        document.PageCount,
        document.ProcessingStatus,
        document.ProcessingError,
        document.UploadedAt,
    };

    /// <summary>
    /// The field catalogue, so the review screen can label and group what it
    /// receives.
    /// </summary>
    /// <remarks>
    /// Sent with the document rather than duplicated in the frontend. The
    /// catalogue is what the model was asked for; a second copy in TypeScript
    /// would be a second thing to keep in step, and the symptom of their
    /// drifting apart would be a field displayed without a label.
    /// </remarks>
    private static object DescribeCatalogue() =>
        LeaseFieldCatalog.Fields.Select(field => new
        {
            field.Name,
            field.Label,
            field.Group,
            field.Description,
            Type = field.ValueTypeName,
            Importance = field.Importance.ToString(),
            field.AllowedValues,
        });
}

/// <summary>A reviewer's decision on one extracted field.</summary>
/// <param name="Decision">"accepted", "corrected", or "rejected".</param>
/// <param name="CorrectedValue">
/// Required when correcting. JSON, so a corrected date stays a date rather
/// than becoming the string a form field happened to produce.
/// </param>
public sealed record FieldReviewRequest(string Decision, string? CorrectedValue);

/// <summary>
/// A document's editable label. Null or blank clears it.
/// </summary>
public sealed record RenameDocumentRequest(string? Name);
