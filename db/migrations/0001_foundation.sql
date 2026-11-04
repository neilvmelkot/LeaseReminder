-- =============================================================================
-- 0001_foundation.sql
-- Extensions, the `app` helper schema, enum types, and shared triggers.
--
-- Design notes
--  * Every state machine in section 13.E gets its OWN enum type. Unrelated
--    workflows are never collapsed into a single generic "status".
--  * RLS helper functions are SECURITY DEFINER with a pinned search_path so
--    that membership lookups cannot recurse through the policies that call
--    them, and cannot be hijacked by a mutable search_path.
-- =============================================================================

create extension if not exists "pgcrypto";   -- gen_random_uuid()
create extension if not exists "citext";     -- case-insensitive email storage
create extension if not exists "btree_gist"; -- exclusion constraints on ranges

create schema if not exists app;
comment on schema app is
  'Internal helper functions for row-level security and auditing. Not a data schema.';

-- -----------------------------------------------------------------------------
-- Enum types
-- -----------------------------------------------------------------------------

-- Membership ------------------------------------------------------------------
create type app.org_role as enum (
  'owner', 'admin', 'property_manager', 'accounting', 'read_only'
);

-- Portfolio -------------------------------------------------------------------
create type app.property_type as enum (
  'single_family', 'multi_family', 'condo', 'apartment',
  'townhouse', 'commercial', 'other'
);

create type app.unit_status as enum (
  'vacant', 'occupied', 'unavailable', 'maintenance'
);

create type app.tenant_status as enum (
  'prospective', 'active', 'past', 'archived'
);

-- Lease: three independent state machines (13.E.2, 13.E.3) --------------------
create type app.lease_lifecycle_status as enum (
  'draft', 'upcoming', 'active', 'expired', 'terminated', 'archived'
);

create type app.lease_approval_status as enum (
  'pending_review', 'approved', 'rejected'
);

-- Documents (13.E.1) ----------------------------------------------------------
create type app.document_processing_status as enum (
  'uploaded', 'validating', 'quarantined', 'queued',
  'extracting', 'review_ready', 'failed'
);

create type app.malware_scan_status as enum (
  'pending', 'clean', 'infected', 'skipped', 'error'
);

-- Extraction ------------------------------------------------------------------
create type app.extraction_review_status as enum (
  'pending', 'accepted', 'corrected', 'rejected'
);

create type app.field_review_status as enum (
  'pending', 'accepted', 'corrected', 'rejected', 'flagged'
);

-- Where a value came from. Keeps stated facts distinct from derived ones (13.D).
create type app.value_provenance as enum (
  'stated_in_document', 'inferred_by_calculation', 'user_entered', 'default_applied'
);

-- Rent scheduling -------------------------------------------------------------
create type app.rent_frequency as enum (
  'monthly', 'weekly', 'biweekly', 'every_30_days', 'one_time', 'custom'
);

create type app.due_day_rule as enum (
  'first_of_month', 'last_of_month', 'specific_day',
  'first_business_day', 'custom_sequence'
);

-- What to do when a monthly due day does not exist in a short month.
create type app.short_month_fallback as enum (
  'last_day_of_month', 'skip_month', 'manual_exception'
);

-- Weekend/holiday handling is explicit and opt-in. Never assumed.
create type app.business_day_shift as enum (
  'none', 'next_business_day', 'previous_business_day'
);

-- Notifications ---------------------------------------------------------------
create type app.notification_event_type as enum (
  'rent_due', 'grace_period_end', 'late_fee_trigger',
  'lease_expiration', 'renewal_notice_deadline',
  'rent_increase', 'early_termination_deadline', 'custom'
);

create type app.notification_trigger_type as enum ('before', 'on', 'after');

create type app.offset_unit as enum ('minutes', 'hours', 'days');

create type app.rule_inheritance_mode as enum (
  'inherited',   -- uses an ancestor rule verbatim
  'overridden',  -- replaces a specific ancestor rule
  'additional',  -- extra rule at this level, ancestors untouched
  'disabled'     -- explicitly switches an inherited rule off
);

create type app.recipient_kind as enum ('to', 'cc', 'bcc');

create type app.notification_automation_status as enum (
  'not_configured', 'pending_activation', 'active', 'paused', 'error'
);

-- Email -----------------------------------------------------------------------
create type app.email_connection_status as enum (
  'not_connected', 'connected', 'reauthorization_required', 'revoked', 'error'
);

create type app.email_template_status as enum ('draft', 'published', 'archived');

