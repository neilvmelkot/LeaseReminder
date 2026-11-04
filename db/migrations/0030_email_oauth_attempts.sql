-- =============================================================================
-- 0030 — Making the Gmail connection able to complete
--
-- THE BUG
--   Connecting a mailbox always failed with "The mailbox could not be saved.
--   Sign in as an administrator and try again." — for an owner, for an
--   administrator, for anybody. It was not a permissions problem. It could
--   never have worked.
--
--   The OAuth callback is deliberately anonymous: Google returns the browser
--   by a top-level GET from another site, and a session cookie cannot be
--   relied on to survive that navigation. So the request reaches the database
--   with no identity, app.current_user_id() is null, and
--   app.can_administer_org() is therefore false.
--
--   But both functions the callback calls —
--   app.store_email_account_token (0008) and
--   app.set_email_account_identity (0021) — gate their UPDATE on exactly that
--   check. Zero rows matched, store_email_account_token returned false, and
--   the endpoint reported it as a missing administrator. The address was
--   never written either; that call ignores its own result.
--
--   An anonymous endpoint and a function demanding a signed-in administrator
--   cannot both be satisfied. The re-check was defence in depth against a
--   condition that is structurally unreachable on the only path that calls it.
--
-- THE FIX, AND WHY NOT SIMPLY DROP THE CHECK
--   Dropping it would leave the attempt cookie as the only authority, and that
--   cookie is unsigned JSON naming an email_account_id. The state comparison
--   in the callback compares the cookie against itself, so it catches a
--   mismatched response but proves nothing about a wholly forged cookie.
--
--   So the authority moves server-side. An attempt is recorded here when an
--   authenticated administrator starts the flow — which is where the rights
--   actually exist — and the callback presents the state it was given. The
--   attempt is found by the digest of that state, is single use, and expires.
--   Authority comes from a row this server wrote, not from a cookie the
--   browser hands back.
--
-- WHY A DIGEST RATHER THAN THE STATE
--   The state is a bearer value for the duration of the flow. Storing only
--   its SHA-256 means a database backup, or a leaked row, cannot be replayed
--   into somebody's mailbox connection. The same reasoning as the invitation
--   tokens in 0025, which store a digest for the same reason.
-- =============================================================================

create table if not exists email_oauth_attempts (
  id                   uuid primary key default gen_random_uuid(),
  email_account_id     uuid not null references email_accounts (id) on delete cascade,
  organization_id      uuid not null references organizations (id) on delete cascade,

  -- Who began the flow. Re-checked at completion, so that withdrawing
  -- somebody's administrator role also invalidates an attempt they left
  -- half-finished.
  initiated_by_user_id uuid not null references users (id) on delete cascade,

  -- SHA-256 of the OAuth state. Never the state itself.
  state_hash           bytea not null,

  created_at           timestamptz not null default now(),
  expires_at           timestamptz not null,
  -- Set the moment it is used. A second callback with the same state finds
  -- nothing, so a replayed response cannot overwrite a live connection.
  consumed_at          timestamptz,

  constraint email_oauth_attempts_state_hash_key unique (state_hash),
  constraint email_oauth_attempts_state_hash_length_check
    check (octet_length(state_hash) = 32)
);

comment on table email_oauth_attempts is
  'In-flight Gmail OAuth authorizations. The server-side authority for a callback that carries no session.';

create index if not exists email_oauth_attempts_expiry_idx
  on email_oauth_attempts (expires_at)
  where consumed_at is null;

-- RLS on with no policy at all, which denies every non-owner role outright.
-- This is internal plumbing: it is written and read only by the two SECURITY
-- DEFINER functions below, and nothing in the API has any business selecting
-- from it directly.
alter table email_oauth_attempts enable row level security;

-- The default privileges from 0010 grant app_api all four verbs on a new
-- table. It must not have them: with no policy they would match nothing, but
-- leaving them granted misstates the intent.
revoke all on email_oauth_attempts from app_api;

