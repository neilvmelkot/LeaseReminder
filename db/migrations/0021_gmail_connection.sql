-- =============================================================================
-- 0021_gmail_connection.sql
--
-- The remaining write path for Gmail sending (phase 4).
--
-- The hard parts were already built in 0005 and 0008: the email_accounts
-- table with its four ciphertext columns, store_email_account_token (API,
-- administrators only), read_email_account_token (worker only), and
-- disconnect_email_account, which destroys the credential rather than
-- flagging it. Column-level grants in 0010 put the token columns out of the
-- API role's reach entirely.
--
-- What is missing is the beginning and the end of the story:
--
--   * a way to CREATE the account row before a token exists, so the OAuth
--     callback has something to store a token against;
--   * a way for the worker to find the one connected account for an
--     organization, since it holds no table privileges;
--   * a way to append to email_logs, which is the record of what was actually
--     sent and must be written by the worker alone.
--
-- Section 13.B: exactly one connected Gmail account sends everything, and
-- there is no fallback sender. That is enforced here by a partial unique
-- index rather than by convention.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- One connected account per organization.
--
-- A partial unique index rather than a CHECK, because the constraint is across
-- rows. Disconnected and errored rows are left out of it on purpose: a history
-- of previous connections is useful, and only one of them may be live.
-- -----------------------------------------------------------------------------
create unique index if not exists email_accounts_one_connected_per_org
  on email_accounts (organization_id)
  where connection_status = 'connected';

comment on index email_accounts_one_connected_per_org is
  'Section 13.B: exactly one connected Gmail account per organization, no fallback sender.';

-- -----------------------------------------------------------------------------
-- app.begin_email_account_connection
--
-- Creates, or reuses, the row an OAuth callback will store a token against.
--
-- Called BEFORE the user is sent to Google, so the callback has an id to work
-- with and so a user who abandons the flow leaves a visible "not connected"
-- row rather than nothing at all.
-- -----------------------------------------------------------------------------
create or replace function app.begin_email_account_connection(
  p_organization_id uuid,
  p_email_address   text default null,
  p_display_name    text default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id    uuid;
  v_account_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Connecting a mailbox requires an authenticated user.'
      using errcode = '28000';
  end if;

  -- Administrators only. A connected mailbox sends mail in the
  -- organization's name, which is not an ordinary member's decision.
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can connect a mailbox.'
      using errcode = '42501';
  end if;

  -- Reuse a row that is not currently connected, so repeatedly starting the
  -- flow does not accumulate abandoned rows. A row that IS connected is left
  -- alone: reconnecting writes over it through store_email_account_token.
  select a.id into v_account_id
  from email_accounts a
  where a.organization_id = p_organization_id
    and a.connection_status <> 'connected'
  order by a.updated_at desc
  limit 1;

  if v_account_id is not null then
    update email_accounts
    set email_address = coalesce(p_email_address, email_address),
        display_name  = coalesce(p_display_name, display_name),
        status_detail = null,
        updated_at    = now()
    where id = v_account_id;

    return v_account_id;
  end if;

  insert into email_accounts (
    organization_id, provider, email_address, display_name, connection_status
  )
  values (
    p_organization_id, 'gmail',
    -- A placeholder until the callback learns the real address from Google.
    -- The column's shape CHECK requires something that looks like an address,
    -- so it cannot simply be left blank.
    coalesce(p_email_address, 'pending@connection.invalid'),
    p_display_name, 'not_connected'
  )
  returning id into v_account_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    p_organization_id, v_user_id, 'email_account.connection_started',
    'email_account', v_account_id);

  return v_account_id;
end;
$fn$;

comment on function app.begin_email_account_connection is
  'Creates or reuses the account row an OAuth callback stores a token against. '
  'Administrators only.';

