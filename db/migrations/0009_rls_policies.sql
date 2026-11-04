-- =============================================================================
-- 0009_rls_policies.sql
-- Row Level Security on every user-owned table, for SELECT, INSERT, UPDATE
-- and DELETE (section 4.B).
--
-- How this works with the rest of the system
--   * Tables are owned by app_owner (the migration role). RLS is enabled but
--     NOT forced, so the SECURITY DEFINER functions in 0007/0008 -- which run
--     as app_owner -- continue to work. That is deliberate: those functions
--     are the narrow, reviewed path the worker uses.
--   * app_api and app_worker are non-owner roles, so every statement they
--     issue is filtered by these policies. Neither is a superuser and neither
--     has BYPASSRLS (0010).
--   * Identity comes from app.current_auth_user_id(), which the API sets
--     transaction-locally from the verified JWT subject. An unauthenticated
--     connection has no identity, and every policy below then matches zero
--     rows -- the safe direction.
--
-- Permission tiers used throughout
--   app.is_org_member        read
--   app.can_write_org        owner, admin, property_manager
--   app.can_administer_org   owner, admin
--
-- Read-only and accounting members can therefore see the portfolio but cannot
-- modify leases, rules, or connected accounts.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- users
-- A member may read their own row, and the rows of people sharing an
-- organization with them (so "approved by" can show a name). Nothing more:
-- the whole user table is never readable.
-- -----------------------------------------------------------------------------
alter table users enable row level security;

create policy users_select_self_or_co_member on users
  for select
  using (
    auth_user_id = app.current_auth_user_id()
    or exists (
      select 1
      from organization_members mine
      join organization_members theirs
        on theirs.organization_id = mine.organization_id
      where mine.user_id = app.current_user_id()
        and theirs.user_id = users.id
    )
  );

-- Provisioning on first sign-in: a user may create only their own row, and
-- only with the auth subject the verified token actually carries.
create policy users_insert_self on users
  for insert
  with check (auth_user_id = app.current_auth_user_id());

create policy users_update_self on users
  for update
  using (auth_user_id = app.current_auth_user_id())
  with check (auth_user_id = app.current_auth_user_id());

-- Deliberately no DELETE policy. Account deletion runs as an operational
-- procedure with a retention decision attached, not as an API call.

-- -----------------------------------------------------------------------------
-- organizations
-- -----------------------------------------------------------------------------
alter table organizations enable row level security;

create policy organizations_select_member on organizations
  for select using (app.is_org_member(id));

-- Creating an organization is allowed for any authenticated user; the API
-- immediately adds them as its owner in the same transaction.
create policy organizations_insert_authenticated on organizations
  for insert
  with check (app.current_user_id() is not null);

create policy organizations_update_admin on organizations
  for update
  using (app.can_administer_org(id))
  with check (app.can_administer_org(id));

-- No DELETE policy: deleting an organization cascades across leases,
-- documents, and logs, so it is an operational procedure.

-- -----------------------------------------------------------------------------
-- organization_members
-- Readable by fellow members; writable only by administrators. The policies
-- call SECURITY DEFINER helpers rather than querying this table inline, which
-- is what keeps them from recursing.
-- -----------------------------------------------------------------------------
alter table organization_members enable row level security;

create policy organization_members_select_member on organization_members
  for select using (app.is_org_member(organization_id));

create policy organization_members_insert_admin on organization_members
  for insert with check (app.can_administer_org(organization_id));

create policy organization_members_update_admin on organization_members
  for update
  using (app.can_administer_org(organization_id))
  with check (app.can_administer_org(organization_id));

create policy organization_members_delete_admin on organization_members
  for delete using (app.can_administer_org(organization_id));

-- -----------------------------------------------------------------------------
-- Bootstrap policy for the very first membership
--
-- A user creating their own organization has no membership in it yet, so
-- can_administer_org is false and the INSERT above would reject their own
-- owner row. This policy permits exactly that one case: inserting a row for
-- YOURSELF, as owner, into an organization that has no members at all.
-- It cannot be used to join an existing organization.
-- -----------------------------------------------------------------------------
create policy organization_members_insert_first_owner on organization_members
  for insert
  with check (
    user_id = app.current_user_id()
    and role = 'owner'
    and not exists (
      select 1
      from organization_members existing
      where existing.organization_id = organization_members.organization_id
    )
  );

-- -----------------------------------------------------------------------------
-- The standard organization-scoped policy set
--
-- Applied by generation rather than by hand so that the tier assigned to each
-- table is visible in one list instead of scattered across a hundred
-- statements. Every table named here carries its own organization_id column
-- (enforced by the composite foreign keys in 0002-0005), so the policy never
-- has to join to find the owner.
-- -----------------------------------------------------------------------------
do $bootstrap$
declare
  -- Portfolio and configuration: read for any member, write for write-tier.
  v_write_tier text[] := array[
    'properties',
    'units',
    'tenants',
    'leases',
    'lease_tenants',
    'lease_documents',
    'lease_document_pages',
    'lease_extractions',
    'lease_extracted_fields',
    'lease_extraction_issues',
    'holiday_calendars',
    'holiday_dates',
    'rent_schedules',
    'rent_schedule_exceptions',
    'rent_escalations',
    'additional_charges',
    'rent_occurrences',
    'recipients',
    'recipient_groups',
    'recipient_group_members',
    'email_suppressions',
    'email_templates',
    'notification_rules',
    'notification_rule_recipients',
    'scheduled_notifications',
    'jobs'
  ];
  v_table text;
