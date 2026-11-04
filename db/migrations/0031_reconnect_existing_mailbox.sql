-- =============================================================================
-- 0031 — Reconnecting an already-connected mailbox
--
-- THE BUG
--   Pressing Reconnect answered "An unexpected error occurred". The API log
--   had the real reason:
--
--     23505: duplicate key value violates unique constraint
--            "email_accounts_organization_email_key"
--
--   app.begin_email_account_connection reuses an existing row only when it is
--   NOT connected:
--
--     where a.organization_id = p_organization_id
--       and a.connection_status <> 'connected'
--
--   on the stated reasoning that "a row that IS connected is left alone:
--   reconnecting writes over it through store_email_account_token". Nothing
--   arranges that. The authorize endpoint has no account id to pass; calling
--   this function IS how it gets one. So on a reconnect the reuse branch is
--   skipped and the INSERT at the foot of the function runs instead, trying
--   to add a second row under the placeholder address — which collides with
--   the first row's address and raises.
--
--   Before 0030 the collision was latent rather than visible: the callback
--   could never write the real address, so every row kept the placeholder and
--   the very first reconnect hit the unique index.
--
-- WHY REUSE RATHER THAN RELAX THE INDEX
--   This product has exactly one sending mailbox per workspace, and says so
--   on the settings page: "Every reminder is sent from this one account.
--   There is no fallback sender." Allowing a second row would make "which
--   mailbox is the mailbox" a real question, and the status endpoint already
--   answers it by ordering connected-first — so the two would disagree the
--   moment a second row existed.
--
--   The row is therefore reused whatever its connection status, with the same
--   connected-first ordering the status endpoint uses, so both agree on which
--   row is the mailbox.
--
-- WHAT A RECONNECT DELIBERATELY DOES NOT DO
--   It does not clear the stored token. Starting the flow is not finishing
--   it: somebody who opens the Google consent screen and backs out should
--   still have the working connection they had a moment ago. The token is
--   replaced only when a callback completes, by
--   app.complete_email_oauth_attempt.
-- =============================================================================

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

  -- The workspace's mailbox row, whatever state it is in. Connected first,
  -- then most recently touched — the same order the status endpoint uses to
  -- decide which row it is showing, so the two cannot pick different rows.
  select a.id into v_account_id
  from email_accounts a
  where a.organization_id = p_organization_id
  order by (a.connection_status = 'connected') desc, a.updated_at desc
  limit 1;

  if v_account_id is not null then
    -- Note what is NOT touched: the token columns and connection_status. A
    -- reconnect that is abandoned at the consent screen must leave the
    -- working connection exactly as it was.
    update email_accounts
    set email_address = coalesce(p_email_address, email_address),
        display_name  = coalesce(p_display_name, display_name),
        status_detail = null,
        updated_at    = now()
    where id = v_account_id;

    insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
    values (
      p_organization_id, v_user_id, 'email_account.connection_started',
      'email_account', v_account_id);

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
  'Creates or reuses the single account row an OAuth callback stores a token against. '
  'Reuses it whatever its connection status, so reconnecting does not try to add a second. '
  'Administrators only.';

-- -----------------------------------------------------------------------------
-- Verification
--
-- The regression specifically: connect, then start a second flow against the
-- already-connected row and require the SAME row back rather than a unique
-- violation.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org    uuid;
  v_user   uuid;
  v_auth   uuid := gen_random_uuid();
  v_first  uuid;
  v_second uuid;
  v_rows   integer;
  v_status text;
begin
  insert into organizations (name, timezone)
  values ('verification-0031', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0031@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  perform set_config('app.auth_user_id', v_auth::text, true);

  v_first := app.begin_email_account_connection(v_org, null, null);

  -- Pretend the first flow completed. The token columns are set too, not for
  -- show: email_accounts_connected_has_token_check refuses a row that claims
  -- to be connected without one, which is the constraint that caught an
  -- earlier draft of this block faking the status alone.
  update email_accounts
  set connection_status       = 'connected',
      email_address           = 'real@example.invalid',
      encrypted_refresh_token = '\x0102'::bytea,
      token_nonce             = decode(repeat('aa', 12), 'hex'),
      token_auth_tag          = decode(repeat('bb', 16), 'hex'),
      token_key_version       = 1,
      connected_at            = now()
  where id = v_first;

  -- Reconnect. This raised 23505 before.
  v_second := app.begin_email_account_connection(v_org, null, null);

  if v_second is distinct from v_first then
    raise exception
      'Verification 0031: reconnect returned a different row (% vs %).',
      v_second, v_first;
  end if;

  select count(*) into v_rows
  from email_accounts where organization_id = v_org;

  if v_rows <> 1 then
    raise exception
      'Verification 0031: expected one mailbox row, found %.', v_rows;
  end if;

  -- And the live connection survived starting a flow that was never finished.
  select connection_status::text into v_status
  from email_accounts where id = v_first;

  if v_status is distinct from 'connected' then
    raise exception
      'Verification 0031: starting a reconnect disturbed the live connection (now %).',
      v_status;
  end if;

  if not exists (
    select 1 from email_accounts
    where id = v_first and email_address = 'real@example.invalid'
  ) then
    raise exception
      'Verification 0031: starting a reconnect overwrote the stored address.';
  end if;

  raise exception 'Verification 0031 passed, rolling back.';
exception
  when others then
    if sqlerrm = 'Verification 0031 passed, rolling back.' then
      raise notice 'Verification 0031 passed.';
    else
      raise;
    end if;
end;
$verify$;
