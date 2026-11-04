-- =============================================================================
-- rls_isolation_test.sql
-- Executable versions of the security acceptance tests in section 4.
--
-- Run against a freshly migrated database:
--     ./db/test.sh local
--
-- The whole file runs in one transaction and rolls back, so it leaves no data
-- behind. It asserts by raising, so any failure aborts with a message naming
-- the test.
--
-- Structure: data is created as the table owner (which is what the migration
-- role can do), then each test SETs ROLE app_api and an identity, exactly as a
-- request would arrive, and checks what that identity can actually reach.
--
-- Covered, by number from section 4:
--   1  User A cannot read User B's lease through the API
--   2  User A cannot download User B's private document (path/row isolation)
--   3  A modified request cannot change another user's property
--   5  OAuth tokens are unreachable by the API role
--   6  Duplicate worker execution cannot send duplicate reminders
--   8  Invalid file types and oversized uploads are rejected
--  10  A disconnected Gmail account cannot continue sending
-- plus the approval gate from section 13.C and the role tiers from 4.B.
-- =============================================================================

\set ON_ERROR_STOP on
\timing off

begin;

-- -----------------------------------------------------------------------------
-- Fixtures
-- -----------------------------------------------------------------------------
create temporary table t_ids (k text primary key, v uuid not null);

-- The fixture table is created by the migration role, but the tests read it
-- while impersonating app_api, so it needs an explicit grant. It holds only
-- identifiers, and it disappears when the session ends.
grant select, insert on t_ids to app_api;

do $seed$
declare
  v_auth_a uuid := gen_random_uuid();
  v_auth_b uuid := gen_random_uuid();
  v_auth_r uuid := gen_random_uuid();   -- a read-only member of organization A
  v_user_a uuid;
  v_user_b uuid;
  v_user_r uuid;
  v_org_a  uuid;
  v_org_b  uuid;
  v_prop_a uuid;
  v_prop_b uuid;
  v_unit_a uuid;
  v_lease_a uuid;
  v_lease_b uuid;
  v_acct_a uuid;
begin
  insert into users (auth_user_id, email, display_name)
  values (v_auth_a, 'owner-a@example.test', 'Owner A') returning id into v_user_a;
  insert into users (auth_user_id, email, display_name)
  values (v_auth_b, 'owner-b@example.test', 'Owner B') returning id into v_user_b;
  insert into users (auth_user_id, email, display_name)
  values (v_auth_r, 'readonly-a@example.test', 'Reader A') returning id into v_user_r;

  insert into organizations (name) values ('Org A') returning id into v_org_a;
  insert into organizations (name) values ('Org B') returning id into v_org_b;

  insert into organization_members (organization_id, user_id, role)
  values (v_org_a, v_user_a, 'owner'),
         (v_org_a, v_user_r, 'read_only'),
         (v_org_b, v_user_b, 'owner');

  insert into properties (organization_id, name, timezone, address_line1, locality)
  values (v_org_a, 'A Street', 'America/New_York', '1 A Street', 'Boston')
  returning id into v_prop_a;

  insert into properties (organization_id, name, timezone)
  values (v_org_b, 'B Avenue', 'America/Los_Angeles')
  returning id into v_prop_b;

  insert into units (organization_id, property_id, unit_number, status)
  values (v_org_a, v_prop_a, '1', 'occupied') returning id into v_unit_a;

  -- An approved, active lease in each organization.
  insert into leases (
    organization_id, property_id, unit_id, reference,
    lifecycle_status, approval_status, automation_status,
    start_date, end_date, rent_amount, currency,
    approved_by_user_id, approved_at
  )
  values (
    v_org_a, v_prop_a, v_unit_a, 'A lease',
    'active', 'approved', 'active',
    date '2026-01-01', date '2026-12-31', 2400.00, 'USD',
    v_user_a, now()
  ) returning id into v_lease_a;

  insert into leases (
    organization_id, property_id, reference,
    lifecycle_status, approval_status, automation_status,
    start_date, end_date, rent_amount, currency,
    approved_by_user_id, approved_at
  )
  values (
    v_org_b, v_prop_b, 'B lease',
    'active', 'approved', 'active',
    date '2026-01-01', date '2026-12-31', 3100.00, 'USD',
    v_user_b, now()
  ) returning id into v_lease_b;

  -- A connected Gmail sender for organization A, with a plausible ciphertext.
  insert into email_accounts (
    organization_id, email_address, display_name,
    encrypted_refresh_token, token_nonce, token_auth_tag, token_key_version,
    connection_status, connected_at, granted_scopes
  )
  values (
    v_org_a, 'sender-a@example.test', 'Org A Sender',
    decode('deadbeefdeadbeefdeadbeef', 'hex'),
    decode('000102030405060708090a0b', 'hex'),
    decode('0102030405060708090a0b0c0d0e0f10', 'hex'),
    1, 'connected', now(),
    array['https://www.googleapis.com/auth/gmail.send']
  ) returning id into v_acct_a;

  -- A private document for organization A.
  insert into lease_documents (
    organization_id, lease_id, storage_bucket, storage_path, original_filename,
    declared_mime_type, detected_mime_type, file_size_bytes,
    processing_status, malware_scan_status
  )
  values (
    v_org_a, v_lease_a, 'lease-documents',
    'org/' || v_org_a::text || '/documents/' || gen_random_uuid()::text || '.pdf',
    'A lease signed.pdf',
    'application/pdf', 'application/pdf', 182344,
    'review_ready', 'clean'
  );

  insert into t_ids (k, v) values
    ('auth_a', v_auth_a), ('auth_b', v_auth_b), ('auth_r', v_auth_r),
    ('user_a', v_user_a), ('user_b', v_user_b), ('user_r', v_user_r),
    ('org_a', v_org_a),   ('org_b', v_org_b),
    ('prop_a', v_prop_a), ('prop_b', v_prop_b),
    ('unit_a', v_unit_a),
    ('lease_a', v_lease_a), ('lease_b', v_lease_b),
    ('acct_a', v_acct_a);
