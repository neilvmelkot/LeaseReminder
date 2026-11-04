-- =============================================================================
-- document_pipeline_test.sql
-- Document intelligence, and the approval control that governs it.
--
-- Run with the other suites:  ./db/test.sh local
--
-- The whole file runs in one transaction and rolls back.
--
-- The centre of this suite is section 13.C: "AI extraction alone must never
-- activate a lease or start reminders." That is a sentence in a specification
-- until something tries to violate it and is refused, so most of what follows
-- is attempts to violate it:
--
--   * approving while fields are still unreviewed
--   * approving while a blocking issue is open
--   * approving as somebody without the authority
--   * activating a lease that was never approved
--   * editing an approval decision after the fact
--   * reaching the extraction write path from the API role
--
-- Each of those would, if it succeeded, mean a machine's reading of a lease
-- could start emailing a tenant about money. None of them does.
-- =============================================================================

\set ON_ERROR_STOP on
\timing off

begin;

create temporary table t (k text primary key, v text not null);
grant select, insert on t to app_api;

create or replace function pg_temp.put(p_key text, p_value text)
returns void language sql as $fn$
  insert into t (k, v) values (p_key, p_value)
  on conflict (k) do update set v = excluded.v
$fn$;

create or replace function pg_temp.uid(p_key text)
returns uuid language sql stable as $fn$
  select (select v from t where k = p_key)::uuid
$fn$;

create or replace function pg_temp.assert(p_condition boolean, p_test text)
returns void language plpgsql as $fn$
begin
  if p_condition is not true then
    raise exception 'FAILED: %', p_test using errcode = 'assert_failure';
  end if;
  raise notice '  pass  %', p_test;
end;
$fn$;

create or replace function pg_temp.become(p_auth_user_id uuid)
returns void language plpgsql as $fn$
begin
  perform set_config('app.auth_user_id', coalesce(p_auth_user_id::text, ''), false);
end;
$fn$;

-- -----------------------------------------------------------------------------
-- Fixtures
-- -----------------------------------------------------------------------------
do $seed$
declare
  v_auth_owner uuid := gen_random_uuid();
  v_auth_member uuid := gen_random_uuid();
  v_auth_other uuid := gen_random_uuid();
  v_user_owner uuid; v_user_member uuid; v_user_other uuid;
  v_org uuid; v_org_other uuid;
  v_prop uuid; v_lease uuid;
begin
  insert into users (auth_user_id, email, display_name)
  values (v_auth_owner, 'doc-owner@example.test', 'Dana Owner')
  returning id into v_user_owner;

  insert into users (auth_user_id, email, display_name)
  values (v_auth_member, 'doc-manager@example.test', 'Milo Manager')
  returning id into v_user_member;

  insert into users (auth_user_id, email, display_name)
  values (v_auth_other, 'doc-other@example.test', 'Olive Other')
  returning id into v_user_other;

  insert into organizations (name) values ('Docs Org') returning id into v_org;
  insert into organizations (name) values ('Other Org') returning id into v_org_other;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user_owner, 'owner'),
         (v_org, v_user_member, 'property_manager'),
         (v_org_other, v_user_other, 'owner');

  insert into properties (organization_id, name, timezone)
  values (v_org, 'Document Street', 'America/Los_Angeles')
  returning id into v_prop;

  -- A draft lease, pending review. This is the state a lease is in after a
  -- document has been read but before a human has decided anything.
  insert into leases (
    organization_id, property_id, lifecycle_status, approval_status,
    automation_status, rent_amount, currency
  )
  values (v_org, v_prop, 'draft', 'pending_review', 'not_configured', 2400.00, 'USD')
  returning id into v_lease;

  perform pg_temp.put('auth_owner', v_auth_owner::text);
  perform pg_temp.put('auth_member', v_auth_member::text);
  perform pg_temp.put('auth_other', v_auth_other::text);
  perform pg_temp.put('user_owner', v_user_owner::text);
  perform pg_temp.put('org', v_org::text);
  perform pg_temp.put('org_other', v_org_other::text);
  perform pg_temp.put('prop', v_prop::text);
  perform pg_temp.put('lease', v_lease::text);
end;
$seed$;

\echo
\echo '== Uploading a document ===================================================='

