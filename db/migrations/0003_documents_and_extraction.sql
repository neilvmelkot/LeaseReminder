-- =============================================================================
-- 0003_documents_and_extraction.sql
-- Uploaded lease documents, extracted page text, and AI extraction results.
--
-- Two principles are encoded here:
--
--  1. A document is UNTRUSTED INPUT. Its storage path is server-generated and
--     constrained by a CHECK so a user-supplied filename can never become a
--     path. The original filename is kept only as a display label.
--
--  2. Extraction produces a PROPOSAL, never a fact. Extracted values live in
--     their own tables with confidence, provenance, source page, and a
--     supporting excerpt, and are copied onto the lease only when a human
--     approves them (section 13.C).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- lease_documents
-- -----------------------------------------------------------------------------
create table lease_documents (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null references organizations (id) on delete cascade,
  -- A document may be uploaded before its lease record exists. The lease is
  -- created or linked at approval time.
  lease_id         uuid,

  -- Storage ---------------------------------------------------------------
  storage_bucket   text not null,
  -- Server-generated. Must NOT be derived from the uploaded filename.
  storage_path     text not null,
  original_filename text not null,

  -- What the client claimed vs. what the file signature actually says. Both
  -- are retained so a mismatch is auditable after the fact.
  declared_mime_type text not null,
  detected_mime_type text,

  file_size_bytes  bigint not null,
  -- Content hash. Used to detect re-uploads of the same document.
  sha256           bytea,
  page_count       integer,

  -- Independent state machines (section 13.E.1) ---------------------------
  processing_status   app.document_processing_status not null default 'uploaded',
  malware_scan_status app.malware_scan_status not null default 'pending',
  scan_completed_at   timestamptz,
  -- Operator-safe reason. Never contains document content.
  processing_error    text,
  processing_started_at  timestamptz,
  processing_finished_at timestamptz,

  uploaded_by_user_id uuid references users (id) on delete set null,
  uploaded_at      timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  -- Soft delete, so retention policy and audit trail survive a user deletion.
  deleted_at       timestamptz,
  -- Set when the object has actually been removed from Storage.
  purged_at        timestamptz,

  constraint lease_documents_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete set null,

  -- The path is machine-generated: org/<org uuid>/documents/<uuid>.<ext>
  -- A user-controlled filename cannot satisfy this, so path traversal and
  -- cross-organization writes are structurally impossible.
  constraint lease_documents_storage_path_shape_check
    check (storage_path ~ '^org/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/documents/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(pdf|docx|jpg|png)$'),
  -- The path must sit under this row's own organization prefix.
  constraint lease_documents_storage_path_tenant_check
    check (storage_path like ('org/' || organization_id::text || '/documents/%')),

  constraint lease_documents_original_filename_length_check
    check (char_length(original_filename) between 1 and 400),

  -- Allowlist of supported formats (section 4.C). Enforced in the API from the
  -- file signature as well; this is the last line of defence.
  constraint lease_documents_declared_mime_check
    check (declared_mime_type in (
      'application/pdf',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      'image/jpeg',
      'image/png'
    )),
  constraint lease_documents_detected_mime_check
    check (detected_mime_type is null or detected_mime_type in (
      'application/pdf',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      'image/jpeg',
      'image/png'
    )),

  -- 50 MB ceiling, and a non-empty file.
  constraint lease_documents_file_size_check
    check (file_size_bytes > 0 and file_size_bytes <= 52428800),
  constraint lease_documents_page_count_check
    check (page_count is null or page_count between 1 and 400),
  constraint lease_documents_sha256_length_check
    check (sha256 is null or octet_length(sha256) = 32),

  -- An infected or unvalidated file must never reach extraction.
  constraint lease_documents_infected_is_quarantined_check
    check (malware_scan_status <> 'infected' or processing_status = 'quarantined'),
  constraint lease_documents_extraction_requires_scan_check
    check (
      processing_status not in ('queued', 'extracting', 'review_ready')
      or malware_scan_status in ('clean', 'skipped')
    ),

  constraint lease_documents_id_organization_key unique (id, organization_id)
);

comment on table lease_documents is
  'An uploaded lease file. Stored in a private bucket under a server-generated path.';
comment on column lease_documents.storage_path is
  'Server-generated object key. Constrained so a user filename can never become a path.';