-- -----------------------------------------------------------------------------
-- app.begin_email_oauth_attempt
--
-- Called from the authorize endpoint, where the caller IS authenticated and
-- the administrator check is both meaningful and satisfiable.
-- -----------------------------------------------------------------------------
create or replace function app.begin_email_oauth_attempt(
  p_email_account_id uuid,
  p_state_hash       bytea,
  p_ttl_seconds      integer
)
returns uuid
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_user_id uuid;
  v_org     uuid;
  v_id      uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Starting a mailbox connection requires an authenticated user.'
      using errcode = '28000';
  end if;

  if p_state_hash is null or octet_length(p_state_hash) <> 32 then
    raise exception 'A 32-byte state digest is required'
      using errcode = 'invalid_parameter_value';
  end if;

  select a.organization_id into v_org
  from email_accounts a
  where a.id = p_email_account_id;

  if v_org is null then
    raise exception 'No such email account.' using errcode = 'no_data_found';
  end if;

  if not app.can_administer_org(v_org) then
    raise exception 'Only an owner or administrator can connect a mailbox.'
      using errcode = 'insufficient_privilege';
  end if;

  -- Any earlier unfinished attempt for this account is abandoned. Leaving it
  -- usable would mean two live states for one mailbox.
  delete from email_oauth_attempts
  where email_account_id = p_email_account_id and consumed_at is null;

  insert into email_oauth_attempts (
    email_account_id, organization_id, initiated_by_user_id, state_hash, expires_at
  )
  values (
    p_email_account_id, v_org, v_user_id, p_state_hash,
    now() + make_interval(secs => greatest(coalesce(p_ttl_seconds, 600), 60))
  )
  returning id into v_id;

  return v_id;
end;
$fn$;

comment on function app.begin_email_oauth_attempt is
  'Records an in-flight Gmail authorization for the current administrator. Returns the attempt id.';