begin
  foreach v_table in array v_write_tier loop
    execute format('alter table %I enable row level security', v_table);

    execute format(
      'create policy %I on %I for select using (app.is_org_member(organization_id))',
      v_table || '_select_member', v_table
    );

    execute format(
      'create policy %I on %I for insert with check (app.can_write_org(organization_id))',
      v_table || '_insert_writer', v_table
    );

    -- Both USING and WITH CHECK are given the same predicate. USING decides
    -- which rows may be updated; WITH CHECK decides what they may become.
    -- Without the second clause a member could move a row into another
    -- organization by rewriting organization_id.
    execute format(
      'create policy %I on %I for update using (app.can_write_org(organization_id)) '
      || 'with check (app.can_write_org(organization_id))',
      v_table || '_update_writer', v_table
    );

    execute format(
      'create policy %I on %I for delete using (app.can_write_org(organization_id))',
      v_table || '_delete_writer', v_table
    );
  end loop;
end;
$bootstrap$;

-- -----------------------------------------------------------------------------
-- email_accounts
-- Administrator tier: a connected sender is a credential, not portfolio data.
-- The ciphertext columns are additionally unreadable at the privilege level
-- (0010), so even a member who can SELECT this row gets no key material.
-- -----------------------------------------------------------------------------
alter table email_accounts enable row level security;

create policy email_accounts_select_member on email_accounts
  for select using (app.is_org_member(organization_id));

create policy email_accounts_insert_admin on email_accounts
  for insert with check (app.can_administer_org(organization_id));

create policy email_accounts_update_admin on email_accounts
  for update
  using (app.can_administer_org(organization_id))
  with check (app.can_administer_org(organization_id));

create policy email_accounts_delete_admin on email_accounts
  for delete using (app.can_administer_org(organization_id));

-- -----------------------------------------------------------------------------
-- lease_approvals
-- Append-only (trigger in 0003). Any member may read the history; only the
-- write tier may add a decision, and only in their own name -- the decided_by
-- column cannot be attributed to someone else.
-- -----------------------------------------------------------------------------
alter table lease_approvals enable row level security;

create policy lease_approvals_select_member on lease_approvals
  for select using (app.is_org_member(organization_id));

create policy lease_approvals_insert_writer on lease_approvals
  for insert
  with check (
    app.can_write_org(organization_id)
    and decided_by_user_id = app.current_user_id()
  );

-- No UPDATE or DELETE policy, reinforcing the append-only trigger.

-- -----------------------------------------------------------------------------
-- email_logs
-- Append-only (trigger in 0005). Readable by members for the email history
-- views. Rows are normally written by app.record_notification_result, which
-- runs as the owner; the INSERT policy exists for test sends issued through
-- the API.
-- -----------------------------------------------------------------------------
alter table email_logs enable row level security;

create policy email_logs_select_member on email_logs
  for select using (app.is_org_member(organization_id));

create policy email_logs_insert_writer on email_logs
  for insert with check (app.can_write_org(organization_id));

-- -----------------------------------------------------------------------------
-- audit_logs
-- Append-only (trigger in 0006). Reading the audit trail is an administrator
-- action: it reveals who did what, which is not ordinary portfolio data.
-- Any member may append, because every member generates auditable events.
-- -----------------------------------------------------------------------------
alter table audit_logs enable row level security;

create policy audit_logs_select_admin on audit_logs
  for select using (app.can_administer_org(organization_id));

create policy audit_logs_insert_member on audit_logs
  for insert
  with check (
    app.is_org_member(organization_id)
    -- A user-attributed audit entry must name the actual caller. A member
    -- cannot write history in someone else's name.
    and (actor_kind <> 'user' or actor_user_id = app.current_user_id())
  );

-- -----------------------------------------------------------------------------
-- rate_limit_counters
-- Not organization-owned and never user-readable. Reached only through
-- app.consume_rate_limit, so it carries RLS with no policies at all: that
-- combination denies every non-owner role outright.
-- -----------------------------------------------------------------------------
alter table rate_limit_counters enable row level security;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Fails the migration if any table in the public schema was added without
-- RLS. This is the check that stops a new table from quietly shipping with
-- no isolation, which is the most likely way this protection erodes.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_missing text;
begin
  select string_agg(c.relname, ', ' order by c.relname)
  into v_missing
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  where n.nspname = 'public'
    and c.relkind = 'r'
    -- The migration ledger is created by the migration runner, owned by the
    -- migration role, and not reachable by app_api or app_worker.
    and c.relname <> 'schema_migrations'
    and not c.relrowsecurity;

  if v_missing is not null then
    raise exception
      'Row Level Security is not enabled on: %. Every table in public must enable RLS.',
      v_missing
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;

-- And fails if an RLS-enabled table has no policies while being reachable by
-- the application roles. rate_limit_counters is the one intentional
-- exception: deny-all is its correct configuration.
do $verify$
declare
  v_missing text;
begin
  select string_agg(c.relname, ', ' order by c.relname)
  into v_missing
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
  where n.nspname = 'public'
    and c.relkind = 'r'
    and c.relrowsecurity
    and c.relname not in ('rate_limit_counters', 'schema_migrations')
    and not exists (
      select 1 from pg_policy p where p.polrelid = c.oid
    );

  if v_missing is not null then
    raise exception
      'These tables enable RLS but define no policy, so they are unreachable: %',
      v_missing
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;