comment on column lease_documents.declared_mime_type is
  'Content type claimed by the client. Never trusted on its own.';
comment on column lease_documents.detected_mime_type is
  'Content type determined from the file signature server-side.';
comment on column lease_documents.processing_error is
  'Operator-safe failure reason. Must never contain document text.';

create unique index lease_documents_storage_path_key
  on lease_documents (storage_bucket, storage_path);

create index lease_documents_organization_idx on lease_documents (organization_id, uploaded_at desc);
create index lease_documents_lease_idx on lease_documents (lease_id);
create index lease_documents_processing_status_idx
  on lease_documents (processing_status)
  where deleted_at is null;
-- Finds duplicate uploads of the same content within an organization.
create index lease_documents_sha256_idx on lease_documents (organization_id, sha256)
  where sha256 is not null;
-- Retention sweep: soft-deleted objects still present in Storage.
create index lease_documents_purge_queue_idx on lease_documents (deleted_at)
  where deleted_at is not null and purged_at is null;

create trigger lease_documents_touch_updated_at
  before update on lease_documents
  for each row execute function app.touch_updated_at();

-- Close the provenance loop deferred from 0002.
alter table leases
  add constraint leases_source_document_fk
  foreign key (source_document_id, organization_id)
  references lease_documents (id, organization_id) on delete set null;

-- -----------------------------------------------------------------------------
-- lease_document_pages
-- Per-page extracted text, used to show the excerpt that supports a field.
--
-- This is the most sensitive table in the schema: it holds lease prose. It is
-- never logged, never sent anywhere whole, and is covered by the retention
-- policy in docs/security.md.
-- -----------------------------------------------------------------------------
create table lease_document_pages (
  id                uuid primary key default gen_random_uuid(),
  organization_id   uuid not null,
  lease_document_id uuid not null,
  page_number       integer not null,
  -- NULL when a page yielded no text (a blank or image-only page before OCR).
  text_content      text,
  -- How the text was obtained, so a reviewer knows whether to distrust it.
  extracted_by      text not null default 'native',
  ocr_confidence    numeric(5, 2),
  char_count        integer not null default 0,
  created_at        timestamptz not null default now(),

  constraint lease_document_pages_document_fk
    foreign key (lease_document_id, organization_id)
    references lease_documents (id, organization_id) on delete cascade,

  constraint lease_document_pages_page_number_check
    check (page_number between 1 and 400),
  constraint lease_document_pages_extracted_by_check
    check (extracted_by in ('native', 'ocr', 'mixed')),
  constraint lease_document_pages_ocr_confidence_check
    check (ocr_confidence is null or ocr_confidence between 0 and 100),
  constraint lease_document_pages_char_count_check
    check (char_count >= 0),
  -- Guards against a malformed parser result exhausting storage.
  constraint lease_document_pages_text_length_check
    check (text_content is null or char_length(text_content) <= 1000000),

  constraint lease_document_pages_document_page_key
    unique (lease_document_id, page_number)
);

comment on table lease_document_pages is
  'Extracted text per page. Holds lease prose: treat as sensitive, never log, honour retention.';
comment on column lease_document_pages.extracted_by is
  'native = embedded text layer, ocr = optical recognition, mixed = both.';

create index lease_document_pages_organization_idx
  on lease_document_pages (organization_id);

-- -----------------------------------------------------------------------------
-- lease_extractions
-- One run of the extraction pipeline over one document. Re-running creates a
-- new row; prior runs and the corrections made against them are preserved.
-- -----------------------------------------------------------------------------
create table lease_extractions (
  id                uuid primary key default gen_random_uuid(),
  organization_id   uuid not null,
  lease_document_id uuid not null,

  -- Reproducibility: which pipeline and which model produced this (section 13.D).
  extraction_version text not null,
  provider           text not null,
  model              text,
  prompt_version     text,

  -- Raw structured output, after schema validation but before any field-level
  -- business validation. Kept verbatim for audit.
  extracted_json     jsonb,

  review_status      app.extraction_review_status not null default 'pending',
  reviewed_by_user_id uuid references users (id) on delete set null,
  reviewed_at        timestamptz,

  started_at         timestamptz,
  completed_at       timestamptz,
  -- Operator-safe reason only.
  error_message      text,

  -- Token/page accounting for cost monitoring and rate limiting.
  pages_processed    integer,

  created_at         timestamptz not null default now(),

  constraint lease_extractions_document_fk
    foreign key (lease_document_id, organization_id)
    references lease_documents (id, organization_id) on delete cascade,

  constraint lease_extractions_version_length_check
    check (char_length(extraction_version) between 1 and 60),
  constraint lease_extractions_provider_length_check
    check (char_length(provider) between 1 and 100),
  constraint lease_extractions_json_is_object_check
    check (extracted_json is null or jsonb_typeof(extracted_json) = 'object'),
  constraint lease_extractions_reviewed_metadata_check
    check (
      (review_status = 'pending' and reviewed_at is null and reviewed_by_user_id is null)
      or (review_status <> 'pending' and reviewed_at is not null)
    ),

  constraint lease_extractions_id_organization_key unique (id, organization_id)
);