end;
$seed$;

-- A tiny assertion helper, so each test reads as one line of intent.
create or replace function pg_temp.assert(p_condition boolean, p_test text)
returns void language plpgsql as $fn$
begin
  if p_condition is not true then
    raise exception 'FAILED: %', p_test using errcode = 'assert_failure';
  end if;
  raise notice '  pass  %', p_test;
end;
$fn$;

-- Impersonates a request: become the API role and adopt a verified subject.
create or replace function pg_temp.become(p_auth_user_id uuid)
returns void language plpgsql as $fn$
begin
  perform set_config('app.auth_user_id', coalesce(p_auth_user_id::text, ''), false);
end;
$fn$;

\echo
\echo '== Test 1: User A cannot read User B lease through the API =================='

set role app_api;

do $t$
declare
  v_org_a   uuid := (select v from t_ids where k = 'org_a');
  v_lease_b uuid := (select v from t_ids where k = 'lease_b');
  v_count   integer;
begin
  perform pg_temp.become((select v from t_ids where k = 'auth_a'));

  select count(*) into v_count from leases;
  perform pg_temp.assert(v_count = 1, 'A sees exactly one lease (its own)');

  select count(*) into v_count from leases where id = v_lease_b;
  perform pg_temp.assert(v_count = 0, 'A cannot select B lease even by its exact id');

  select count(*) into v_count from leases where organization_id <> v_org_a;
  perform pg_temp.assert(v_count = 0, 'A cannot select any lease outside its organization');

  -- The same must hold for every table that hangs off a lease.
  select count(*) into v_count from properties where organization_id <> v_org_a;
  perform pg_temp.assert(v_count = 0, 'A cannot select another organization properties');

  select count(*) into v_count from organizations;
  perform pg_temp.assert(v_count = 1, 'A sees only its own organization');
end;
$t$;

\echo
\echo '== Test 2: User A cannot reach User B private document ====================='

do $t$
declare
  v_count integer;
  v_org_b uuid := (select v from t_ids where k = 'org_b');
begin
  perform pg_temp.become((select v from t_ids where k = 'auth_b'));

  -- B owns no documents, and A's document must not leak into B's view. The
  -- storage path is the download key, so a row A cannot see is a file A
  -- cannot request a signed URL for.
  select count(*) into v_count from lease_documents;
  perform pg_temp.assert(v_count = 0, 'B sees no documents at all');

  select count(*) into v_count from lease_document_pages;
  perform pg_temp.assert(v_count = 0, 'B sees no extracted page text');
end;
$t$;

\echo
\echo '== Test 3: A modified request cannot change another user property ==========='

do $t$
declare
  v_prop_a  uuid := (select v from t_ids where k = 'prop_a');
  v_org_b   uuid := (select v from t_ids where k = 'org_b');
  v_updated integer;
  v_deleted integer;
