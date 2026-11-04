-- =============================================================================
-- 0019_document_pipeline.sql
--
-- The write path for document intelligence (phase 2).
--
-- Every function here exists for one of two reasons.
--
-- ATOMICITY. An extraction run is one unit: the run, its forty-odd fields, and
-- its issues. A half-written extraction is worse than none, because it shows a
-- reviewer a partial lease and invites them to approve it. The API cannot
-- express "these forty-two rows or none" through a change tracker, so the
-- write is a single function call.
--
-- AUTHORITY. Section 13.C is the central rule of this product: AI extraction
-- alone must never activate a lease or start reminders. That is enforced in
-- app.approve_lease_from_extraction below, which is the only path from an
-- extraction to an active lease, and which refuses to run without a named
-- human approver. The application role is granted no direct INSERT on
-- lease_approvals and no UPDATE on leases' approval column, so there is no
-- second path.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- app.create_lease_document
--
-- Records an uploaded file. Called after the bytes are safely in storage, so a
-- row never refers to an object that does not exist.
-- -----------------------------------------------------------------------------
create or replace function app.create_lease_document(
  p_organization_id   uuid,
  p_storage_bucket    text,
  p_storage_path      text,
  p_original_filename text,
  p_declared_mime     text,
  p_detected_mime     text,
  p_file_size_bytes   bigint,
  p_sha256            bytea,
  p_document_id       uuid default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id     uuid;
  v_document_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'A document upload requires an authenticated user.'
      using errcode = '28000';
  end if;

  -- Membership is re-checked here rather than trusted from the API. This
  -- function is SECURITY DEFINER, so it runs with the owner's rights and Row
  -- Level Security does not apply to it; the check RLS would have performed
  -- has to be performed explicitly instead.
  --
  -- Through app.can_write_org rather than a role list written out again here.
  -- The set of roles that may write is a product decision that belongs in one
  -- place, and spelling it out at each call site is how one of them ends up
  -- disagreeing with the others.
  if not app.can_write_org(p_organization_id) then
    raise exception 'You do not have permission to upload documents here.'
      using errcode = '42501';
  end if;

  v_document_id := coalesce(p_document_id, gen_random_uuid());

  insert into lease_documents (
    id, organization_id, storage_bucket, storage_path, original_filename,
    declared_mime_type, detected_mime_type, file_size_bytes, sha256,
    -- The signature was already validated in the API, which is why the scan
    -- status is 'skipped' rather than 'pending': there is no malware scanner
    -- wired in, and leaving it pending would block extraction forever on a
    -- step nothing performs. The CHECK constraint on lease_documents permits
    -- extraction for 'clean' or 'skipped' precisely so this is an explicit
    -- decision rather than an accident.
    malware_scan_status, scan_completed_at,
    processing_status, uploaded_by_user_id
  )
  values (
    v_document_id, p_organization_id, p_storage_bucket, p_storage_path,
    p_original_filename, p_declared_mime, p_detected_mime, p_file_size_bytes,
    p_sha256, 'skipped', now(), 'queued', v_user_id
  );

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (p_organization_id, v_user_id, 'document.uploaded', 'lease_document', v_document_id);

  return v_document_id;
end;
$$;

comment on function app.create_lease_document is
  'Records an uploaded lease file and queues it for extraction. Re-checks membership.';

-- -----------------------------------------------------------------------------
-- app.claim_documents_for_extraction
--
-- The worker's queue. Same FOR UPDATE SKIP LOCKED shape as the other claim
-- functions, so two workers never process the same document.
-- -----------------------------------------------------------------------------
create or replace function app.claim_documents_for_extraction(
  p_batch_size integer default 5
)
returns table (
  document_id       uuid,
  organization_id   uuid,
  storage_bucket    text,
  storage_path      text,
  detected_mime_type text,
  original_filename text,
  file_size_bytes   bigint
)
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
begin
  if p_batch_size is null or p_batch_size < 1 or p_batch_size > 50 then
    raise exception 'Batch size must be between 1 and 50.' using errcode = '22023';
  end if;

  return query
  with claimed as (
    select d.id
    from lease_documents d
    where d.processing_status = 'queued'
      and d.deleted_at is null
      -- Belt and braces: the table's CHECK constraint already forbids
      -- extracting an unscanned document, but a claim that selected one would
      -- fail on update rather than simply not matching.
      and d.malware_scan_status in ('clean', 'skipped')
    order by d.uploaded_at
    for update skip locked
    limit p_batch_size
  )
  update lease_documents d
  set processing_status = 'extracting',
      processing_started_at = now(),
      updated_at = now()
  from claimed c
  where d.id = c.id
  returning
    d.id, d.organization_id, d.storage_bucket, d.storage_path,
    d.detected_mime_type, d.original_filename, d.file_size_bytes;
end;
$$;

comment on function app.claim_documents_for_extraction is
  'Worker queue for document extraction. Claims with SKIP LOCKED so workers never collide.';

-- -----------------------------------------------------------------------------
-- app.record_extraction_result
--
-- The atomic write. The run, every field, and every issue commit together, or
-- nothing does.
--
-- Fields arrive as a jsonb array rather than as separate calls for exactly
-- that reason: forty round trips could be interrupted after twenty.
-- -----------------------------------------------------------------------------
create or replace function app.record_extraction_result(
  p_document_id        uuid,
  p_extraction_version text,
  p_provider           text,
  p_model              text,
  p_prompt_version     text,
  p_extracted_json     jsonb,
  p_fields             jsonb,
  p_issues             jsonb,
  p_page_count         integer default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_organization_id uuid;
  v_extraction_id   uuid;
  v_field           jsonb;
  v_issue           jsonb;
begin
  select d.organization_id into v_organization_id
  from lease_documents d
  where d.id = p_document_id;

  if v_organization_id is null then
    raise exception 'No such document.' using errcode = 'P0002';
  end if;

  insert into lease_extractions (
    organization_id, lease_document_id, extraction_version, provider, model,
    prompt_version, extracted_json, started_at, completed_at, pages_processed
  )
  values (
    v_organization_id, p_document_id, p_extraction_version, p_provider, p_model,
    p_prompt_version, p_extracted_json, now(), now(), p_page_count
  )
  returning id into v_extraction_id;

  -- Fields. Each entry is {name, value_type, extracted_value, normalized_value,
  -- confidence, provenance, source_page, source_excerpt, issue_codes}.
  for v_field in select * from jsonb_array_elements(coalesce(p_fields, '[]'::jsonb))
  loop
    insert into lease_extracted_fields (
      organization_id, extraction_id, field_name, value_type, extracted_value,
      normalized_value, confidence, provenance, source_page, source_excerpt,
      review_status, issue_codes
    )
    values (
      v_organization_id,
      v_extraction_id,
      v_field ->> 'name',
      v_field ->> 'value_type',
      v_field ->> 'extracted_value',
      -- 'null' as a jsonb value and SQL NULL are different things here. A
      -- field whose normalisation failed must store SQL NULL, because that is
      -- what the column's comment defines as "a human must supply this".
      case
        when v_field -> 'normalized_value' is null
          or jsonb_typeof(v_field -> 'normalized_value') = 'null'
        then null
        else v_field -> 'normalized_value'
      end,
      (v_field ->> 'confidence')::numeric,
      coalesce((v_field ->> 'provenance')::app.value_provenance, 'stated_in_document'),
      (v_field ->> 'source_page')::integer,
      v_field ->> 'source_excerpt',
      -- A field the pipeline could not use, or is unsure about, starts
      -- 'flagged' rather than 'pending' so the review screen can put it first
      -- without recomputing the judgement the parser already made.
      case
        when coalesce(jsonb_array_length(v_field -> 'issue_codes'), 0) > 0
          then 'flagged'::app.field_review_status
        else 'pending'::app.field_review_status
      end,
      coalesce(
        array(select jsonb_array_elements_text(v_field -> 'issue_codes')),
        '{}'::text[]
      )
    )
    -- The model occasionally answers the same field twice despite the parser
    -- de-duplicating. Losing the whole run to that would be a poor trade.
    on conflict (extraction_id, field_name) do nothing;
  end loop;

  -- Issues. Each entry is {code, message, severity, source_pages}.
  for v_issue in select * from jsonb_array_elements(coalesce(p_issues, '[]'::jsonb))
  loop
    insert into lease_extraction_issues (
      organization_id, extraction_id, issue_code, severity, message, source_pages
    )
    values (
      v_organization_id,
      v_extraction_id,
      v_issue ->> 'code',
      coalesce(v_issue ->> 'severity', 'warning'),
      left(coalesce(v_issue ->> 'message', 'No detail was given.'), 2000),
      coalesce(
        array(select jsonb_array_elements_text(v_issue -> 'source_pages')::integer),
        '{}'::integer[]
      )
    );
  end loop;

  update lease_documents
  set processing_status = 'review_ready',
      processing_finished_at = now(),
      processing_error = null,
      page_count = coalesce(p_page_count, page_count),
      updated_at = now()
  where id = p_document_id;

  return v_extraction_id;
end;
$$;

comment on function app.record_extraction_result is
  'Writes an extraction run, its fields, and its issues in one transaction.';

-- -----------------------------------------------------------------------------
-- app.fail_extraction
--
-- A document that could not be read still has to reach a human. It becomes a
-- failed document with an operator-safe reason, never a row that disappears.
-- -----------------------------------------------------------------------------
create or replace function app.fail_extraction(
  p_document_id uuid,
  p_reason      text,
  p_retryable   boolean default false
)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
begin
  update lease_documents
  set
    -- A retryable failure goes back on the queue; a permanent one stops and
    -- waits for a person. Without the distinction a rate limit would
    -- permanently fail every document it touched.
    processing_status = case
      when p_retryable then 'queued'::app.document_processing_status
      else 'failed'::app.document_processing_status
    end,
    processing_finished_at = case when p_retryable then null else now() end,
    processing_error = left(p_reason, 1000),
    updated_at = now()
  where id = p_document_id;
end;
$$;

comment on function app.fail_extraction is
  'Records an extraction failure with an operator-safe reason. Never stores document content.';

-- -----------------------------------------------------------------------------
-- app.correct_extracted_field
--
-- A reviewer's decision on one field. The model's answer is never overwritten:
-- extracted_value stays, corrected_value is added beside it.
-- -----------------------------------------------------------------------------
create or replace function app.correct_extracted_field(
  p_field_id        uuid,
  p_decision        text,
  p_corrected_value jsonb default null
)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Reviewing a field requires an authenticated user.'
      using errcode = '28000';
  end if;

  if p_decision not in ('accepted', 'corrected', 'rejected') then
    raise exception 'A field decision must be accepted, corrected, or rejected.'
      using errcode = '22023';
  end if;

  if p_decision = 'corrected' and p_corrected_value is null then
    raise exception 'A correction must supply the corrected value.'
      using errcode = '22023';
  end if;

  select f.organization_id into v_organization_id
  from lease_extracted_fields f
  where f.id = p_field_id;

  if v_organization_id is null then
    raise exception 'No such field.' using errcode = 'P0002';
  end if;

  if not app.can_write_org(v_organization_id) then
    raise exception 'You do not have permission to review this document.'
      using errcode = '42501';
  end if;

  update lease_extracted_fields
  set review_status = p_decision::app.field_review_status,
      corrected_value = case when p_decision = 'corrected' then p_corrected_value else null end,
      corrected_by_user_id = case when p_decision = 'corrected' then v_user_id else null end,
      corrected_at = case when p_decision = 'corrected' then now() else null end
  where id = p_field_id;
end;
$$;

comment on function app.correct_extracted_field is
  'Records a reviewer decision on one field. The model answer is preserved alongside it.';

-- -----------------------------------------------------------------------------
-- app.approve_lease_from_extraction
--
-- THE CONTROL IN SECTION 13.C.
--
-- The only path from a reviewed extraction to a lease that can send reminders.
-- Four things make it the only one:
--
--   1. It requires app.current_user_id() to resolve to a real row in users.
--      There is no service-account path and no way to call it without a
--      human identity.
--   2. It refuses while any blocking issue is unresolved.
--   3. It writes lease_approvals, which is append-only even for the
--      application role, so the decision cannot later be edited away.
--   4. The leases table's own CHECK constraint
--      (leases_live_requires_approval_check) refuses any lifecycle status
--      other than draft unless approval_status is 'approved'. So even a direct
--      UPDATE could not activate an unapproved lease.
--
-- Notice what it does NOT do: it does not start reminders. Approval and
-- automation are separate state machines (section 13.E), so an approved lease
-- still has notification_automation at whatever it was. Conflating the two
-- would mean approving a lease silently began emailing a tenant.
-- -----------------------------------------------------------------------------
create or replace function app.approve_lease_from_extraction(
  p_lease_id      uuid,
  p_extraction_id uuid,
  p_decision      text,
  p_reason        text default null,
  p_approved_terms jsonb default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id          uuid;
  v_organization_id  uuid;
  v_blocking_count   integer;
  v_pending_count    integer;
  v_approval_id      uuid;
  v_previous_id      uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Approving a lease requires an authenticated user.'
      using errcode = '28000';
  end if;

  if p_decision not in ('approved', 'rejected') then
    raise exception 'A lease decision must be approved or rejected.'
      using errcode = '22023';
  end if;

  select l.organization_id into v_organization_id
  from leases l
  where l.id = p_lease_id;

  if v_organization_id is null then
    raise exception 'No such lease.' using errcode = 'P0002';
  end if;

  -- Approval is an administrative action. A lease going live commits the
  -- organization to sending mail in its name, which is not a decision for a
  -- property manager, let alone a read-only member.
  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can approve a lease.'
      using errcode = '42501';
  end if;

  if p_extraction_id is not null then
    if not exists (
      select 1 from lease_extractions e
      where e.id = p_extraction_id and e.organization_id = v_organization_id
    ) then
      raise exception 'No such extraction.' using errcode = 'P0002';
    end if;

    if p_decision = 'approved' then
      select count(*) into v_blocking_count
      from lease_extraction_issues i
      where i.extraction_id = p_extraction_id
        and i.severity = 'blocking'
        and i.resolved_at is null;

      if v_blocking_count > 0 then
        raise exception
          'This lease has % unresolved blocking issue(s) and cannot be approved yet.',
          v_blocking_count
          using errcode = '23514';
      end if;

      -- Every field must have been looked at. This is what stops "approve"
      -- becoming a single click that accepts forty unreviewed machine
      -- guesses, which is precisely the failure section 13.C describes.
      select count(*) into v_pending_count
      from lease_extracted_fields f
      where f.extraction_id = p_extraction_id
        and f.review_status in ('pending', 'flagged');

      if v_pending_count > 0 then
        raise exception
          'There are still % field(s) awaiting review. Every field must be accepted, '
          'corrected, or rejected before the lease can be approved.',
          v_pending_count
          using errcode = '23514';
      end if;
    end if;

    update lease_extractions
    set review_status = case
          when p_decision = 'approved' then 'accepted'::app.extraction_review_status
          else 'rejected'::app.extraction_review_status
        end,
        reviewed_by_user_id = v_user_id,
        reviewed_at = now()
    where id = p_extraction_id;
  end if;

  -- A re-approval after a term change points at the decision it replaces, so
  -- the history reads as a chain rather than as unrelated events.
  select a.id into v_previous_id
  from lease_approvals a
  where a.lease_id = p_lease_id
  order by a.decided_at desc
  limit 1;

  insert into lease_approvals (
    organization_id, lease_id, extraction_id, decision, decided_by_user_id,
    reason, approved_terms, supersedes_approval_id
  )
  values (
    v_organization_id, p_lease_id, p_extraction_id,
    p_decision::app.lease_approval_status, v_user_id,
    left(p_reason, 2000), p_approved_terms, v_previous_id
  )
  returning id into v_approval_id;

  -- The approval columns on leases are governed by two CHECK constraints
  -- (leases_approved_metadata_check and leases_rejected_metadata_check) that
  -- refuse any state where the status and its metadata disagree. So all three
  -- have to move together: an "approved" lease with no recorded approver
  -- would be a lease nobody can be shown to have approved, which is precisely
  -- what section 13.C exists to prevent.
  update leases
  set approval_status = p_decision::app.lease_approval_status,
      approved_at = case when p_decision = 'approved' then now() end,
      approved_by_user_id = case when p_decision = 'approved' then v_user_id end,
      rejected_at = case when p_decision = 'rejected' then now() end,
      -- Only the approval state machine moves here. Lifecycle stays where it
      -- was, and automation is untouched: approving a lease must never be the
      -- thing that starts sending email.
      updated_at = now()
  where id = p_lease_id;

  update lease_documents
  set lease_id = p_lease_id,
      updated_at = now()
  where organization_id = v_organization_id
    and lease_id is null
    and id in (
      select e.lease_document_id from lease_extractions e where e.id = p_extraction_id
    );

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    v_organization_id, v_user_id,
    case when p_decision = 'approved' then 'lease.approved' else 'lease.rejected' end,
    'lease', p_lease_id
  );

  return v_approval_id;
end;
$$;

comment on function app.approve_lease_from_extraction is
  'The only path from an extraction to an approved lease. Requires a human approver, '
  'refuses while fields are unreviewed or blocking issues are open, and never starts '
  'reminders by itself.';

-- -----------------------------------------------------------------------------
-- app.soft_delete_lease_document
-- -----------------------------------------------------------------------------
create or replace function app.soft_delete_lease_document(p_document_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Deleting a document requires an authenticated user.'
      using errcode = '28000';
  end if;

  select d.organization_id into v_organization_id
  from lease_documents d
  where d.id = p_document_id;

  if v_organization_id is null then
    raise exception 'No such document.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can delete a document.'
      using errcode = '42501';
  end if;

  -- Soft only. The object stays in storage until a retention or erasure run
  -- removes it and sets purged_at, because "the user clicked delete" and "the
  -- bytes are gone" are different facts and section 11.7 needs both.
  update lease_documents
  set deleted_at = now(), updated_at = now()
  where id = p_document_id and deleted_at is null;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'document.deleted', 'lease_document', p_document_id);
end;
$$;

-- =============================================================================
-- Row Level Security
-- =============================================================================

alter table lease_documents enable row level security;
alter table lease_extractions enable row level security;
alter table lease_extracted_fields enable row level security;
alter table lease_extraction_issues enable row level security;
alter table lease_approvals enable row level security;

alter table lease_documents force row level security;
alter table lease_extractions force row level security;
alter table lease_extracted_fields force row level security;
alter table lease_extraction_issues force row level security;
alter table lease_approvals force row level security;

do $$
declare
  v_table text;
begin
  foreach v_table in array array[
    'lease_documents', 'lease_extractions', 'lease_extracted_fields',
    'lease_extraction_issues', 'lease_approvals'
  ]
  loop
    execute format('drop policy if exists %I_member_select on %I', v_table, v_table);

    -- SELECT only. Every write goes through the functions above, which is what
    -- makes the approval control unavoidable: there is no policy permitting
    -- the application role to write these tables directly.
    --
    -- Through app.is_org_member, as every policy in 0009 does. Writing the
    -- membership lookup out by hand here is how a policy ends up comparing
    -- organization_members.user_id against the external auth identifier,
    -- which is a different value: the policy then matches nothing and
    -- everybody silently loses access to their own documents.
    execute format($f$
      create policy %I_member_select on %I
      for select
      using (app.is_org_member(organization_id))
    $f$, v_table, v_table);
  end loop;
end;
$$;

-- =============================================================================
-- Grants
-- =============================================================================

grant select on lease_documents, lease_extractions, lease_extracted_fields,
  lease_extraction_issues, lease_approvals to app_api;

-- Explicit, because ALTER DEFAULT PRIVILEGES for TABLES does apply to tables
-- created later and would otherwise have handed the API role write access to
-- every table in this migration. This was a real defect caught by the
-- verification block in 0017; the REVOKE is repeated here for the same reason.
revoke insert, update, delete, truncate
  on lease_documents, lease_extractions, lease_extracted_fields,
     lease_extraction_issues, lease_approvals
  from app_api;

grant execute on function app.create_lease_document(
  uuid, text, text, text, text, text, bigint, bytea, uuid) to app_api;
grant execute on function app.correct_extracted_field(uuid, text, jsonb) to app_api;
grant execute on function app.approve_lease_from_extraction(
  uuid, uuid, text, text, jsonb) to app_api;
grant execute on function app.soft_delete_lease_document(uuid) to app_api;

-- Worker-only. The API must never claim a document or write an extraction
-- result: those are the worker's job, and granting them here would create a
-- second path that bypasses the queue.
grant execute on function app.claim_documents_for_extraction(integer) to app_worker;
grant execute on function app.record_extraction_result(
  uuid, text, text, text, text, jsonb, jsonb, jsonb, integer) to app_worker;
grant execute on function app.fail_extraction(uuid, text, boolean) to app_worker;

-- The worker is granted NO table privileges, here or anywhere. It reaches
-- documents only through app.claim_documents_for_extraction, which is
-- SECURITY DEFINER and returns exactly the columns extraction needs. A SELECT
-- grant would have been convenient and would have broken the invariant that a
-- stolen worker credential can read nothing directly; the verification block
-- in 0010 asserts it, and caught this when the grant was first written here.

-- PUBLIC execute on a function is implicit rather than a stored grant, so
-- ALTER DEFAULT PRIVILEGES cannot revoke it. It has to be revoked explicitly,
-- every time functions are added. Omitting this would leave every function
-- above callable by any role that can reach the schema.
revoke all on all functions in schema app from public;

-- =============================================================================
-- Verification
-- =============================================================================

do $$
declare
  v_count integer;
begin
  -- No application role may write these tables directly.
  select count(*) into v_count
  from information_schema.role_table_grants
  where grantee in ('app_api', 'app_worker')
    and table_schema = 'app'
    and table_name in (
      'lease_documents', 'lease_extractions', 'lease_extracted_fields',
      'lease_extraction_issues', 'lease_approvals'
    )
    and privilege_type in ('INSERT', 'UPDATE', 'DELETE', 'TRUNCATE');

  if v_count > 0 then
    raise exception
      'An application role holds % direct write grant(s) on the document tables. '
      'Every write must go through the functions, or the approval control can be bypassed.',
      v_count;
  end if;

  -- The API must not be able to run the worker's functions.
  if has_function_privilege(
       'app_api', 'app.record_extraction_result(uuid, text, text, text, text, jsonb, jsonb, jsonb, integer)', 'execute')
  then
    raise exception 'app_api can write extraction results directly; it must not.';
  end if;

  -- Nothing in the schema may be PUBLIC-executable.
  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Document pipeline: grants and policies verified.';
end;
$$;