comment on table lease_extractions is
  'One extraction run. Records provider, model, and pipeline version for reproducibility.';
comment on column lease_extractions.extracted_json is
  'Schema-validated model output, kept verbatim. Data only; never interpreted as instructions.';

create index lease_extractions_document_idx
  on lease_extractions (lease_document_id, created_at desc);
create index lease_extractions_organization_idx on lease_extractions (organization_id);

-- -----------------------------------------------------------------------------
-- lease_extracted_fields
-- The reviewable unit. One row per candidate lease term.
-- -----------------------------------------------------------------------------
create table lease_extracted_fields (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  extraction_id   uuid not null,

  -- Dotted path into the lease model, e.g. 'rent.amount', 'term.end_date'.
  field_name      text not null,
  -- Declared shape, so the API validates before coercing.
  value_type      text not null,

  -- As produced by the model, normalised to text for display and diffing.
  extracted_value text,
  -- Typed form after backend validation: {"date":"2026-06-01"} etc.
  -- NULL means the value failed validation and must be corrected by hand.
  normalized_value jsonb,

  -- Review guidance only. Never treated as proof of correctness (section 4.D).
  confidence      numeric(5, 4),
  -- Separates a stated fact from a calculated one (section 13.D).
  provenance      app.value_provenance not null default 'stated_in_document',

  source_page     integer,
  -- Short supporting quote. Bounded so it cannot become a copy of the lease.
  source_excerpt  text,

  review_status   app.field_review_status not null default 'pending',
  -- What the human decided. Distinct from extracted_value, so the model's
  -- original answer and the correction are both preserved.
  corrected_value jsonb,
  corrected_by_user_id uuid references users (id) on delete set null,
  corrected_at    timestamptz,

  -- Machine-readable review reasons, e.g. ['missing', 'conflicting_clause'].
  issue_codes     text[] not null default '{}',
  -- Human-readable note from the pipeline explaining a flag.
  issue_note      text,

  created_at      timestamptz not null default now(),

  constraint lease_extracted_fields_extraction_fk
    foreign key (extraction_id, organization_id)
    references lease_extractions (id, organization_id) on delete cascade,

  constraint lease_extracted_fields_field_name_check
    check (field_name ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$'
           and char_length(field_name) <= 120),
  constraint lease_extracted_fields_value_type_check
    check (value_type in (
      'string', 'text', 'date', 'money', 'integer', 'decimal',
      'boolean', 'enum', 'duration_days', 'json'
    )),
  constraint lease_extracted_fields_confidence_check
    check (confidence is null or confidence between 0 and 1),
  constraint lease_extracted_fields_source_page_check
    check (source_page is null or source_page between 1 and 400),
  constraint lease_extracted_fields_excerpt_length_check
    check (source_excerpt is null or char_length(source_excerpt) <= 2000),
  constraint lease_extracted_fields_extracted_value_length_check
    check (extracted_value is null or char_length(extracted_value) <= 4000),
  constraint lease_extracted_fields_issue_note_length_check
    check (issue_note is null or char_length(issue_note) <= 2000),
  constraint lease_extracted_fields_issue_codes_bounded_check
    check (array_length(issue_codes, 1) is null or array_length(issue_codes, 1) <= 20),
  constraint lease_extracted_fields_corrected_metadata_check
    check (
      (review_status = 'corrected' and corrected_at is not null)
      or review_status <> 'corrected'
    ),

  constraint lease_extracted_fields_extraction_field_key
    unique (extraction_id, field_name)
);