begin
  -- B forges A's property id, exactly as a tampered browser request would.
  perform pg_temp.become((select v from t_ids where k = 'auth_b'));

  update properties set name = 'Hijacked' where id = v_prop_a;
  get diagnostics v_updated = row_count;
  perform pg_temp.assert(v_updated = 0, 'B cannot update A property by forged id');

  delete from properties where id = v_prop_a;
  get diagnostics v_deleted = row_count;
  perform pg_temp.assert(v_deleted = 0, 'B cannot delete A property by forged id');

  -- And B cannot create a row inside A's organization.
  begin
    insert into properties (organization_id, name, timezone)
    values ((select v from t_ids where k = 'org_a'), 'Smuggled', 'UTC');
    perform pg_temp.assert(false, 'B must not insert into A organization');
  exception when insufficient_privilege or check_violation then
    perform pg_temp.assert(true, 'B cannot insert into A organization');
  end;
end;
$t$;

do $t$
declare
  v_org_a   uuid := (select v from t_ids where k = 'org_a');
  v_prop_a  uuid := (select v from t_ids where k = 'prop_a');
  v_updated integer;
begin
  -- The subtler attack: A legitimately owns the row, and tries to move it
  -- into B's organization. The WITH CHECK half of the UPDATE policy is what
  -- stops this; a policy with only USING would let it through.
  perform pg_temp.become((select v from t_ids where k = 'auth_a'));

  begin
    update properties
    set organization_id = (select v from t_ids where k = 'org_b')
    where id = v_prop_a;
    get diagnostics v_updated = row_count;
    perform pg_temp.assert(v_updated = 0, 'A cannot move its property into B organization');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'A cannot move its property into B organization');
  end;
end;
$t$;

\echo
\echo '== Test 3b: role tiers (read-only member cannot write) ====================='

do $t$
declare
  v_prop_a  uuid := (select v from t_ids where k = 'prop_a');
  v_count   integer;
  v_updated integer;
begin
  perform pg_temp.become((select v from t_ids where k = 'auth_r'));

  select count(*) into v_count from properties;
  perform pg_temp.assert(v_count = 1, 'read-only member can read the portfolio');

  update properties set name = 'Renamed by reader' where id = v_prop_a;
  get diagnostics v_updated = row_count;
  perform pg_temp.assert(v_updated = 0, 'read-only member cannot update a property');

  begin
    insert into properties (organization_id, name, timezone)
    values ((select v from t_ids where k = 'org_a'), 'Reader property', 'UTC');
    perform pg_temp.assert(false, 'read-only member must not insert a property');
  exception when insufficient_privilege or check_violation then
    perform pg_temp.assert(true, 'read-only member cannot insert a property');
  end;

  -- Reading the audit trail is an administrator action.
  select count(*) into v_count from audit_logs;
  perform pg_temp.assert(v_count = 0, 'read-only member cannot read audit logs');
end;
$t$;

\echo
\echo '== Test 4/5: the API role cannot reach OAuth token material ================'

do $t$
declare
  v_acct_a uuid := (select v from t_ids where k = 'acct_a');
  v_status text;
begin
  perform pg_temp.become((select v from t_ids where k = 'auth_a'));

  -- The non-secret columns are readable, so the UI can show connection state.
  select connection_status into v_status
  from email_accounts where id = v_acct_a;
  perform pg_temp.assert(v_status = 'connected', 'API can read Gmail connection status');

  -- The ciphertext is not, even for the organization owner.
  begin
    perform encrypted_refresh_token from email_accounts where id = v_acct_a;
    perform pg_temp.assert(false, 'API must not read encrypted_refresh_token');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'API cannot read encrypted_refresh_token');
  end;

  begin
    perform token_key_version from email_accounts where id = v_acct_a;
    perform pg_temp.assert(false, 'API must not read token_key_version');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'API cannot read token_key_version');
  end;

  begin
    update email_accounts set token_nonce = decode('ffffffffffffffffffffffff', 'hex')
    where id = v_acct_a;
    perform pg_temp.assert(false, 'API must not write token_nonce');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'API cannot write token_nonce');
  end;

  -- And it has no way to call the worker's decryption door.
  begin
    perform * from app.read_email_account_token(v_acct_a);
    perform pg_temp.assert(false, 'API must not execute app.read_email_account_token');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'API cannot execute app.read_email_account_token');
  end;