-- -----------------------------------------------------------------------------
-- app.complete_email_oauth_attempt
--
-- Called from the anonymous callback. Deliberately does NOT consult the
-- session: there is none, and that was the whole bug. It authorises on the
-- attempt row instead, and re-checks that the person who started the flow is
-- still an administrator.
-- -----------------------------------------------------------------------------
create or replace function app.complete_email_oauth_attempt(
  p_state_hash     bytea,
  p_email_address  text,
  p_ciphertext     bytea,
  p_nonce          bytea,
  p_auth_tag       bytea,
  p_key_version    integer,
  p_granted_scopes text[]
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_attempt email_oauth_attempts;
  v_updated integer;
begin
  -- The same parameter checks as app.store_email_account_token. A token
  -- written with a wrong-length nonce is a token that can never be decrypted,
  -- and the failure would not surface until the first send.
  if p_ciphertext is null or octet_length(p_ciphertext) = 0 then
    raise exception 'A refresh token ciphertext is required'
      using errcode = 'invalid_parameter_value';
  end if;
  if p_nonce is null or octet_length(p_nonce) <> 12 then
    raise exception 'AES-256-GCM requires a 12-byte nonce'
      using errcode = 'invalid_parameter_value';
  end if;
  if p_auth_tag is null or octet_length(p_auth_tag) <> 16 then
    raise exception 'AES-256-GCM requires a 16-byte authentication tag'
      using errcode = 'invalid_parameter_value';
  end if;
  if p_key_version is null or p_key_version < 1 then
    raise exception 'A key version is required so the token can be rotated'
      using errcode = 'invalid_parameter_value';
  end if;

  -- FOR UPDATE, so two callbacks racing on one state cannot both consume it.
  select * into v_attempt
  from email_oauth_attempts
  where state_hash = p_state_hash
    and consumed_at is null
    and expires_at > now()
  for update;

  if not found then
    -- Unknown, already used, or expired. One answer for all three: the
    -- caller is anonymous, and telling it which would let it probe.
    return false;
  end if;

  -- Still an administrator? The attempt may have been started ten minutes ago
  -- and the role withdrawn since. Checked directly rather than through
  -- app.can_administer_org, which reads the session this path does not have.
  if not exists (
    select 1
    from organization_members m
    where m.organization_id = v_attempt.organization_id
      and m.user_id = v_attempt.initiated_by_user_id
      and m.role in ('owner', 'admin')
  ) then
    return false;
  end if;

  update email_oauth_attempts
  set consumed_at = now()
  where id = v_attempt.id;

  update email_accounts a
  set encrypted_refresh_token = p_ciphertext,
      token_nonce             = p_nonce,
      token_auth_tag          = p_auth_tag,
      token_key_version       = p_key_version,
      granted_scopes          = coalesce(p_granted_scopes, '{}'),
      -- The address Google reported, when it reported one. Left alone
      -- otherwise rather than blanked.
      email_address           = coalesce(nullif(trim(p_email_address), ''), a.email_address),
      connection_status       = 'connected',
      status_detail           = null,
      connected_at            = coalesce(a.connected_at, now()),
      disconnected_at         = null,
      last_error_at           = null,
      updated_at              = now()
  where a.id = v_attempt.email_account_id;

  get diagnostics v_updated = row_count;

  insert into audit_logs (
    organization_id, actor_user_id, actor_kind, action, entity_type, entity_id
  )
  values (
    v_attempt.organization_id, v_attempt.initiated_by_user_id, 'user',
    'email_account.connected', 'email_account', v_attempt.email_account_id
  );

  return v_updated = 1;
end;
$fn$;

comment on function app.complete_email_oauth_attempt is
  'Stores a Gmail refresh token for an anonymous OAuth callback, authorised by a single-use server-side attempt.';

-- -----------------------------------------------------------------------------
-- Privileges
--
-- Targeted rather than the blanket revoke used in 0028: only these two
-- functions are new, and revoking the whole schema from PUBLIC to re-grant
-- three names is how an earlier grant gets dropped by accident.
-- -----------------------------------------------------------------------------
revoke all on function app.begin_email_oauth_attempt(uuid, bytea, integer) from public;
revoke all on function
  app.complete_email_oauth_attempt(bytea, text, bytea, bytea, bytea, integer, text[])
  from public;

grant execute on function app.begin_email_oauth_attempt(uuid, bytea, integer) to app_api;
grant execute on function
  app.complete_email_oauth_attempt(bytea, text, bytea, bytea, bytea, integer, text[])
  to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Runs the flow rather than checking the functions exist, including the two
-- refusals that matter: a replayed state, and an initiator who has lost their
-- administrator role.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org     uuid;
  v_user    uuid;
  v_auth    uuid := gen_random_uuid();
  v_account uuid;
  v_state   bytea := sha256('verification-0030'::bytea);
  v_second  bytea := sha256('verification-0030-second'::bytea);
  v_ok      boolean;
  v_status  text;
begin
  insert into organizations (name, timezone)
  values ('verification-0030', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0030@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  insert into email_accounts (organization_id, email_address)
  values (v_org, 'mailbox-0030@example.invalid')
  returning id into v_account;

  perform set_config('app.auth_user_id', v_auth::text, true);

  perform app.begin_email_oauth_attempt(v_account, v_state, 600);

  -- The callback has no session. This is the condition the old code could
  -- never satisfy, so the verification explicitly removes the identity first.
  perform set_config('app.auth_user_id', NULL, true);

  v_ok := app.complete_email_oauth_attempt(
    v_state, 'reported@example.invalid',
    '\x0102'::bytea, decode(repeat('aa', 12), 'hex'),
    decode(repeat('bb', 16), 'hex'), 1, array['https://www.googleapis.com/auth/gmail.send']);

  if not v_ok then
    raise exception
      'Verification 0030: the anonymous callback could not store the token.';
  end if;

  select connection_status::text into v_status
  from email_accounts where id = v_account;

  if v_status is distinct from 'connected' then
    raise exception
      'Verification 0030: expected connection_status connected, got %.', v_status;
  end if;

  if not exists (
    select 1 from email_accounts
    where id = v_account and email_address = 'reported@example.invalid'
  ) then
    raise exception 'Verification 0030: the reported address was not stored.';
  end if;

  -- Single use: the same state a second time must refuse.
  v_ok := app.complete_email_oauth_attempt(
    v_state, null,
    '\x0102'::bytea, decode(repeat('aa', 12), 'hex'),
    decode(repeat('bb', 16), 'hex'), 1, array['x']);

  if v_ok then
    raise exception 'Verification 0030: a consumed attempt was accepted twice.';
  end if;

  -- An initiator who is no longer an administrator must refuse.
  perform set_config('app.auth_user_id', v_auth::text, true);
  perform app.begin_email_oauth_attempt(v_account, v_second, 600);
  perform set_config('app.auth_user_id', NULL, true);

  update organization_members set role = 'read_only'
  where organization_id = v_org and user_id = v_user;

  v_ok := app.complete_email_oauth_attempt(
    v_second, null,
    '\x0102'::bytea, decode(repeat('aa', 12), 'hex'),
    decode(repeat('bb', 16), 'hex'), 1, array['x']);

  if v_ok then
    raise exception
      'Verification 0030: an attempt from a demoted initiator was accepted.';
  end if;

  raise exception 'Verification 0030 passed, rolling back.';
exception
  when others then
    if sqlerrm = 'Verification 0030 passed, rolling back.' then
      raise notice 'Verification 0030 passed.';
    else
      raise;
    end if;
end;
$verify$;