comment on table lease_extracted_fields is
  'One candidate lease term per row, with confidence, provenance, source page, and supporting excerpt.';
comment on column lease_extracted_fields.confidence is
  'Model self-reported confidence. Review guidance only; it is not evidence of correctness.';
comment on column lease_extracted_fields.normalized_value is
  'Typed value after backend validation. NULL means validation failed and a human must supply it.';
comment on column lease_extracted_fields.provenance is
  'Whether the value was stated in the document or derived by calculation.';

create index lease_extracted_fields_extraction_idx
  on lease_extracted_fields (extraction_id, field_name);
create index lease_extracted_fields_organization_idx
  on lease_extracted_fields (organization_id);
-- Drives the review screen: show everything still needing a decision first.
create index lease_extracted_fields_needs_review_idx
  on lease_extracted_fields (extraction_id)
  where review_status in ('pending', 'flagged');

-- -----------------------------------------------------------------------------
-- lease_extraction_issues
-- Problems that are not attributable to a single field: two clauses that
-- contradict each other, a term referenced but never stated, and so on.
-- -----------------------------------------------------------------------------
create table lease_extraction_issues (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  extraction_id   uuid not null,

  issue_code      text not null,
  severity        text not null default 'warning',
  message         text not null,
  -- Fields the issue touches, as dotted paths.
  related_fields  text[] not null default '{}',
  source_pages    integer[] not null default '{}',

  resolved_at     timestamptz,
  resolved_by_user_id uuid references users (id) on delete set null,
  resolution_note text,

  created_at      timestamptz not null default now(),

  constraint lease_extraction_issues_extraction_fk
    foreign key (extraction_id, organization_id)
    references lease_extractions (id, organization_id) on delete cascade,

  constraint lease_extraction_issues_code_check
    check (issue_code ~ '^[a-z][a-z0-9_]*$' and char_length(issue_code) <= 80),
  constraint lease_extraction_issues_severity_check
    check (severity in ('info', 'warning', 'blocking')),
  constraint lease_extraction_issues_message_length_check
    check (char_length(message) between 1 and 2000),
  constraint lease_extraction_issues_resolved_metadata_check
    check ((resolved_at is null) = (resolved_by_user_id is null))
);

comment on table lease_extraction_issues is
  'Cross-field extraction problems: contradictions, ambiguity, missing-but-referenced terms.';
comment on column lease_extraction_issues.severity is
  'blocking issues must be resolved before the lease can be approved.';

create index lease_extraction_issues_extraction_idx
  on lease_extraction_issues (extraction_id)
  where resolved_at is null;
create index lease_extraction_issues_organization_idx
  on lease_extraction_issues (organization_id);

-- -----------------------------------------------------------------------------
-- lease_approvals
-- Append-only approval history. A lease row carries its current approval
-- state; this table records how it got there, including re-approvals after a
-- significant term change (section 13.C).
-- -----------------------------------------------------------------------------
create table lease_approvals (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  lease_id        uuid not null,
  extraction_id   uuid,

  decision        app.lease_approval_status not null,
  decided_by_user_id uuid not null references users (id) on delete restrict,
  decided_at      timestamptz not null default now(),
  reason          text,

  -- Snapshot of the lease terms as approved, so a later diff can tell whether
  -- re-approval is required.
  approved_terms  jsonb,
  -- Set when this decision was triggered by a change to an already-approved lease.
  supersedes_approval_id uuid references lease_approvals (id) on delete set null,

  constraint lease_approvals_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint lease_approvals_extraction_fk
    foreign key (extraction_id, organization_id)
    references lease_extractions (id, organization_id) on delete set null,

  constraint lease_approvals_decision_check
    check (decision in ('approved', 'rejected')),
  constraint lease_approvals_reason_length_check
    check (reason is null or char_length(reason) <= 2000),
  constraint lease_approvals_terms_is_object_check
    check (approved_terms is null or jsonb_typeof(approved_terms) = 'object')
);

comment on table lease_approvals is
  'Append-only record of every approval and rejection decision, with the approved term snapshot.';

create index lease_approvals_lease_idx on lease_approvals (lease_id, decided_at desc);
create index lease_approvals_organization_idx on lease_approvals (organization_id);

-- Genuinely append-only, even for the application role.
create trigger lease_approvals_append_only
  before update or delete on lease_approvals
  for each row execute function app.deny_mutation();