create type app.scheduled_notification_status as enum (
  'scheduled',   -- persisted, waiting for its send instant
  'claimed',     -- atomically claimed by a worker
  'sent',        -- provider accepted the send request
  'failed',      -- attempt failed, may retry
  'dead_letter', -- retries exhausted, or a permanent error
  'skipped',     -- deliberately not sent (quiet hours, suppression)
  'cancelled',   -- rule disabled or lease automation paused
  'superseded'   -- replaced after a lease or rule change
);

create type app.email_send_status as enum ('accepted', 'failed', 'suppressed');

create type app.email_error_category as enum (
  'none', 'authorization', 'quota', 'invalid_recipient',
  'transient', 'permanent', 'configuration', 'unknown'
);

-- Background jobs -------------------------------------------------------------
create type app.job_kind as enum (
  'document_validate', 'document_extract_text', 'document_ocr',
  'document_ai_extract', 'schedule_regenerate',
  'notification_dispatch', 'cleanup'
);

create type app.job_status as enum (
  'queued', 'claimed', 'succeeded', 'failed', 'dead_letter', 'cancelled'
);

-- -----------------------------------------------------------------------------
-- Identity helpers
--
-- The API opens a transaction and runs
--     select set_config('app.auth_user_id', <sub claim>, true)
-- before touching any table. The trailing `true` makes the setting
-- transaction-local, so a pooled connection can never leak one request
-- identity into the next.
--
-- The fallback to request.jwt.claims keeps these policies correct if the same
-- tables are ever read through Supabase PostgREST.
-- -----------------------------------------------------------------------------

create or replace function app.current_auth_user_id()
returns uuid
language plpgsql
stable
set search_path = pg_catalog, public
as $fn$
declare
  v_raw text;
begin
  v_raw := nullif(current_setting('app.auth_user_id', true), '');

  if v_raw is null then
    begin
      v_raw := nullif(current_setting('request.jwt.claims', true)::jsonb ->> 'sub', '');
    exception when others then
      v_raw := null;
    end;
  end if;

  if v_raw is null then
    return null;
  end if;

  begin
    return v_raw::uuid;
  exception when others then
    -- A malformed claim means "no identity". It must never match a row.
    return null;
  end;
end;
$fn$;

comment on function app.current_auth_user_id() is
  'Identity-provider subject (auth user id) for the current transaction, or NULL.';

-- -----------------------------------------------------------------------------
-- Shared triggers
-- -----------------------------------------------------------------------------

create or replace function app.touch_updated_at()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  new.updated_at := now();
  return new;
end;
$fn$;

comment on function app.touch_updated_at() is
  'BEFORE UPDATE trigger that maintains an updated_at column.';

-- Makes a table append-only: audit logs, email logs, approval decisions.
--
-- The rule is not quite "block everything", because that would make a lawful
-- deletion request impossible to honour. Section 11.7 requires a defined
-- answer for deletion requests and legal retention, and deleting an
-- organization has to be able to cascade to its logs. A trigger that refused
-- the owner as well would mean the only way to honour such a request was to
-- disable the trigger, which is a worse procedure than permitting the
-- deletion in the first place.
--
-- So:
--   * UPDATE is refused for everyone. History is never amended, by anyone.
--     Correcting a wrong entry means appending a new one.
--   * DELETE is refused for the application roles, which is what makes the
--     table append-only as far as the running system is concerned.
--   * DELETE is permitted for the table OWNER, so a reviewed retention or
--     erasure procedure can run. The owner can already drop the table, so
--     this concedes nothing that was actually protected; it just makes the
--     supported path an ordinary DELETE instead of a trigger-disabling script.
create or replace function app.deny_mutation()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
declare
  v_owner name;
begin
  if tg_op = 'UPDATE' then
    raise exception
      'Table %.% is append-only; UPDATE is never permitted. Append a correcting row instead.',
      tg_table_schema, tg_table_name
      using errcode = 'insufficient_privilege';
  end if;

  select pg_get_userbyid(c.relowner)
  into v_owner
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  where n.nspname = tg_table_schema
    and c.relname = tg_table_name;

  -- pg_has_role covers membership, so a role granted the owner role counts.
  if v_owner is not null and pg_has_role(current_user, v_owner, 'USAGE') then
    return old;
  end if;

  raise exception
    'Table %.% is append-only; DELETE by % is not permitted. '
    'Deletion is a reviewed retention operation run as the table owner.',
    tg_table_schema, tg_table_name, current_user
    using errcode = 'insufficient_privilege';
end;
$fn$;

comment on function app.deny_mutation() is
  'Trigger enforcing append-only tables: UPDATE is refused outright, DELETE only for the table owner.';