end;
$t$;

\echo
\echo '== Test 6: duplicate execution cannot produce duplicate reminders =========='

reset role;

do $t$
declare
  v_org_a   uuid := (select v from t_ids where k = 'org_a');
  v_lease_a uuid := (select v from t_ids where k = 'lease_a');
  v_acct_a  uuid := (select v from t_ids where k = 'acct_a');
  v_rule    uuid;
  v_tpl     uuid;
  v_key     text := 'rent_due:2026-03-01:rule:fixed-for-test';
  v_inserted integer;
begin
  select id into v_tpl from email_templates
  where organization_id = v_org_a and template_key = 'rent_due_today'
  limit 1;

  -- Organization A has no templates yet (defaults are seeded by the API), so
  -- create the minimum needed to schedule something.
  if v_tpl is null then
    insert into email_templates (
      organization_id, template_key, name, version, status,
      subject, html_body, text_body, published_at
    )
    values (v_org_a, 'rent_due_today', 'Rent due today', 1, 'published',
            'Rent due', '<p>Rent due</p>', 'Rent due', now())
    returning id into v_tpl;
  end if;

  insert into notification_rules (
    organization_id, lease_id, event_type, trigger_type,
    offset_value, offset_unit, send_local_time, timezone,
    email_account_id, template_id, enabled
  )
  values (v_org_a, v_lease_a, 'rent_due', 'on', 0, 'days',
          time '09:00', 'America/New_York', v_acct_a, v_tpl, true)
  returning id into v_rule;

  -- First generation run.
  insert into scheduled_notifications (
    organization_id, notification_rule_id, lease_id, event_type,
    event_local_date, scheduled_at_utc, local_timezone, local_send_time,
    idempotency_key, email_account_id, template_id, recipients_snapshot
  )
  values (v_org_a, v_rule, v_lease_a, 'rent_due',
          date '2026-03-01', timestamptz '2026-03-01 14:00:00+00',
          'America/New_York', time '09:00',
          v_key, v_acct_a, v_tpl,
          '[{"email":"tenant@example.test","kind":"to"}]'::jsonb);

  -- A second run, as happens after a restart or a double-triggered
  -- regeneration. ON CONFLICT DO NOTHING is how the generator is written, so
  -- the replay is a no-op rather than a second email.
  insert into scheduled_notifications (
    organization_id, notification_rule_id, lease_id, event_type,
    event_local_date, scheduled_at_utc, local_timezone, local_send_time,
    idempotency_key, email_account_id, template_id, recipients_snapshot
  )
  values (v_org_a, v_rule, v_lease_a, 'rent_due',
          date '2026-03-01', timestamptz '2026-03-01 14:00:00+00',
          'America/New_York', time '09:00',
          v_key, v_acct_a, v_tpl,
          '[{"email":"tenant@example.test","kind":"to"}]'::jsonb)
  on conflict (organization_id, idempotency_key) do nothing;

  get diagnostics v_inserted = row_count;
  perform pg_temp.assert(v_inserted = 0, 'replayed generation inserts nothing');

  perform pg_temp.assert(
    (select count(*) from scheduled_notifications where idempotency_key = v_key) = 1,
    'exactly one scheduled notification exists for the idempotency key'
  );

  -- And a generator that does not use ON CONFLICT gets a hard error rather
  -- than a duplicate row.
  begin
    insert into scheduled_notifications (
      organization_id, notification_rule_id, lease_id, event_type,
      event_local_date, scheduled_at_utc, local_timezone, local_send_time,
      idempotency_key, email_account_id, template_id
    )
    values (v_org_a, v_rule, v_lease_a, 'rent_due',
            date '2026-03-01', timestamptz '2026-03-01 14:00:00+00',
            'America/New_York', time '09:00',
            v_key, v_acct_a, v_tpl);
    perform pg_temp.assert(false, 'a duplicate idempotency key must be rejected');
  exception when unique_violation then
    perform pg_temp.assert(true, 'a duplicate idempotency key is rejected');
  end;

  insert into t_ids (k, v) values ('rule_a', v_rule), ('tpl_a', v_tpl);
end;
$t$;

\echo
\echo '== Test 6b: two concurrent workers claim disjoint work ====================='

do $t$
declare
  v_first  integer;
  v_second integer;
