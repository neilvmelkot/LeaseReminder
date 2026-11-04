-- =============================================================================
-- 0010_roles_and_grants.sql
-- Least-privilege database roles (section 4.B, 4.I).
--
--   app_api     The web API. Full DML on application tables, every statement
--               filtered by the RLS policies in 0009. Cannot read or write
--               the Gmail token ciphertext at the column level.
--
--   app_worker  The background worker. NO table privileges whatsoever. It can
--               only EXECUTE the reviewed SECURITY DEFINER functions in
--               0007/0008. Strictly narrower than app_api, as required.
--
--   The migration role (app_owner on self-hosted PostgreSQL, postgres on
--   Supabase) owns the tables and the functions. Services never connect as it.
--
-- Roles are created WITHOUT LOGIN and WITHOUT a password on purpose: no
-- credential belongs in source control. Operations grants login separately:
--
--     alter role app_api    with login password '<from the secrets manager>';
--     alter role app_worker with login password '<from the secrets manager>';
--
-- Neither role is a superuser and neither has BYPASSRLS. That is the single
-- most important line in this file.
-- =============================================================================

do $roles$
begin
  if not exists (select 1 from pg_roles where rolname = 'app_api') then
    create role app_api nologin noinherit;
  end if;

  if not exists (select 1 from pg_roles where rolname = 'app_worker') then
    create role app_worker nologin noinherit;
  end if;
end;
$roles$;

comment on role app_api is
  'Web API role. DML on application tables, filtered by RLS. No access to token ciphertext.';
comment on role app_worker is
  'Background worker role. No table privileges; only EXECUTE on reviewed app.* functions.';

-- -----------------------------------------------------------------------------
-- Close the default-open door
--
-- PostgreSQL historically grants CREATE on schema public to PUBLIC. Left
-- alone, any role could create objects there, including a function that
-- shadows one of ours on a mutable search_path.
-- -----------------------------------------------------------------------------
revoke all on schema public from public;
revoke all on schema app from public;
revoke create on schema public from public;

grant usage on schema public to app_api, app_worker;
grant usage on schema app to app_api, app_worker;

-- PostgreSQL grants EXECUTE on a new function to PUBLIC automatically. Left
-- alone, every role could call app.read_email_account_token -- the one
-- function that exists to hand out a decryptable credential.
--
-- ALTER DEFAULT PRIVILEGES is not sufficient on its own: it applies only to
-- functions created AFTER it runs, and the functions in 0007 and 0008 already
-- exist by now. So revoke from PUBLIC explicitly, then set the default for
-- functions added by later migrations.
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;
alter default privileges in schema app revoke execute on functions from public;

-- The trigger functions are needed by any role whose statements fire them.
-- These are the only app.* functions granted broadly, and none of them reads
-- a credential or crosses an organization boundary.
grant execute on function app.touch_updated_at() to app_api;
grant execute on function app.deny_mutation() to app_api;
grant execute on function app.validate_timezone_column() to app_api;
grant execute on function app.protect_published_template() to app_api;
grant execute on function app.protect_sent_notification() to app_api;
grant execute on function app.validate_rule_override() to app_api;

-- =============================================================================
-- app_api
-- =============================================================================

-- Full DML. RLS decides which rows each statement may actually touch; these
-- grants only decide which tables are reachable at all.
grant select, insert, update, delete on all tables in schema public to app_api;

-- Identity-sequence usage for the one table that uses an identity column.
grant usage, select on all sequences in schema public to app_api;

-- -----------------------------------------------------------------------------
-- Column-level lockout of the Gmail token
--
-- This is what makes "restrict access to tokens to the email-sending
-- component" (section 4.E) a privilege boundary rather than a code review
-- convention. Even a missed authorization check in the API cannot read or
-- rewrite the four ciphertext columns: PostgreSQL refuses the statement. The
-- API stores a token only through app.store_email_account_token.
--
-- Note on mechanics: a column-level REVOKE does nothing while the role still
-- holds the table-level privilege, because table-level SELECT implies every
-- column. The table-level grant must therefore be dropped first and replaced
-- with an explicit column list. The verification block at the foot of this
-- file asserts the result, so getting this wrong fails the migration.
--
-- A future migration that runs "grant select on all tables in schema public
-- to app_api" would silently undo this. Grant new tables individually.
-- -----------------------------------------------------------------------------
revoke select, insert, update on email_accounts from app_api;