-- -----------------------------------------------------------------------------
-- app.set_email_account_identity
--
-- The callback learns the real address from Google, after the token exchange.
-- Separate from store_email_account_token so that function keeps doing one
-- thing, and so the address can be corrected without touching the credential.
-- -----------------------------------------------------------------------------
create or replace function app.set_email_account_identity(
  p_email_account_id uuid,
  p_email_address    text,
  p_display_name     text default null
)
returns boolean
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_updated integer;
begin
  if p_email_address is null or p_email_address = '' then
    raise exception 'An email address is required.' using errcode = '22023';
  end if;

  update email_accounts a
  set email_address = p_email_address,
      display_name  = coalesce(p_display_name, a.display_name),
      updated_at    = now()
  where a.id = p_email_account_id
    and app.can_administer_org(a.organization_id);

  get diagnostics v_updated = row_count;

  return v_updated = 1;
end;
$fn$;

-- -----------------------------------------------------------------------------
-- app.connected_email_account
--
-- The worker's lookup. It holds no table privileges, so it reaches the one
-- sending account through a function like everything else.
--
-- Returns nothing when no account is connected, which is what stops sending
-- after a disconnect: the caller has no address to send from and records a
-- configuration failure instead.
-- -----------------------------------------------------------------------------
create or replace function app.connected_email_account(p_organization_id uuid)
returns table (
  email_account_id uuid,
  email_address    text,
  display_name     text,
  daily_send_limit integer
)
language sql
stable
security definer
set search_path = app, public
as $fn$
  select a.id, a.email_address::text, a.display_name, a.daily_send_limit
  from email_accounts a
  where a.organization_id = p_organization_id
    and a.connection_status = 'connected'
    and a.encrypted_refresh_token is not null
  limit 1
$fn$;

comment on function app.connected_email_account is
  'The one sending account for an organization, for the worker. Empty after a disconnect.';