begin
  -- The notification above is due in the past relative to nothing -- make it
  -- due now so it is claimable.
  update scheduled_notifications
  set scheduled_at_utc = now() - interval '1 minute'
  where idempotency_key = 'rent_due:2026-03-01:rule:fixed-for-test';

  select count(*) into v_first
  from app.claim_due_notifications('worker-one', 20, 120);
  perform pg_temp.assert(v_first = 1, 'worker one claims the due notification');

  -- The second worker, running the identical query at the same moment, must
  -- come away with nothing: the row is already claimed and its lock is live.
  select count(*) into v_second
  from app.claim_due_notifications('worker-two', 20, 120);
  perform pg_temp.assert(v_second = 0, 'worker two claims nothing');

  -- Only the claim holder can read the send context or record the result.
  perform pg_temp.assert(
    app.get_notification_context(
      (select id from scheduled_notifications
       where idempotency_key = 'rent_due:2026-03-01:rule:fixed-for-test'),
      'worker-two'
    ) is null,
    'a non-holder cannot read the notification context'
  );

  perform pg_temp.assert(
    app.record_notification_result(
      (select id from scheduled_notifications
       where idempotency_key = 'rent_due:2026-03-01:rule:fixed-for-test'),
      'worker-two', true, 'forged-message-id'
    ) is null,
    'a non-holder cannot record a send result'
  );

  perform pg_temp.assert(
    (select count(*) from email_logs) = 0,
    'no email log was written by the non-holder'
  );
end;
$t$;

\echo
\echo '== Test 10: a disconnected Gmail account cannot continue sending ==========='

do $t$
declare
  v_acct_a uuid := (select v from t_ids where k = 'acct_a');
  v_id     uuid := (select id from scheduled_notifications
                    where idempotency_key = 'rent_due:2026-03-01:rule:fixed-for-test');
  v_claimed integer;
begin
  -- Release the claim from the previous test.
  update scheduled_notifications
  set status = 'scheduled', locked_at = null, locked_by = null,
      lock_expires_at = null, attempt_count = 0
  where id = v_id;

  -- Disconnect the sender, as the user clicking Disconnect would.
  update email_accounts
  set connection_status = 'revoked',
      encrypted_refresh_token = null, token_nonce = null, token_auth_tag = null
  where id = v_acct_a;

  select count(*) into v_claimed
  from app.claim_due_notifications('worker-one', 20, 120);
  perform pg_temp.assert(v_claimed = 0,
    'nothing is claimable while the sender is revoked');

  perform pg_temp.assert(
    (select count(*) from app.read_email_account_token(v_acct_a)) = 0,
    'no token is returned for a revoked account'
  );

  -- Reconnect, and the same notification becomes claimable again. This shows
  -- the gate is the live account state, not a one-way flag on the queue row.
  update email_accounts
  set connection_status = 'connected',
      encrypted_refresh_token = decode('deadbeef', 'hex'),
      token_nonce = decode('000102030405060708090a0b', 'hex'),
      token_auth_tag = decode('0102030405060708090a0b0c0d0e0f10', 'hex'),
      token_key_version = 1
  where id = v_acct_a;

  select count(*) into v_claimed
  from app.claim_due_notifications('worker-one', 20, 120);
  perform pg_temp.assert(v_claimed = 1, 'reconnecting makes the send claimable again');
end;
$t$;

\echo
\echo '== Approval gate (section 13.C): nothing sends for an unapproved lease ====='

do $t$
declare
  v_lease_a uuid := (select v from t_ids where k = 'lease_a');
  v_id      uuid := (select id from scheduled_notifications
                     where idempotency_key = 'rent_due:2026-03-01:rule:fixed-for-test');
  v_claimed integer;
begin
  update scheduled_notifications
  set status = 'scheduled', locked_at = null, locked_by = null,
      lock_expires_at = null, attempt_count = 0
  where id = v_id;

  -- Pausing automation without touching approval or lifecycle.
  update leases
  set automation_status = 'paused', automation_paused_at = now()
  where id = v_lease_a;

  select count(*) into v_claimed
  from app.claim_due_notifications('worker-one', 20, 120);
  perform pg_temp.assert(v_claimed = 0, 'a paused lease sends nothing');

  perform pg_temp.assert(
    (select approval_status from leases where id = v_lease_a) = 'approved',
    'pausing automation left the approval status untouched'
  );
  perform pg_temp.assert(
    (select lifecycle_status from leases where id = v_lease_a) = 'active',
    'pausing automation left the lifecycle status untouched'
  );

  update leases
  set automation_status = 'active', automation_paused_at = null
  where id = v_lease_a;

  -- An unapproved lease cannot even be put into a live lifecycle state.
  begin
    update leases
    set approval_status = 'pending_review',
        approved_at = null, approved_by_user_id = null
    where id = v_lease_a;
    perform pg_temp.assert(false,
      'an active lease must not be returnable to pending_review');
  exception when check_violation then
    perform pg_temp.assert(true,
      'an active lease cannot be returned to pending_review');
  end;
