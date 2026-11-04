-- =============================================================================
-- 0022_test_send.sql
--
-- "Send me a test message."
--
-- The point of a test send is to prove the whole path works: the stored
-- credential decrypts, Google accepts the token, the message is built
-- correctly, and the send is recorded. A function that called Gmail directly
-- from the API would prove none of that, because the API cannot read a token
-- and does not build messages.
--
-- So a test send is an ordinary delivery on the ordinary queue. It is picked
-- up by the same worker, sent by the same sender, and logged the same way. The
-- only thing that distinguishes it is the subject line and that it is
-- addressed to the person who asked for it.
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
    p_organization_id, 'test', null, 'email',
    v_user_id, v_email,
    'Test message from LeaseReminder',
    'This confirms that your connected Gmail account can send reminders.' || E'\n\n'
      || 'If you are reading this, the connection works: the stored credential '
      || 'decrypted, Google accepted it, and the message was delivered to your '
      || 'provider. Rent reminders will be sent the same way.' || E'\n\n'
      || 'Nothing about your leases has changed, and no tenant received this.',
    '/settings/email',
    'pending',
    -- Time-based, so a user can send more than one. Every other delivery key
    -- is content-based precisely to prevent duplicates; a test is the one
    -- case where repeating it on purpose is the whole point.
    'test:' || p_organization_id::text || ':' || v_user_id::text || ':'
      || extract(epoch from clock_timestamp())::bigint::text
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

grant execute on function app.enqueue_test_email(uuid) to app_api;

-- PUBLIC execute is implicit rather than a stored grant, so it has to be
-- revoked explicitly every time functions are added.
revoke all on all functions in schema app from public;

do $$
declare
  v_count integer;
begin
  if has_function_privilege('app_worker', 'app.enqueue_test_email(uuid)', 'execute') then
    raise exception 'app_worker can queue test messages; only the API may.';
  end if;

  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Test send: grants verified.';
end;
$$;
