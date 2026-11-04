-- =============================================================================
-- 0032 — "Send me a test" could never queue anything
--
-- THE BUG
--   Pressing "Send me a test" answered:
--
--     new row for relation "notification_deliveries" violates check
--     constraint "notification_deliveries_subject_type_check"
--
--   app.enqueue_test_email (0022) inserts subject_type = 'test'. The
--   constraint, written in 0014, allows:
--
--     'task', 'lease', 'rent_occurrence', 'lease_document', 'vendor', 'system'
--
--   'test' is not among them, so the insert has always raised and no test
--   message has ever been queued. 0022's own verification block checked that
--   the function exists and that its guards refuse the wrong caller; it never
--   reached a successful insert, which is the one path that would have caught
--   this.
--
-- WHY 'system' RATHER THAN WIDENING THE CONSTRAINT
--   subject_type says what a delivery is ABOUT, so that a notification can be
--   traced back to the thing that caused it. A test message is not about a
--   task or a lease — it is about the installation itself, which is what
--   'system' already means. Nothing in the worker or the frontend branches on
--   this column, so the value is descriptive and 'system' describes it.
--
--   Adding 'test' would mean a seventh category that exists for one function
--   and means "ignore me", which is how a vocabulary stops being worth
--   reading.
--
--   The subject line and the recipient already distinguish a test, which is
--   what 0022 said made it distinguishable in the first place.
-- =============================================================================

create or replace function app.enqueue_test_email(p_organization_id uuid)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id     uuid;
  v_email       citext;
  v_delivery_id uuid;
  v_connected   boolean;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Sending a test message requires an authenticated user.'
      using errcode = '28000';
  end if;

  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can send a test message.'
      using errcode = '42501';
  end if;

  -- Addressed to the person asking, read from their own user record rather
  -- than accepted as a parameter. An endpoint that took an arbitrary address
  -- and sent mail to it from a connected Gmail account would be an open
  -- relay with extra steps.
  select u.email into v_email from users u where u.id = v_user_id;

  if v_email is null then
    raise exception 'Your account has no email address to send to.'
      using errcode = '22023';
  end if;

  -- Refused up front when nothing is connected. The send would fail anyway,
  -- but failing here says why in one sentence instead of leaving a failed
  -- row for the user to interpret.
  select exists (
    select 1 from email_accounts a
    where a.organization_id = p_organization_id
      and a.connection_status = 'connected'
      and a.encrypted_refresh_token is not null
  ) into v_connected;

  if not v_connected then
    raise exception 'No Gmail account is connected, so there is nothing to send from.'
      using errcode = '23514';
  end if;

  insert into notification_deliveries (
    organization_id, subject_type, subject_id, channel,
    recipient_user_id, recipient_email, title, body, link_path,
    status, idempotency_key
  )
  values (
    -- 'system', not 'test': see the header. This is the line that raised.
    p_organization_id, 'system', null, 'email',
    v_user_id, v_email,
    'Test message from LeaseReminder',
    'This confirms that your connected Gmail account can send reminders.' || E'\n\n'
      || 'If you are reading this, the connection works: the stored credential '
      || 'decrypted, Google accepted it, and the message was delivered to your '
      || 'provider. Rent reminders will be sent the same way.' || E'\n\n'
      || 'Nothing about your leases has changed, and no tenant received this.',
    '/settings/email',
    'pending',
    -- Random, so a user can send more than one. Every other delivery key is
    -- content-based precisely to prevent duplicates; a test is the one case
    -- where repeating it on purpose is the whole point, so the key says
    -- "deliberately unique" rather than approximating it.
    --
    -- This was epoch SECONDS, which is an approximation that fails on the
    -- obvious input: two clicks inside one second produced the same key and
    -- the second raised a unique violation. The verification below sends
    -- twice in a row and caught it.
    'test:' || p_organization_id::text || ':' || v_user_id::text || ':'
      || gen_random_uuid()::text
  )
  returning id into v_delivery_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    p_organization_id, v_user_id, 'email_account.test_sent',
    'notification_delivery', v_delivery_id);

  return v_delivery_id;
end;
$fn$;

comment on function app.enqueue_test_email is
  'Queues a test message to the requesting user''s own address, on the ordinary '
  'delivery queue, so the test exercises the real send path.';

revoke all on function app.enqueue_test_email(uuid) from public;
grant execute on function app.enqueue_test_email(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Reaches a SUCCESSFUL insert, which is precisely what 0022's verification did
-- not do. Checking that a function exists and refuses the wrong caller proves
-- nothing about the row it writes.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org      uuid;
  v_user     uuid;
  v_auth     uuid := gen_random_uuid();
  v_account  uuid;
  v_delivery uuid;
  v_subject  text;
begin
  insert into organizations (name, timezone)
  values ('verification-0032', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0032@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  -- A connected mailbox, token included: the status alone will not satisfy
  -- email_accounts_connected_has_token_check.
  insert into email_accounts (
    organization_id, email_address, connection_status,
    encrypted_refresh_token, token_nonce, token_auth_tag, token_key_version,
    connected_at
  )
  values (
    v_org, 'mailbox-0032@example.invalid', 'connected',
    '\x0102'::bytea, decode(repeat('aa', 12), 'hex'),
    decode(repeat('bb', 16), 'hex'), 1, now()
  )
  returning id into v_account;

  perform set_config('app.auth_user_id', v_auth::text, true);

  v_delivery := app.enqueue_test_email(v_org);

  if v_delivery is null then
    raise exception 'Verification 0032: no delivery was queued.';
  end if;

  select subject_type into v_subject
  from notification_deliveries where id = v_delivery;

  if v_subject is distinct from 'system' then
    raise exception
      'Verification 0032: expected subject_type system, got %.', v_subject;
  end if;

  -- Two in a row, with no pause. A test you can only send once a second is
  -- not a test, and this is what caught the epoch-seconds key: the second
  -- call raised a unique violation on notification_deliveries.
  if app.enqueue_test_email(v_org) is null then
    raise exception 'Verification 0032: a second test send did not queue.';
  end if;

  raise exception 'Verification 0032 passed, rolling back.';
exception
  when others then
    if sqlerrm = 'Verification 0032 passed, rolling back.' then
      raise notice 'Verification 0032 passed.';
    else
      raise;
    end if;
end;
$verify$;