do $t$
declare
  v_org uuid := pg_temp.uid('org');
  v_doc uuid;
  v_doc_id uuid := gen_random_uuid();
  v_status app.document_processing_status;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  v_doc := app.create_lease_document(
    v_org, 'lease-documents',
    'org/' || v_org::text || '/documents/' || v_doc_id::text || '.pdf',
    'Smith lease FINAL (2).pdf',
    'application/pdf', 'application/pdf',
    184320, decode(repeat('ab', 32), 'hex'),
    v_doc_id);

  perform pg_temp.put('doc', v_doc::text);

  select processing_status into v_status from lease_documents where id = v_doc;

  perform pg_temp.assert(v_status = 'queued',
    'an uploaded document is queued for extraction');

  -- The scan state machine is separate from the processing one, and the
  -- table refuses to let an unscanned document be extracted.
  perform pg_temp.assert(
    (select malware_scan_status from lease_documents where id = v_doc) = 'skipped',
    'the scan status is recorded explicitly rather than left pending');

  perform pg_temp.assert(
    (select count(*) from audit_logs
     where entity_id = v_doc and action = 'document.uploaded') = 1,
    'the upload is audited');
end;
$t$;

-- The storage path is machine-generated, and the database refuses anything
-- else. This is the constraint that makes path traversal structurally
-- impossible rather than merely unlikely.
do $t$
declare
  v_org uuid := pg_temp.uid('org');
  v_refused boolean := false;
begin
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (
      v_org, 'lease-documents',
      'org/' || v_org::text || '/documents/../../../etc/passwd.pdf',
      'passwd.pdf', 'application/pdf', 100);
  exception when check_violation then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a storage path that walks upwards is refused by the database');
end;
$t$;

do $t$
declare
  v_org uuid := pg_temp.uid('org');
  v_other uuid := pg_temp.uid('org_other');
  v_refused boolean := false;
begin
  begin
    -- A path under another organization's prefix, on a row owned by this one.
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (
      v_org, 'lease-documents',
      'org/' || v_other::text || '/documents/' || gen_random_uuid()::text || '.pdf',
      'sneaky.pdf', 'application/pdf', 100);
  exception when check_violation then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a storage path under another organization is refused');
end;
$t$;

\echo
\echo '== The worker queue ========================================================'

do $t$
declare
  v_claimed integer;
  v_second  integer;
begin
  -- Claiming is the worker's job. The API role is not granted it, which is
  -- asserted separately below.
  select count(*) into v_claimed
  from app.claim_documents_for_extraction(10);

  perform pg_temp.assert(v_claimed = 1, 'the queued document is claimed');

  -- A second claim in the same transaction finds nothing: the first one moved
  -- the row out of 'queued'. This is what stops two workers extracting the
  -- same lease and billing twice for it.
  select count(*) into v_second
  from app.claim_documents_for_extraction(10);

  perform pg_temp.assert(v_second = 0, 'a claimed document is not claimed twice');

  perform pg_temp.assert(
    (select processing_status from lease_documents where id = pg_temp.uid('doc'))
      = 'extracting',
    'claiming moves the document into extracting');
end;
$t$;

\echo
\echo '== An extraction writes atomically ========================================='

do $t$
declare
  v_extraction uuid;
  v_fields integer;
  v_issues integer;
begin
  v_extraction := app.record_extraction_result(
    pg_temp.uid('doc'),
    '1.0', 'google.generativelanguage', 'gemini-2.5-flash', '1.0',
    '{"document_kind":"residential_lease"}'::jsonb,
    jsonb_build_array(
      jsonb_build_object(
        'name', 'rent.amount', 'value_type', 'money',
        'extracted_value', '2400.00', 'normalized_value', to_jsonb(2400.00),
        'confidence', 0.96, 'provenance', 'stated_in_document',
        'source_page', 2, 'source_excerpt', 'rent of $2,400.00 per month',
        'issue_codes', '[]'::jsonb),
      jsonb_build_object(
        'name', 'term.start_date', 'value_type', 'date',
        'extracted_value', 'the 32nd of June', 'normalized_value', 'null'::jsonb,
        'confidence', 0.31, 'provenance', 'stated_in_document',
        'source_page', 1, 'source_excerpt', 'commencing the 32nd of June',
        'issue_codes', '["unparseable_value"]'::jsonb)
    ),
    jsonb_build_array(
      jsonb_build_object(
        'code', 'conflicting_clause', 'severity', 'blocking',
        'message', 'Two clauses state different rent amounts.',
        'source_pages', '[2, 7]'::jsonb)
    ),
    12);

  perform pg_temp.put('extraction', v_extraction::text);

  select count(*) into v_fields
  from lease_extracted_fields where extraction_id = v_extraction;
  select count(*) into v_issues
  from lease_extraction_issues where extraction_id = v_extraction;

  perform pg_temp.assert(v_fields = 2, 'every field is written with the run');
  perform pg_temp.assert(v_issues = 1, 'every issue is written with the run');

  perform pg_temp.assert(
    (select processing_status from lease_documents where id = pg_temp.uid('doc'))
      = 'review_ready',
    'the document becomes ready for review');

  -- A value that failed normalisation stores SQL NULL, not the JSON value
  -- null. The column's meaning depends on the difference: NULL is what the
  -- review screen reads as "a human must supply this".
  perform pg_temp.assert(
    (select normalized_value is null from lease_extracted_fields
     where extraction_id = v_extraction and field_name = 'term.start_date'),
    'a value that failed validation stores a real NULL');

  -- And the model's answer survives beside it, which is what lets a reviewer
  -- see that it read "the 32nd of June" rather than simply finding a blank.
  perform pg_temp.assert(
    (select extracted_value from lease_extracted_fields
     where extraction_id = v_extraction and field_name = 'term.start_date')
      = 'the 32nd of June',
    'the unusable answer is kept for the reviewer to see');

  perform pg_temp.assert(
    (select review_status from lease_extracted_fields
     where extraction_id = v_extraction and field_name = 'term.start_date') = 'flagged',
    'a field with issues starts flagged rather than pending');
