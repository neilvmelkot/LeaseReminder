-- =============================================================================
-- 0017_maintenance_rls_and_grants.sql
-- Row Level Security and privileges for the maintenance module.
--
-- Grants are made PER TABLE here, never with "on all tables in schema public".
-- That wholesale form would re-grant table-level SELECT on email_accounts and
-- silently undo the column revoke in 0010 that keeps the Gmail token out of
-- the API's reach. The verification block at the end of this file re-asserts
-- that it is still in force, so the mistake cannot pass unnoticed.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- The standard organization-scoped policy set
--
-- Applied by generation so the tier chosen for each table is visible as one
-- list. Every table named carries its own organization_id, enforced by the
-- composite foreign keys in 0013 and 0014, so a policy never has to join to
-- find the owner.
-- -----------------------------------------------------------------------------
do $policies$
declare
  -- Read for any member, write for the write tier. Operational data: a
  -- property manager creates and moves tasks as their daily work.
  v_write_tier text[] := array[
    'vendors',
    'tasks',
    'task_assignees',
    'task_comments',
    'task_attachments',
    'task_recipients'
  ];

  -- Read for any member, write for administrators. Board structure and
  -- notification rules are configuration: one person changing a column
  -- category changes what every rule means for everybody.
  v_admin_tier text[] := array[
    'task_columns',
    'task_notification_rules',
    'task_notification_rule_recipients'
  ];

  v_table text;
begin
  foreach v_table in array v_write_tier loop
    execute format('alter table %I enable row level security', v_table);

    execute format(
      'create policy %I on %I for select using (app.is_org_member(organization_id))',
      v_table || '_select_member', v_table);

    execute format(
      'create policy %I on %I for insert with check (app.can_write_org(organization_id))',
      v_table || '_insert_writer', v_table);

    -- USING decides which rows may be updated; WITH CHECK decides what they
    -- may become. Without the second clause a member could move a row into
    -- another organization by rewriting organization_id.
    execute format(
      'create policy %I on %I for update using (app.can_write_org(organization_id)) '
      || 'with check (app.can_write_org(organization_id))',
      v_table || '_update_writer', v_table);

    execute format(
      'create policy %I on %I for delete using (app.can_write_org(organization_id))',
      v_table || '_delete_writer', v_table);
  end loop;

  foreach v_table in array v_admin_tier loop
    execute format('alter table %I enable row level security', v_table);

    execute format(
      'create policy %I on %I for select using (app.is_org_member(organization_id))',
      v_table || '_select_member', v_table);

    execute format(
      'create policy %I on %I for insert with check (app.can_administer_org(organization_id))',
      v_table || '_insert_admin', v_table);

    execute format(
      'create policy %I on %I for update using (app.can_administer_org(organization_id)) '
      || 'with check (app.can_administer_org(organization_id))',
      v_table || '_update_admin', v_table);

    execute format(
      'create policy %I on %I for delete using (app.can_administer_org(organization_id))',
      v_table || '_delete_admin', v_table);
  end loop;
end;
$policies$;

-- -----------------------------------------------------------------------------
-- task_activity
--
-- Append-only, and readable by any member. Writes happen only through the
-- SECURITY DEFINER functions in 0015, which is why there is no INSERT policy:
-- an activity entry must always accompany the change it describes, and a
-- hand-written insert would let the two come apart.
-- -----------------------------------------------------------------------------
alter table task_activity enable row level security;

create policy task_activity_select_member on task_activity
  for select using (app.is_org_member(organization_id));

-- -----------------------------------------------------------------------------
-- notification_outbox
--
-- RLS enabled with no policy at all, which denies every non-owner role
-- outright. This is internal plumbing: the API writes to it only through
-- app.record_outbox_event and the worker reads it only through
-- app.claim_outbox_events, both of which run as the owner.
--
-- Administrators do need to see stuck events, so 0018 exposes a read-only
-- projection rather than opening the table.
-- -----------------------------------------------------------------------------
alter table notification_outbox enable row level security;

-- -----------------------------------------------------------------------------
-- notification_deliveries
--
-- Two different things live in this table and they need different visibility:
--
--   in_app  is somebody's personal notification. Readable only by that
--           person, never by a colleague and never by an administrator. An
--           admin who could read every in-app notification could reconstruct
--           who was told what and when, which is not something the role needs.
--   email   is the organization's send log, and section 6.K wants it visible
--           for troubleshooting. Scoped to administrators.
-- -----------------------------------------------------------------------------
alter table notification_deliveries enable row level security;