end;
$t$;

do $t$
declare
  v_org_a  uuid := (select v from t_ids where k = 'org_a');
  v_prop_a uuid := (select v from t_ids where k = 'prop_a');
begin
  -- The constraint stated positively: a pending lease cannot be made active.
  begin
    insert into leases (
      organization_id, property_id, lifecycle_status, approval_status,
      start_date, rent_amount
    )
    values (v_org_a, v_prop_a, 'active', 'pending_review',
            date '2026-01-01', 1000.00);
    perform pg_temp.assert(false, 'a pending lease must not be insertable as active');
  exception when check_violation then
    perform pg_temp.assert(true, 'a pending lease cannot be active');
  end;

  -- And approval metadata cannot be faked without an approver.
  begin
    insert into leases (
      organization_id, property_id, lifecycle_status, approval_status,
      start_date, rent_amount, approved_at
    )
    values (v_org_a, v_prop_a, 'active', 'approved',
            date '2026-01-01', 1000.00, now());
    perform pg_temp.assert(false, 'approval without an approver must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'approval requires a named approver');
  end;
end;
$t$;

\echo
\echo '== Test 8: invalid uploads are rejected ===================================='

do $t$
declare
  v_org_a   uuid := (select v from t_ids where k = 'org_a');
  v_doc_uid text := gen_random_uuid()::text;
begin
  -- A user-supplied filename as the storage path.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (v_org_a, 'lease-documents', '../../etc/passwd', 'passwd',
            'application/pdf', 100);
    perform pg_temp.assert(false, 'a traversal path must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'a traversal storage path is rejected');
  end;

  -- A path under another organization's prefix.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (v_org_a, 'lease-documents',
            'org/' || (select v from t_ids where k = 'org_b')::text
              || '/documents/' || v_doc_uid || '.pdf',
            'cross.pdf', 'application/pdf', 100);
    perform pg_temp.assert(false, 'a cross-organization path must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'a cross-organization storage path is rejected');
  end;

  -- An unsupported format.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (v_org_a, 'lease-documents',
            'org/' || v_org_a::text || '/documents/' || v_doc_uid || '.pdf',
            'payload.exe', 'application/x-msdownload', 100);
    perform pg_temp.assert(false, 'an unsupported mime type must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'an unsupported mime type is rejected');
  end;

  -- Oversized.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes
    )
    values (v_org_a, 'lease-documents',
            'org/' || v_org_a::text || '/documents/' || v_doc_uid || '.pdf',
            'huge.pdf', 'application/pdf', 52428801);
    perform pg_temp.assert(false, 'an oversized upload must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'an oversized upload is rejected');
  end;

  -- An infected file must not be allowed to sit in a processable state.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes,
      processing_status, malware_scan_status
    )
    values (v_org_a, 'lease-documents',
            'org/' || v_org_a::text || '/documents/' || v_doc_uid || '.pdf',
            'infected.pdf', 'application/pdf', 1000,
            'queued', 'infected');
    perform pg_temp.assert(false, 'an infected file must be quarantined');
  exception when check_violation then
    perform pg_temp.assert(true, 'an infected file cannot enter the queue');
  end;

  -- Extraction cannot start before a scan has cleared.
  begin
    insert into lease_documents (
      organization_id, storage_bucket, storage_path, original_filename,
      declared_mime_type, file_size_bytes,
      processing_status, malware_scan_status
    )
    values (v_org_a, 'lease-documents',
            'org/' || v_org_a::text || '/documents/' || v_doc_uid || '.pdf',
            'unscanned.pdf', 'application/pdf', 1000,
            'extracting', 'pending');
    perform pg_temp.assert(false, 'extraction must not start before scanning');
  exception when check_violation then
    perform pg_temp.assert(true, 'extraction cannot start before scanning completes');
  end;
end;
$t$;

\echo
\echo '== Append-only logs cannot be rewritten ===================================='

do $t$
declare
  v_org_a uuid := (select v from t_ids where k = 'org_a');
  v_user_a uuid := (select v from t_ids where k = 'user_a');
  v_id    bigint;
begin
  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_org_a, v_user_a, 'lease.approved', 'lease',
          (select v from t_ids where k = 'lease_a'))
  returning id into v_id;

  -- UPDATE is refused for everyone, including the owner running this script.
  -- History is appended to, never amended.
  begin
    update audit_logs set action = 'lease.rejected' where id = v_id;
    perform pg_temp.assert(false, 'an audit entry must not be editable, even by the owner');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'an audit entry cannot be edited, even by the owner');
  end;

  insert into t_ids (k, v) values ('audit_probe', gen_random_uuid());