grant select (
  id, organization_id, provider, email_address, display_name,
  granted_scopes, connection_status, status_detail,
  connected_at, last_successful_send_at, last_error_at, disconnected_at,
  daily_send_limit, is_default, created_at, updated_at
) on email_accounts to app_api;

-- Creating the account row is allowed; it starts as not_connected and the
-- CHECK constraints in 0005 make a tokenless 'connected' row unrepresentable.
grant insert (
  id, organization_id, provider, email_address, display_name,
  connection_status, status_detail, daily_send_limit, is_default
) on email_accounts to app_api;

-- Renaming the sender or adjusting its send limit is ordinary administration.
-- Touching the ciphertext or the key version is not.
grant update (
  display_name, connection_status, status_detail,
  daily_send_limit, is_default, last_error_at, disconnected_at, updated_at
) on email_accounts to app_api;

-- -----------------------------------------------------------------------------
-- Tables the API must never write directly
-- -----------------------------------------------------------------------------

-- Append-only. The triggers already reject UPDATE and DELETE; removing the
-- privilege means the attempt fails before a trigger has to.
revoke update, delete on audit_logs from app_api;
revoke update, delete on email_logs from app_api;
revoke update, delete on lease_approvals from app_api;

-- Rate limiting is reached only through app.consume_rate_limit, so the API
-- cannot read other tenants' counters or reset its own.
revoke all on rate_limit_counters from app_api;

-- -----------------------------------------------------------------------------
-- Functions the API may call
-- -----------------------------------------------------------------------------
grant execute on function app.current_auth_user_id() to app_api;
grant execute on function app.current_user_id() to app_api;
grant execute on function app.is_org_member(uuid) to app_api;
grant execute on function app.has_org_role(uuid, app.org_role[]) to app_api;
grant execute on function app.can_write_org(uuid) to app_api;
grant execute on function app.can_administer_org(uuid) to app_api;
grant execute on function app.consume_rate_limit(text, integer, integer, integer) to app_api;

-- OAuth connect and disconnect. Both re-check administrator rights
-- internally, because being SECURITY DEFINER they bypass RLS.
grant execute on function
  app.store_email_account_token(uuid, bytea, bytea, bytea, integer, text[]) to app_api;
grant execute on function
  app.disconnect_email_account(uuid, app.email_connection_status, text) to app_api;

-- Deliberately NOT granted to app_api:
--   app.read_email_account_token          only the sending component reads tokens
--   app.claim_jobs / app.claim_due_notifications
--   app.complete_job / app.fail_job
--   app.get_notification_context
--   app.record_notification_result
--   app.email_accounts_pending_key_rotation
-- The API has no business claiming queue work or decrypting a credential.

-- =============================================================================
-- app_worker
--
-- No table privileges at all -- not even SELECT. Everything the worker does
-- goes through the functions below, each of which verifies that the worker
-- holds the claim on the row it is acting upon.
--
-- As later phases add worker responsibilities (document validation and OCR in
-- phase 2, schedule generation in phase 3), they must arrive as new reviewed
-- app.* functions, NOT as table grants added to this role. Keeping that rule
-- is what keeps the worker's blast radius small.
-- =============================================================================

grant execute on function
  app.claim_jobs(text, app.job_kind[], integer, integer) to app_worker;
grant execute on function app.complete_job(uuid, text) to app_worker;
grant execute on function
  app.fail_job(uuid, text, text, integer, boolean) to app_worker;

grant execute on function
  app.claim_due_notifications(text, integer, integer) to app_worker;
grant execute on function
  app.get_notification_context(uuid, text) to app_worker;
grant execute on function
  app.filter_suppressed_recipients(uuid, jsonb) to app_worker;
grant execute on function
  app.record_notification_result(
    uuid, text, boolean, text, app.email_error_category, text, boolean
  ) to app_worker;
grant execute on function
  app.pause_rule_on_repeated_failure(uuid, integer, integer) to app_worker;

-- The sending component, and only it, reads the encrypted credential.
grant execute on function app.read_email_account_token(uuid) to app_worker;

-- Key rotation runs as a worker task.
grant execute on function
  app.email_accounts_pending_key_rotation(integer) to app_worker;

-- Worker-side rate limiting, for per-account daily send ceilings.
grant execute on function
  app.consume_rate_limit(text, integer, integer, integer) to app_worker;