end;
$t$;

\echo
\echo '== Section 13.C: a machine cannot approve a lease =========================='

-- The most important test in this file. Every field must have been decided
-- before approval, so "approve" cannot become one click that accepts forty
-- unreviewed machine guesses.
do $t$
declare
  v_refused boolean := false;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  begin
    perform app.approve_lease_from_extraction(
      pg_temp.uid('lease'), pg_temp.uid('extraction'), 'approved', null, null);
  exception when check_violation then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a lease cannot be approved while any field is still unreviewed');
end;
$t$;

-- Review the fields, leaving the blocking issue open.
do $t$
declare
  v_field uuid;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  for v_field in
    select id from lease_extracted_fields where extraction_id = pg_temp.uid('extraction')
  loop
    perform app.correct_extracted_field(v_field, 'accepted', null);
  end loop;

  perform pg_temp.assert(
    (select count(*) from lease_extracted_fields
     where extraction_id = pg_temp.uid('extraction')
       and review_status in ('pending', 'flagged')) = 0,
    'every field has been reviewed');
end;
$t$;

do $t$
declare
  v_refused boolean := false;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  begin
    perform app.approve_lease_from_extraction(
      pg_temp.uid('lease'), pg_temp.uid('extraction'), 'approved', null, null);
  exception when check_violation then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a lease cannot be approved while a blocking issue is unresolved');
end;
$t$;

-- Resolve the issue, then try as somebody without the authority.
do $t$
declare
  v_refused boolean := false;
begin
  update lease_extraction_issues
  set resolved_at = now(), resolved_by_user_id = pg_temp.uid('user_owner')
  where extraction_id = pg_temp.uid('extraction');

  perform pg_temp.become(pg_temp.uid('auth_member'));

  begin
    perform app.approve_lease_from_extraction(
      pg_temp.uid('lease'), pg_temp.uid('extraction'), 'approved', null, null);
  exception when insufficient_privilege then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a property manager can review fields but cannot approve the lease');
end;
$t$;

do $t$
declare
  v_refused boolean := false;
begin
  -- No identity at all. There is no service-account path to approval.
  perform pg_temp.become(null);

  begin
    perform app.approve_lease_from_extraction(
      pg_temp.uid('lease'), pg_temp.uid('extraction'), 'approved', null, null);
  exception when invalid_authorization_specification then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'approval is impossible without an authenticated human');
end;
$t$;

\echo
\echo '== Approval, and what it deliberately does not do =========================='

do $t$
declare
  v_approval uuid;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  v_approval := app.approve_lease_from_extraction(
    pg_temp.uid('lease'), pg_temp.uid('extraction'), 'approved',
    'Checked against the signed copy.', '{"rent.amount": 2400.00}'::jsonb);

  perform pg_temp.put('approval', v_approval::text);

  perform pg_temp.assert(
    (select approval_status from leases where id = pg_temp.uid('lease')) = 'approved',
    'an owner can approve once every field is reviewed and no issue blocks it');

  -- The separation that matters most. Approving a lease must not be the thing
  -- that starts emailing a tenant: those are two state machines (section
  -- 13.E), and a user who approves a lease has not thereby asked for
  -- reminders to begin.
  perform pg_temp.assert(
    (select automation_status from leases where id = pg_temp.uid('lease'))
      = 'not_configured',
    'approving a lease does not start reminders');

  perform pg_temp.assert(
    (select lifecycle_status from leases where id = pg_temp.uid('lease')) = 'draft',
    'approving a lease does not by itself make it active');

  perform pg_temp.assert(
    (select decided_by_user_id from lease_approvals where id = v_approval)
      = pg_temp.uid('user_owner'),
    'the approval records which human made it');

  perform pg_temp.assert(
    (select review_status from lease_extractions where id = pg_temp.uid('extraction'))
      = 'accepted',
    'the extraction is marked reviewed');

  -- The document is linked to the lease it turned out to describe.
  perform pg_temp.assert(
    (select lease_id from lease_documents where id = pg_temp.uid('doc'))
      = pg_temp.uid('lease'),
    'the document is attached to the approved lease');