create policy notification_deliveries_select_own_or_admin on notification_deliveries
  for select
  using (
    (channel = 'in_app' and recipient_user_id = app.current_user_id())
    or (channel = 'email' and app.can_administer_org(organization_id))
  );

-- Read and dismissed state is the only thing a user may change, and only on
-- their own rows. The immutability trigger in 0014 blocks everything else.
create policy notification_deliveries_update_own on notification_deliveries
  for update
  using (channel = 'in_app' and recipient_user_id = app.current_user_id())
  with check (channel = 'in_app' and recipient_user_id = app.current_user_id());

-- No INSERT policy: deliveries are created only by
-- app.create_notification_delivery, so a user cannot fabricate a notification
-- for somebody else.

-- =============================================================================
-- Privileges
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Close the default-open door on the functions added since 0010
--
-- PostgreSQL grants EXECUTE on every new function to PUBLIC. 0010 revoked that
-- for the functions existing at the time and also set
--
--     alter default privileges in schema app revoke execute on functions from public;
--
-- expecting it to cover functions added later. It does NOT: the PUBLIC
-- EXECUTE default for functions is implicit rather than a stored grant, so
-- that statement recorded nothing in pg_default_acl and had no effect. Every
-- function added in 0013 to 0016 came back with PUBLIC EXECUTE, including
-- app.create_notification_delivery, which can address a notification to any
-- user, and app.claim_outbox_events.
--
-- The verification block at the foot of this file is what caught it. The
-- reliable mechanism is this explicit sweep, so the pattern for ANY migration
-- that adds an app.* function is: revoke from PUBLIC first, then grant to the
-- specific roles that need it.
-- -----------------------------------------------------------------------------
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

-- -----------------------------------------------------------------------------
-- app_api: the new tables, filtered by the policies above.
-- -----------------------------------------------------------------------------
grant select, insert, update, delete on
  vendors,
  task_columns,
  tasks,
  task_assignees,
  task_comments,
  task_attachments,
  task_recipients,
  task_notification_rules,
  task_notification_rule_recipients
to app_api;

-- -----------------------------------------------------------------------------
-- The tables the API must NOT write, and why a REVOKE is what does it
--
-- 0010 set
--
--     alter default privileges in schema public
--       grant select, insert, update, delete on tables to app_api;
--
-- so a table added by a later migration is reachable without somebody
-- remembering to grant it. For TABLES that default does take effect, unlike
-- the equivalent for functions noted above. So every table created in 0013
-- and 0014 arrived with all four privileges already granted, and a `grant
-- select` here would have been a no-op that read as though it were the whole
-- story. The privileges that matter are the ones taken away.
--
-- The verification block at the foot of this file is what caught the omission.
-- -----------------------------------------------------------------------------

-- The append-only, user-facing timeline. Written only by the functions in
-- 0015, because an entry must always accompany the change it describes.
grant select on task_activity to app_api;
revoke insert, update, delete on task_activity from app_api;

-- Internal plumbing. The API raises events only through the task functions, so
-- that an event can never describe something that did not happen. RLS already
-- denies every row; this denies the privilege as well.
revoke all on notification_outbox from app_api;

-- The notification centre. SELECT and UPDATE only, with the RLS policy
-- confining the UPDATE to read and dismissed state on the caller's own rows.
--
-- The INSERT revoke is the important one: without it the API could write a
-- notification addressed to any user in the organization, which is both a
-- spoofing surface and a way around every consent and suppression check the
-- rule engine performs.
grant select, update on notification_deliveries to app_api;
revoke insert, delete on notification_deliveries from app_api;

grant usage, select on all sequences in schema public to app_api;

-- -----------------------------------------------------------------------------
-- Functions the API may call
-- -----------------------------------------------------------------------------
grant execute on function app.position_step() to app_api;
grant execute on function app.position_between(numeric, numeric) to app_api;

grant execute on function app.create_task(
  uuid, uuid, text, uuid, text, uuid, text, app.task_priority, app.task_category,
  app.task_source, date, uuid, numeric, uuid, uuid, integer, uuid) to app_api;

grant execute on function app.move_task(uuid, uuid, uuid, uuid, uuid, text) to app_api;
grant execute on function app.assign_task(uuid, uuid, uuid, boolean, uuid, text) to app_api;
grant execute on function app.add_task_comment(uuid, text, boolean, uuid, text) to app_api;

grant execute on function app.update_task_fields(
  uuid, uuid, text, text, text, app.task_priority, app.task_category,
  date, boolean, numeric, numeric, uuid) to app_api;

