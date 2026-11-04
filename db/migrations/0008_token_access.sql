-- =============================================================================
-- 0008_token_access.sql
-- The only two doors to the stored Gmail refresh token.
--
-- "Restrict access to tokens to the email-sending component" (section 4.E) is
-- enforced at the privilege level, not by convention:
--
--   * 0010 revokes column privileges on the ciphertext columns from BOTH the
--     API role and the worker role. Neither can SELECT or UPDATE them, even
--     with a hand-written query, even if an authorization check is missed.
--   * The API stores a token only through app.store_email_account_token.
--   * The worker reads one only through app.read_email_account_token.
--   * Disconnecting destroys the ciphertext rather than flagging a column.
--
-- Encryption and decryption happen in application code (AES-256-GCM); the key
-- is never sent to PostgreSQL. These functions move opaque bytes.
-- =============================================================================

create or replace function app.store_email_account_token(
  p_email_account_id uuid,
  p_ciphertext       bytea,
  p_nonce            bytea,
  p_auth_tag         bytea,
  p_key_version      integer,
  p_granted_scopes   text[]
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_updated integer;
begin
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

  -- The caller must still be an administrator of the owning organization.
  -- This function is SECURITY DEFINER, so it would otherwise bypass the RLS
  -- that normally protects email_accounts.
  update email_accounts a
  set encrypted_refresh_token = p_ciphertext,
      token_nonce             = p_nonce,
      token_auth_tag          = p_auth_tag,
      token_key_version       = p_key_version,
      granted_scopes          = coalesce(p_granted_scopes, '{}'),
      connection_status       = 'connected',
      status_detail           = null,
      connected_at            = coalesce(a.connected_at, now()),
      disconnected_at         = null,
      last_error_at           = null,
      updated_at              = now()
  where a.id = p_email_account_id
    and app.can_administer_org(a.organization_id);

  get diagnostics v_updated = row_count;
  return v_updated = 1;
end;
$fn$;

comment on function app.store_email_account_token(uuid, bytea, bytea, bytea, integer, text[]) is
  'Stores an encrypted Gmail refresh token. Re-checks administrator rights because it bypasses RLS.';

-- -----------------------------------------------------------------------------
-- Read path, for the worker only.
--
-- Takes no organization argument and does no authorization check of its own:
-- EXECUTE is granted solely to the worker role (0010), and the worker only
-- ever asks for the account attached to a notification it already holds a
-- claim on. The API role is never granted EXECUTE here.
-- -----------------------------------------------------------------------------
create or replace function app.read_email_account_token(
  p_email_account_id uuid
)
returns table (
  ciphertext   bytea,
  nonce        bytea,
  auth_tag     bytea,
  key_version  integer,
  email_address citext
)
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select a.encrypted_refresh_token,
         a.token_nonce,
         a.token_auth_tag,
         a.token_key_version,
         a.email_address
  from email_accounts a
  where a.id = p_email_account_id
    -- A revoked or disconnected account yields nothing, so sending cannot
    -- continue after a disconnect (launch test 10, section 4).
    and a.connection_status = 'connected'
    and a.encrypted_refresh_token is not null
$fn$;

comment on function app.read_email_account_token(uuid) is
  'Returns the encrypted token for sending. EXECUTE is granted to the worker role only.';

-- -----------------------------------------------------------------------------
-- Disconnect: destroy the credential, do not merely flag it.
-- -----------------------------------------------------------------------------
create or replace function app.disconnect_email_account(
  p_email_account_id uuid,
  p_status           app.email_connection_status default 'not_connected',
  p_detail           text default null
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_updated integer;
  v_org     uuid;
begin
  if p_status not in ('not_connected', 'revoked') then
    raise exception 'Disconnecting must result in not_connected or revoked, not %L', p_status
      using errcode = 'invalid_parameter_value';
  end if;

  select organization_id into v_org
  from email_accounts
  where id = p_email_account_id;

  if v_org is null then
    return false;
  end if;

  if not app.can_administer_org(v_org) then
    raise exception 'Not permitted to disconnect this account'
      using errcode = 'insufficient_privilege';
  end if;

  update email_accounts
  set encrypted_refresh_token = null,
      token_nonce             = null,
      token_auth_tag          = null,
      token_key_version       = null,
      granted_scopes          = '{}',
      connection_status       = p_status,
      status_detail           = left(p_detail, 1000),
      disconnected_at         = now(),
      updated_at              = now()
  where id = p_email_account_id;

  get diagnostics v_updated = row_count;

  -- Nothing may remain queued against a sender that no longer exists. The
  -- rows are cancelled with a reason rather than deleted, so the calendar and
  -- logs still explain what happened.
  update scheduled_notifications
  set status = 'cancelled',
      resolution_reason = 'sender_disconnected',
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      updated_at = now()
  where email_account_id = p_email_account_id
    and status in ('scheduled', 'failed');

  return v_updated = 1;
end;
$fn$;

comment on function app.disconnect_email_account(uuid, app.email_connection_status, text) is
  'Destroys the stored credential and cancels every pending send for that sender.';

-- -----------------------------------------------------------------------------
-- Key rotation support.
--
-- Lists accounts still encrypted under an old key version so a rotation job
-- can re-encrypt them one at a time. Returns identifiers only, never bytes.
-- -----------------------------------------------------------------------------
create or replace function app.email_accounts_pending_key_rotation(
  p_current_key_version integer
)
returns table (email_account_id uuid, organization_id uuid, key_version integer)
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select a.id, a.organization_id, a.token_key_version
  from email_accounts a
  where a.encrypted_refresh_token is not null
    and (a.token_key_version is null or a.token_key_version < p_current_key_version)
$fn$;

comment on function app.email_accounts_pending_key_rotation(integer) is
  'Identifiers of accounts still encrypted under an older key version. Returns no key material.';