end;
$t$;

-- The approval history cannot be rewritten by an application role. Without
-- this, "a human approved it" would be a claim rather than a record.
--
-- Note which actor this runs as. app.deny_mutation refuses UPDATE for
-- everybody, including the table owner, because history is never amended. It
-- permits DELETE for the owner alone, so that a reviewed retention or erasure
-- procedure can run (section 11.7). The guarantee being asserted here is
-- therefore about app_api, which is what a compromised application process
-- would hold -- not about postgres, which is what runs this file by default.
set role app_api;

do $t$
declare
  v_update_refused boolean := false;
  v_delete_refused boolean := false;
begin
  begin
    update lease_approvals set reason = 'something else'
    where id = pg_temp.uid('approval');
  exception when others then
    v_update_refused := true;
  end;

  begin
    delete from lease_approvals where id = pg_temp.uid('approval');
  exception when others then
    v_delete_refused := true;
  end;

  perform pg_temp.assert(v_update_refused,
    'the application role cannot edit an approval decision');
  perform pg_temp.assert(v_delete_refused,
    'the application role cannot delete an approval decision');
end;
$t$;

reset role;

-- And the owner, who may delete for erasure, still cannot amend. An edited
-- approval would be indistinguishable from an honest one.
do $t$
declare
  v_update_refused boolean := false;
begin
  begin
    update lease_approvals set reason = 'rewritten after the fact'
    where id = pg_temp.uid('approval');
  exception when others then
    v_update_refused := true;
  end;

  perform pg_temp.assert(v_update_refused,
    'not even the table owner can amend an approval decision');
end;
$t$;

-- Re-approval chains to the decision it replaces, so the history reads as a
-- sequence rather than as unrelated events.
do $t$
declare
  v_second uuid;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  v_second := app.approve_lease_from_extraction(
    pg_temp.uid('lease'), null, 'approved', 'Rent corrected after a term change.', null);

  perform pg_temp.assert(
    (select supersedes_approval_id from lease_approvals where id = v_second)
      = pg_temp.uid('approval'),
    're-approval points at the decision it supersedes');
end;
$t$;

\echo
\echo '== A lease still cannot go live without approval ==========================='

do $t$
declare
  v_org uuid := pg_temp.uid('org');
  v_prop uuid := pg_temp.uid('prop');
  v_unapproved uuid;
  v_refused boolean := false;
begin
  insert into leases (
    organization_id, property_id, lifecycle_status, approval_status, automation_status
  )
  values (v_org, v_prop, 'draft', 'pending_review', 'not_configured')
  returning id into v_unapproved;

  begin
    update leases set lifecycle_status = 'active' where id = v_unapproved;
  exception when check_violation then
    v_refused := true;
  end;

  -- Belt and braces on top of the function: even a direct UPDATE that
  -- bypassed every function above cannot activate an unapproved lease,
  -- because the table itself refuses.
  perform pg_temp.assert(v_refused,
    'the leases table refuses to activate a lease that was never approved');
end;
$t$;

\echo
\echo '== Isolation and least privilege ==========================================='

do $t$
begin
  -- The API role must not be able to write extraction results: that is the
  -- worker queue's job, and a second path would mean a document could be
  -- "extracted" without ever having been read.
  perform pg_temp.assert(
    not has_function_privilege('app_api',
      'app.record_extraction_result(uuid, text, text, text, text, jsonb, jsonb, jsonb, integer)',
      'execute'),
    'the API role cannot write extraction results');

  perform pg_temp.assert(
    not has_function_privilege('app_api',
      'app.claim_documents_for_extraction(integer)', 'execute'),
    'the API role cannot claim documents');

  perform pg_temp.assert(
    not has_function_privilege('app_worker',
      'app.approve_lease_from_extraction(uuid, uuid, text, text, jsonb)', 'execute'),
    'the worker cannot approve a lease');

  -- No application role may write these tables directly. Every write goes
  -- through a function, which is what makes the approval control the only
  -- path rather than merely the intended one.
  perform pg_temp.assert(
    (select count(*) from information_schema.role_table_grants
     where grantee in ('app_api', 'app_worker')
       and table_name in (
         'lease_documents', 'lease_extractions', 'lease_extracted_fields',
         'lease_extraction_issues', 'lease_approvals')
       and privilege_type in ('INSERT', 'UPDATE', 'DELETE')) = 0,
    'no application role can write the document tables directly');