-- -----------------------------------------------------------------------------
-- app.record_email_log
--
-- Appends to the send record. Written by the worker alone: the API has no
-- INSERT on email_logs, because a log the application can write is a log that
-- can disagree with what the provider actually accepted.
-- -----------------------------------------------------------------------------
create or replace function app.record_email_log(
  p_organization_id   uuid,
  p_email_account_id  uuid,
  p_sender_email      text,
  p_recipients        jsonb,
  p_subject           text,
  p_status            text,
  p_error_category    text default 'none',
  p_error_message     text default null,
  p_provider_message_id text default null,
  p_attempt_number    integer default 1,
  p_lease_id          uuid default null,
  p_scheduled_notification_id uuid default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_id uuid;
begin
  insert into email_logs (
    organization_id, email_account_id, sender_email, recipients_json, subject,
    status, error_category, error_message, provider_message_id, attempt_number,
    provider_accepted_at, lease_id, scheduled_notification_id
  )
  values (
    p_organization_id, p_email_account_id, p_sender_email,
    coalesce(p_recipients, '[]'::jsonb),
    -- The column caps this at 500 characters and requires at least one.
    left(coalesce(nullif(p_subject, ''), '(no subject)'), 500),
    p_status::app.email_send_status,
    coalesce(p_error_category, 'none')::app.email_error_category,
    -- Operator-safe only. Never the body, never a lease term.
    left(p_error_message, 1000),
    p_provider_message_id,
    greatest(coalesce(p_attempt_number, 1), 1),
    case when p_status = 'accepted' then now() end,
    p_lease_id, p_scheduled_notification_id
  )
  returning id into v_id;

  -- A successful send is also the freshest evidence the connection works.
  if p_status = 'accepted' and p_email_account_id is not null then
    update email_accounts
    set last_successful_send_at = now(), updated_at = now()
    where id = p_email_account_id;
  end if;

  -- An authorization failure means the token no longer works. Recording it on
  -- the account is what turns "one send failed" into "this mailbox needs
  -- reconnecting", which is the thing a user has to be told.
  if p_status = 'failed' and p_error_category = 'authorization'
     and p_email_account_id is not null
  then
    update email_accounts
    set connection_status = 'reauthorization_required',
        status_detail = left(coalesce(p_error_message, 'Authorization failed.'), 1000),
        last_error_at = now(),
        updated_at = now()
    where id = p_email_account_id;
  end if;

  return v_id;
end;
$fn$;

comment on function app.record_email_log is
  'Appends a send record and updates the account''s health. Worker only.';

-- -----------------------------------------------------------------------------
-- app.email_account_sends_today
--
-- Gmail enforces a daily cap and so does this application, which is the
-- difference between stopping at the limit and having Google start refusing.
-- -----------------------------------------------------------------------------
create or replace function app.email_account_sends_today(p_email_account_id uuid)
returns integer
language sql
stable
security definer
set search_path = app, public
as $fn$
  select count(*)::integer
  from email_logs l
  where l.email_account_id = p_email_account_id
    and l.status = 'accepted'
    and l.created_at >= date_trunc('day', now())
$fn$;

-- =============================================================================
-- Grants
-- =============================================================================

grant execute on function app.begin_email_account_connection(uuid, text, text) to app_api;
grant execute on function app.set_email_account_identity(uuid, text, text) to app_api;

-- Worker only. The API must never look up a sending account or write a send
-- record: both would create a second path around the queue, and the log would
-- stop being evidence of what the provider accepted.
grant execute on function app.connected_email_account(uuid) to app_worker;
grant execute on function app.record_email_log(
  uuid, uuid, text, jsonb, text, text, text, text, text, integer, uuid, uuid) to app_worker;
grant execute on function app.email_account_sends_today(uuid) to app_worker;

-- PUBLIC execute on a function is implicit rather than a stored grant, so
-- ALTER DEFAULT PRIVILEGES cannot revoke it. Explicit, every time.
revoke all on all functions in schema app from public;

-- =============================================================================
-- Verification
-- =============================================================================

do $$
declare
  v_count integer;
begin
  -- The API must not be able to read a token, find the sending account, or
  -- write the send log.
  if has_function_privilege('app_api', 'app.read_email_account_token(uuid)', 'execute') then
    raise exception 'app_api can read Gmail tokens; it must not.';
  end if;

  if has_function_privilege('app_api', 'app.connected_email_account(uuid)', 'execute') then
    raise exception 'app_api can resolve the sending account; only the worker may.';
  end if;

  if has_function_privilege(
       'app_api',
       'app.record_email_log(uuid, uuid, text, jsonb, text, text, text, text, text, integer, uuid, uuid)',
       'execute')
  then
    raise exception 'app_api can write the email log; only the worker may.';
  end if;

  -- The worker must not be able to start a connection or store a token.
  if has_function_privilege(
       'app_worker',
       'app.store_email_account_token(uuid, bytea, bytea, bytea, integer, text[])',
       'execute')
  then
    raise exception 'app_worker can store tokens; only the API may.';
  end if;

  -- The API must still have no reach into the token columns themselves.
  select count(*) into v_count
  from information_schema.column_privileges
  where grantee = 'app_api'
    and table_name = 'email_accounts'
    and column_name in (
      'encrypted_refresh_token', 'token_nonce', 'token_auth_tag', 'token_key_version');

  if v_count > 0 then
    raise exception
      'app_api holds % column grant(s) on the Gmail token columns.', v_count;
  end if;

  -- The worker still holds no base-table privileges. Views are excluded: it
  -- reads app.queue_health, which is a monitoring view, not stored data.
  select count(*) into v_count
  from information_schema.role_table_grants g
  join pg_class c on c.relname = g.table_name
  join pg_namespace n on n.oid = c.relnamespace and n.nspname = g.table_schema
  where g.grantee = 'app_worker'
    and c.relkind = 'r';

  if v_count > 0 then
    raise exception 'app_worker holds % base-table grant(s).', v_count;
  end if;

  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Gmail connection: grants and the single-account rule verified.';
end;
$$;