-- Health reporting. The view runs with owner rights by design: queue depth
-- across organizations is operational data, and it exposes counts only, never
-- lease or recipient content.
grant select on app.queue_health to app_worker;

-- =============================================================================
-- Defaults for future migrations
--
-- Without these, a table added by a later migration would be unreachable by
-- app_api until someone remembered to grant it. The RLS verification block in
-- 0009 catches the opposite mistake.
-- =============================================================================
alter default privileges in schema public
  grant select, insert, update, delete on tables to app_api;
alter default privileges in schema public
  grant usage, select on sequences to app_api;

-- =============================================================================
-- Verification
-- Fails the migration if either application role has been given a privilege
-- that would defeat the whole design.
-- =============================================================================
do $verify$
declare
  v_role record;
begin
  for v_role in
    select rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole
    from pg_roles
    where rolname in ('app_api', 'app_worker')
  loop
    if v_role.rolsuper then
      raise exception 'Role % must not be a superuser', v_role.rolname
        using errcode = 'insufficient_privilege';
    end if;
    if v_role.rolbypassrls then
      raise exception 'Role % must not have BYPASSRLS; RLS is the isolation boundary',
        v_role.rolname using errcode = 'insufficient_privilege';
    end if;
    if v_role.rolcreaterole then
      raise exception 'Role % must not have CREATEROLE', v_role.rolname
        using errcode = 'insufficient_privilege';
    end if;
  end loop;
end;
$verify$;

-- The worker must hold no table privileges. If a later migration grants it
-- one, this check fails and the reviewer is forced to justify it.
do $verify$
declare
  v_tables text;
begin
  select string_agg(distinct table_name, ', ' order by table_name)
  into v_tables
  from information_schema.role_table_grants
  where grantee = 'app_worker'
    and table_schema = 'public';

  if v_tables is not null then
    raise exception
      'app_worker has direct table privileges on: %. The worker must go through app.* functions.',
      v_tables
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;

-- The API must not be able to read or write any part of the token ciphertext.
do $verify$
declare
  v_column text;
  v_action text;
begin
  foreach v_column in array array[
    'encrypted_refresh_token', 'token_nonce', 'token_auth_tag', 'token_key_version'
  ] loop
    foreach v_action in array array['SELECT', 'INSERT', 'UPDATE'] loop
      if has_column_privilege('app_api', 'email_accounts', v_column, v_action) then
        raise exception
          'app_api holds % on email_accounts.%; token columns must be unreachable by the API',
          v_action, v_column
          using errcode = 'insufficient_privilege';
      end if;
    end loop;
  end loop;
end;
$verify$;

-- The API must not be able to call any worker function. PUBLIC EXECUTE on
-- functions is granted by default in PostgreSQL, so this check is what proves
-- the revoke above actually landed.
do $verify$
declare
  v_signature text;
begin
  foreach v_signature in array array[
    'app.read_email_account_token(uuid)',
    'app.claim_jobs(text, app.job_kind[], integer, integer)',
    'app.claim_due_notifications(text, integer, integer)',
    'app.complete_job(uuid, text)',
    'app.fail_job(uuid, text, text, integer, boolean)',
    'app.get_notification_context(uuid, text)',
    'app.record_notification_result(uuid, text, boolean, text, app.email_error_category, text, boolean)',
    'app.email_accounts_pending_key_rotation(integer)'
  ] loop
    if has_function_privilege('app_api', v_signature, 'EXECUTE') then
      raise exception 'app_api can execute %, which belongs to the worker only', v_signature
        using errcode = 'insufficient_privilege';
    end if;
  end loop;
end;
$verify$;

-- And the worker must not be able to call an API-only function.
do $verify$
begin
  if has_function_privilege(
       'app_worker',
       'app.store_email_account_token(uuid, bytea, bytea, bytea, integer, text[])',
       'EXECUTE') then
    raise exception 'app_worker can store an OAuth token; only the API connect flow may'
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;

-- And the API must still be able to do its job on that table.
do $verify$
begin
  if not has_column_privilege('app_api', 'email_accounts', 'connection_status', 'SELECT') then
    raise exception 'app_api cannot read email_accounts.connection_status; the grant list is wrong'
      using errcode = 'insufficient_privilege';
  end if;
  if not has_table_privilege('app_api', 'email_accounts', 'DELETE') then
    raise exception 'app_api cannot delete an email account row'
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;
