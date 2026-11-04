using LeaseReminder.Domain;

using Microsoft.EntityFrameworkCore;

namespace LeaseReminder.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping for the document and extraction tables.
/// </summary>
/// <remarks>
/// <para>
/// Reads go through these entities. The upload path writes
/// <c>lease_documents</c> through the change tracker, which is safe because a
/// document row stands alone: unlike a task move, there is no second row that
/// has to commit with it.
/// </para>
/// <para>
/// Extraction results do not work that way. An extraction, its forty fields,
/// and its issues are one unit — a half-written extraction would show a
/// reviewer a partial lease and invite them to approve it — so those are
/// written by a database function in one transaction, as the maintenance
/// module's writes are.
/// </para>
/// </remarks>
internal static class DocumentModelConfiguration
{
    internal static void ConfigureDocuments(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<LeaseDocument>(entity =>
        {
            entity.ToTable("lease_documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.LeaseId).HasColumnName("lease_id");
            entity.Property(e => e.StorageBucket).HasColumnName("storage_bucket");
            entity.Property(e => e.StoragePath).HasColumnName("storage_path");
            entity.Property(e => e.OriginalFilename).HasColumnName("original_filename");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.DeclaredMimeType).HasColumnName("declared_mime_type");
            entity.Property(e => e.DetectedMimeType).HasColumnName("detected_mime_type");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.Sha256).HasColumnName("sha256");
            entity.Property(e => e.PageCount).HasColumnName("page_count");
            entity.Property(e => e.ProcessingStatus).HasColumnName("processing_status");
            entity.Property(e => e.MalwareScanStatus).HasColumnName("malware_scan_status");
            entity.Property(e => e.ScanCompletedAt).HasColumnName("scan_completed_at");
            entity.Property(e => e.ProcessingError).HasColumnName("processing_error");
            entity.Property(e => e.ProcessingStartedAt).HasColumnName("processing_started_at");
            entity.Property(e => e.ProcessingFinishedAt).HasColumnName("processing_finished_at");
            entity.Property(e => e.UploadedByUserId).HasColumnName("uploaded_by_user_id");
            entity.Property(e => e.UploadedAt).HasColumnName("uploaded_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.PurgedAt).HasColumnName("purged_at");

            entity.HasIndex(e => new { e.OrganizationId, e.UploadedAt });
        });

        modelBuilder.Entity<LeaseExtraction>(entity =>
        {
            entity.ToTable("lease_extractions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.LeaseDocumentId).HasColumnName("lease_document_id");
            entity.Property(e => e.ExtractionVersion).HasColumnName("extraction_version");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Model).HasColumnName("model");
            entity.Property(e => e.PromptVersion).HasColumnName("prompt_version");

            // jsonb. Mapped as a string rather than a typed document so the
            // model's output is stored exactly as received: this column is the
            // audit record of what was said, and a round trip through a
            // serializer is a chance to change it.
            entity.Property(e => e.ExtractedJson)
                .HasColumnName("extracted_json")
                .HasColumnType("jsonb");

            entity.Property(e => e.ReviewStatus).HasColumnName("review_status");
            entity.Property(e => e.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.PagesProcessed).HasColumnName("pages_processed");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => new { e.LeaseDocumentId, e.CreatedAt });
        });

        modelBuilder.Entity<LeaseExtractedField>(entity =>
        {
            entity.ToTable("lease_extracted_fields");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ExtractionId).HasColumnName("extraction_id");
            entity.Property(e => e.FieldName).HasColumnName("field_name");
            entity.Property(e => e.ValueType).HasColumnName("value_type");
            entity.Property(e => e.ExtractedValue).HasColumnName("extracted_value");

            entity.Property(e => e.NormalizedValue)
                .HasColumnName("normalized_value")
                .HasColumnType("jsonb");

            entity.Property(e => e.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            entity.Property(e => e.Provenance).HasColumnName("provenance");
            entity.Property(e => e.SourcePage).HasColumnName("source_page");
            entity.Property(e => e.SourceExcerpt).HasColumnName("source_excerpt");
            entity.Property(e => e.ReviewStatus).HasColumnName("review_status");

            entity.Property(e => e.CorrectedValue)
                .HasColumnName("corrected_value")
                .HasColumnType("jsonb");

            entity.Property(e => e.CorrectedByUserId).HasColumnName("corrected_by_user_id");
            entity.Property(e => e.CorrectedAt).HasColumnName("corrected_at");
            entity.Property(e => e.IssueCodes).HasColumnName("issue_codes");
            entity.Property(e => e.IssueNote).HasColumnName("issue_note");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => new { e.ExtractionId, e.FieldName });
        });

        modelBuilder.Entity<LeaseExtractionIssue>(entity =>
        {
            entity.ToTable("lease_extraction_issues");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ExtractionId).HasColumnName("extraction_id");
            entity.Property(e => e.IssueCode).HasColumnName("issue_code");
            entity.Property(e => e.Severity).HasColumnName("severity");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.RelatedFields).HasColumnName("related_fields");
            entity.Property(e => e.SourcePages).HasColumnName("source_pages");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.ResolvedByUserId).HasColumnName("resolved_by_user_id");
            entity.Property(e => e.ResolutionNote).HasColumnName("resolution_note");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.ExtractionId);
        });
    }
}