-- The notification centre. Each of these reads the caller's identity rather
-- than taking a user parameter, so there is no argument through which one
-- user could reach another's inbox.
grant execute on function app.list_notifications(boolean, integer, timestamptz) to app_api;
grant execute on function app.unread_notification_count() to app_api;
grant execute on function app.mark_notifications_read(uuid[]) to app_api;
grant execute on function app.dismiss_notification(uuid) to app_api;

-- Trigger functions, needed by any role whose statements fire them.
grant execute on function app.sync_task_status_category() to app_api;
grant execute on function app.assign_task_reference_number() to app_api;
grant execute on function app.guard_task_parent_cycle() to app_api;
grant execute on function app.touch_task_last_activity() to app_api;
grant execute on function app.protect_delivery_content() to app_api;

-- Deliberately NOT granted to app_api:
--   app.record_outbox_event              events accompany a change, via the
--                                        functions above; a hand-raised event
--                                        could describe something that never
--                                        happened
--   app.claim_outbox_events              worker only
--   app.complete_outbox_event / fail     worker only
--   app.create_notification_delivery     worker only; otherwise the API could
--                                        write a notification to any user
--   app.resolve_task_notification_recipients
--   app.matching_task_notification_rules
--   app.sweep_task_schedule_events
--   app.claim_pending_email_deliveries
--   app.record_delivery_result

-- -----------------------------------------------------------------------------
-- app_worker: still no table privileges at all.
--
-- Everything the worker does goes through these functions. The assertion at
-- the end of 0010 fails the migration if a table grant is ever added to this
-- role, and that check still runs; the block below re-asserts it after this
-- migration's grants.
-- -----------------------------------------------------------------------------
grant execute on function
  app.claim_outbox_events(text, integer, integer) to app_worker;
grant execute on function app.complete_outbox_event(bigint, text) to app_worker;
grant execute on function
  app.fail_outbox_event(bigint, text, text, boolean) to app_worker;

grant execute on function
  app.resolve_task_notification_recipients(uuid, uuid, uuid, boolean) to app_worker;
grant execute on function
  app.matching_task_notification_rules(uuid, text) to app_worker;

grant execute on function app.create_notification_delivery(
  uuid, bigint, text, uuid, app.delivery_channel, uuid, citext,
  app.task_recipient_role, uuid, text, text, text, text) to app_worker;

grant execute on function
  app.sweep_task_schedule_events(integer, integer) to app_worker;
grant execute on function
  app.claim_pending_email_deliveries(text, integer) to app_worker;
grant execute on function app.record_delivery_result(
  uuid, boolean, text, app.email_error_category, text) to app_worker;

-- The worker raises time-based events, so it needs the outbox writer.
grant execute on function app.record_outbox_event(
  uuid, text, uuid, text, jsonb, uuid, text, text) to app_worker;

-- =============================================================================
-- Verification
-- =============================================================================

-- Every table in public must still have RLS.
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

-- The worker must still hold no table privileges.
do $verify$
declare
  v_tables text;
begin
  select string_agg(distinct table_name, ', ' order by table_name)
  into v_tables
  from information_schema.role_table_grants
  where grantee = 'app_worker' and table_schema = 'public';

  if v_tables is not null then
    raise exception
      'app_worker has direct table privileges on: %. The worker must go through app.* functions.',
      v_tables
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;

-- The Gmail token must still be unreachable by the API. This is the check that
-- catches a wholesale "grant on all tables" added by a later migration.
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
          'app_api holds % on email_accounts.%. A wholesale table grant has undone the '
          '0010 column revoke.', v_action, v_column
          using errcode = 'insufficient_privilege';
      end if;
    end loop;
  end loop;
end;
$verify$;

-- The API must not be able to write a notification to an arbitrary user.
do $verify$
begin
  if has_function_privilege(
       'app_api',
       'app.create_notification_delivery(uuid, bigint, text, uuid, app.delivery_channel, '
       || 'uuid, citext, app.task_recipient_role, uuid, text, text, text, text)',
       'EXECUTE') then
    raise exception
      'app_api can create a notification delivery directly, which would let it write '
      'a notification addressed to any user. That belongs to the worker.'
      using errcode = 'insufficient_privilege';
  end if;

  if has_table_privilege('app_api', 'notification_deliveries', 'INSERT') then
    raise exception
      'app_api can INSERT into notification_deliveries; notifications must be created '
      'only by the worker through app.create_notification_delivery.'
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;