end;
$t$;

-- DELETE is refused for the application role: as far as the running system is
-- concerned, the audit log is append-only.
set role app_api;

do $t$
declare
  v_id bigint;
begin
  perform pg_temp.become((select v from t_ids where k = 'auth_a'));

  select id into v_id from audit_logs order by id desc limit 1;
  perform pg_temp.assert(v_id is not null, 'the application can read the audit entry');

  begin
    delete from audit_logs where id = v_id;
    perform pg_temp.assert(false, 'the application must not delete an audit entry');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the application cannot delete an audit entry');
  end;
end;
$t$;

reset role;

-- DELETE IS permitted for the table owner, which is what makes a lawful
-- erasure request answerable (section 11.7). Deleting an organization has to
-- be able to cascade to its logs; a trigger that refused the owner too would
-- mean the only supported procedure was to disable the trigger first, which is
-- strictly worse.
do $t$
declare
  v_org   uuid;
  v_user  uuid;
  v_before integer;
  v_after  integer;
begin
  insert into users (auth_user_id, email) values (gen_random_uuid(), 'purge@example.test')
  returning id into v_user;

  insert into organizations (name) values ('Purge me') returning id into v_org;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  insert into audit_logs (organization_id, actor_user_id, action, entity_type)
  values (v_org, v_user, 'organization.created', 'organization');

  select count(*) into v_before from audit_logs where organization_id = v_org;
  perform pg_temp.assert(v_before = 1, 'the retention fixture has an audit entry');

  -- The cascade from organizations reaches audit_logs and is allowed through.
  delete from organizations where id = v_org;

  select count(*) into v_after from audit_logs where organization_id = v_org;
  perform pg_temp.assert(v_after = 0,
    'deleting an organization as the owner cascades to its audit entries');

  delete from users where id = v_user;
end;
$t$;

do $t$
declare
  v_tpl uuid := (select v from t_ids where k = 'tpl_a');
begin
  begin
    update email_templates set html_body = '<p>silently changed</p>' where id = v_tpl;
    perform pg_temp.assert(false, 'a published template must not be editable');
  exception when integrity_constraint_violation then
    perform pg_temp.assert(true, 'a published template version is immutable');
  end;
end;
$t$;

\echo
\echo '== Unauthenticated access reaches nothing =================================='

set role app_api;

do $t$
declare
  v_count integer;
begin
  -- No identity set at all: the state a request with a missing or invalid
  -- token would produce if an authorization check were ever skipped.
  perform set_config('app.auth_user_id', '', false);

  perform pg_temp.assert(app.current_auth_user_id() is null, 'no identity is resolved');

  select count(*) into v_count from leases;
  perform pg_temp.assert(v_count = 0, 'an unauthenticated session reads no leases');

  select count(*) into v_count from properties;
  perform pg_temp.assert(v_count = 0, 'an unauthenticated session reads no properties');

  select count(*) into v_count from tenants;
  perform pg_temp.assert(v_count = 0, 'an unauthenticated session reads no tenants');

  select count(*) into v_count from email_logs;
  perform pg_temp.assert(v_count = 0, 'an unauthenticated session reads no email logs');

  -- A malformed subject claim must behave as "no identity", never as a match.
  perform set_config('app.auth_user_id', 'not-a-uuid', false);
  perform pg_temp.assert(app.current_auth_user_id() is null,
    'a malformed subject claim resolves to no identity');

  select count(*) into v_count from leases;
  perform pg_temp.assert(v_count = 0, 'a malformed claim reads no leases');
end;
$t$;

reset role;

\echo
\echo '== All isolation tests passed =============================================='

rollback;