end;
$t$;

do $t$
declare
  v_visible integer;
begin
  -- Another organization's owner sees none of this.
  perform pg_temp.become(pg_temp.uid('auth_other'));

  set local role app_api;

  select count(*) into v_visible from lease_documents;
  perform pg_temp.assert(v_visible = 0,
    'another organization sees no documents');

  select count(*) into v_visible from lease_extracted_fields;
  perform pg_temp.assert(v_visible = 0,
    'another organization sees no extracted lease terms');

  select count(*) into v_visible from lease_approvals;
  perform pg_temp.assert(v_visible = 0,
    'another organization sees no approval history');

  reset role;
end;
$t$;

do $t$
declare
  v_refused boolean := false;
begin
  -- A member of another organization cannot review a field in this one, even
  -- knowing its id.
  perform pg_temp.become(pg_temp.uid('auth_other'));

  begin
    perform app.correct_extracted_field(
      (select id from lease_extracted_fields
       where extraction_id = pg_temp.uid('extraction') limit 1),
      'accepted', null);
  exception when insufficient_privilege then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a field cannot be reviewed from another organization');
end;
$t$;

\echo
\echo '== A correction preserves what the model said =============================='

do $t$
declare
  v_field uuid;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  select id into v_field from lease_extracted_fields
  where extraction_id = pg_temp.uid('extraction') and field_name = 'term.start_date';

  perform app.correct_extracted_field(v_field, 'corrected', '"2026-06-01"'::jsonb);

  perform pg_temp.assert(
    (select extracted_value from lease_extracted_fields where id = v_field)
      = 'the 32nd of June',
    'correcting a field does not erase what the model answered');

  perform pg_temp.assert(
    (select corrected_value from lease_extracted_fields where id = v_field)
      = '"2026-06-01"'::jsonb,
    'the correction is stored beside it');

  perform pg_temp.assert(
    (select corrected_by_user_id from lease_extracted_fields where id = v_field)
      = pg_temp.uid('user_owner'),
    'the correction records who made it');
end;
$t$;

do $t$
declare
  v_refused boolean := false;
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  begin
    -- "corrected" without a value would blank the field while claiming a
    -- human had decided it.
    perform app.correct_extracted_field(
      (select id from lease_extracted_fields
       where extraction_id = pg_temp.uid('extraction') limit 1),
      'corrected', null);
  exception when others then
    v_refused := true;
  end;

  perform pg_temp.assert(v_refused,
    'a correction must supply the corrected value');
end;
$t$;

\echo
\echo '== Failures stay reviewable ================================================'

do $t$
declare
  v_org uuid := pg_temp.uid('org');
  v_doc uuid;
  v_doc_id uuid := gen_random_uuid();
begin
  perform pg_temp.become(pg_temp.uid('auth_owner'));

  v_doc := app.create_lease_document(
    v_org, 'lease-documents',
    'org/' || v_org::text || '/documents/' || v_doc_id::text || '.pdf',
    'unreadable.pdf', 'application/pdf', 'application/pdf', 1024, null, v_doc_id);

  -- A permanent failure stops and waits for a person.
  perform app.fail_extraction(v_doc, 'The document could not be read.', false);

  perform pg_temp.assert(
    (select processing_status from lease_documents where id = v_doc) = 'failed',
    'a permanent failure leaves the document visible and failed');

  perform pg_temp.assert(
    (select processing_error from lease_documents where id = v_doc) is not null,
    'the failure records an operator-safe reason');

  -- A retryable one goes back on the queue, so a rate limit does not
  -- permanently fail every document it touches.
  perform app.fail_extraction(v_doc, 'Rate limited.', true);

  perform pg_temp.assert(
    (select processing_status from lease_documents where id = v_doc) = 'queued',
    'a retryable failure returns the document to the queue');
end;
$t$;

\echo
\echo '== All document pipeline tests passed ======================================'

rollback;
