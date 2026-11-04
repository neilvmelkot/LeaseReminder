-- =============================================================================
-- 0018_task_board_defaults.sql
-- The default board, the default task notification rules, and the operator
-- view for stuck events.
--
-- The defaults matter more than they look. A board that arrives empty forces
-- every new user to invent a workflow before they can file their first repair,
-- and the usual outcome is three columns called "todo", "doing", "done" with
-- no categories attached, which makes every rule meaningless. Shipping a
-- sensible board that can then be renamed is the better default.
--
-- The notification rules ship ENABLED but conservative: the ones that reach a
-- tenant are on, because a tenant who reports a leak and hears nothing assumes
-- it was ignored; the escalation ladders are on but bounded; and the noisiest
-- rule, staleness, ships disabled, matching the example settings screen in the
-- brief.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- app.seed_task_board
-- -----------------------------------------------------------------------------
create or replace function app.seed_task_board(p_organization_id uuid)
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Not permitted to seed a board for this organization'
      using errcode = 'insufficient_privilege';
  end if;

  -- Columns. The names are the user's to change; the categories are what
  -- every rule and metric actually reads, which is why each is set here
  -- rather than inferred from the name.
  --
  -- Positions are spaced by the standard step so a new column can be inserted
  -- between any two without renumbering.
  insert into task_columns (
    organization_id, name, category, position, colour,
    is_default_intake, collapsed_by_default, work_in_progress_limit
  )
  values
    (p_organization_id, 'New',         'new',         1024,  '#5c6678', true,  false, null),
    (p_organization_id, 'Assigned',    'assigned',    2048,  '#2f52c8', false, false, null),
    (p_organization_id, 'In Progress', 'in_progress', 3072,  '#b7791f', false, false, 10),
    (p_organization_id, 'Blocked',     'blocked',     4096,  '#c0393a', false, false, null),
    -- Collapsed by default: a column that only grows is noise on a board
    -- people read every morning.
    (p_organization_id, 'Completed',   'completed',   5120,  '#16865b', false, true,  null)
  on conflict (organization_id, name) do nothing;

  -- ---------------------------------------------------------------------------
  -- Event rules
  -- ---------------------------------------------------------------------------

  -- Somebody is given work. The single most important notification in the
  -- module: an assignment nobody is told about is an assignment that does not
  -- happen.
  insert into task_notification_rules (
    organization_id, name, trigger_kind, event_type, channels, enabled
  )
  values (
    p_organization_id, 'Task assigned', 'event', 'task_assigned',
    '{email,in_app}', true
  )
  on conflict do nothing;

  insert into task_notification_rules (
    organization_id, name, trigger_kind, event_type, channels, enabled
  )
  values
    -- A reassignment has to reach the person losing it as well, which the
    -- recipient roles below handle.
    (p_organization_id, 'Task reassigned', 'event', 'task_reassigned',
     '{email,in_app}', true),

    -- Status changes are in-app only by default. On a busy board this fires
    -- many times a day, and emailing all of it is how people start filtering
    -- the sender.
    (p_organization_id, 'Status changed', 'event', 'task_status_changed',
     '{in_app}', true),

    (p_organization_id, 'Comment added', 'event', 'task_comment_added',
     '{email,in_app}', true),

    -- Completion reaches the tenant who reported it, which is the whole point
    -- of recording who reported it.
    (p_organization_id, 'Task completed', 'event', 'task_completed',
     '{email,in_app}', true),

    -- A new report is acknowledged, so a tenant knows it arrived.
    (p_organization_id, 'Task created', 'event', 'task_created',
     '{email,in_app}', true),

    -- Only matters when something becomes urgent, which the priority filter
    -- below narrows it to.
    (p_organization_id, 'Priority raised to urgent', 'event', 'task_priority_changed',
     '{email,in_app}', true)
  on conflict do nothing;

  -- Narrow the priority rule to the case worth an email.
  update task_notification_rules
  set only_priorities = array['urgent', 'high']::app.task_priority[]
  where organization_id = p_organization_id
    and name = 'Priority raised to urgent';

  -- ---------------------------------------------------------------------------
  -- Schedule rules
  -- ---------------------------------------------------------------------------
  insert into task_notification_rules (
    organization_id, name, trigger_kind, schedule_basis, schedule_offset_days,
    repeat_daily, max_repeats, channels, enabled, send_local_time
  )
  values
    (p_organization_id, 'Due tomorrow', 'schedule', 'before_due', 1,
     false, null, '{email,in_app}', true, time '08:00'),

    (p_organization_id, 'Due today', 'schedule', 'on_due', 0,
     false, null, '{email,in_app}', true, time '08:00'),

    -- Overdue escalates daily, bounded at fourteen. Unbounded repetition is
    -- how a forgotten task turns into a hundred identical emails.
    (p_organization_id, 'Overdue', 'schedule', 'after_due', 1,
     true, 14, '{email,in_app}', true, time '08:00'),

    -- The second escalation, three days later, reaching the manager rather
    -- than the assignee.
    (p_organization_id, 'Overdue escalation', 'schedule', 'after_due', 3,
     true, 10, '{email,in_app}', true, time '08:00'),

    -- The noisiest rule, so it ships off. A board with genuine work in
    -- progress triggers this constantly until people tune the window.
    (p_organization_id, 'No activity for 5 days', 'schedule', 'no_activity', 5,
     false, null, '{in_app}', false, time '08:00')
  on conflict do nothing;

  -- ---------------------------------------------------------------------------
  -- Recipients
  --
  -- Roles rather than addresses, so a rule follows a reassignment. The
  -- assignments below encode the distinction the brief draws: a tenant hears
  -- about their own report arriving and being finished, and nothing else; the
  -- internal traffic stays internal.
  -- ---------------------------------------------------------------------------

  -- Assignment notices go to the person given the work, plus the manager for
  -- visibility.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('assignee'), ('property_manager')) as role(value)
  where r.organization_id = p_organization_id
    and r.name in ('Task assigned', 'Task reassigned')
  on conflict do nothing;

  -- Status changes: the people working it and the person who raised it
  -- internally. Not the tenant; they do not need "moved to Blocked".
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('assignee'), ('task_creator')) as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Status changed'
  on conflict do nothing;

  -- Comments reach the participants. The reporting tenant is included, and
  -- the internal-note flag is what keeps a candid note away from them.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('assignee'), ('task_creator'), ('reporting_tenant')) as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Comment added'
  on conflict do nothing;

  -- A new report is acknowledged to the tenant, and announced to the manager.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('reporting_tenant'), ('property_manager')) as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Task created'
  on conflict do nothing;

  -- Completion: the tenant who reported it, the manager, and whoever did it.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('reporting_tenant'), ('property_manager'), ('task_creator'))
    as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Task completed'
  on conflict do nothing;

  -- Urgent priority reaches the manager and the owner.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('assignee'), ('property_manager'), ('property_owner'))
    as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Priority raised to urgent'
  on conflict do nothing;

  -- Due and first-level overdue go to whoever has to act.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, 'assignee'::app.task_recipient_role
  from task_notification_rules r
  where r.organization_id = p_organization_id
    and r.name in ('Due tomorrow', 'Due today', 'Overdue')
  on conflict do nothing;

  -- The second escalation deliberately goes ABOVE the assignee. Escalating to
  -- the same person who has already missed two notices is not an escalation.
  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, role.value::app.task_recipient_role
  from task_notification_rules r
  cross join (values ('property_manager'), ('property_owner')) as role(value)
  where r.organization_id = p_organization_id
    and r.name = 'Overdue escalation'
  on conflict do nothing;

  insert into task_notification_rule_recipients (
    organization_id, task_notification_rule_id, recipient_role
  )
  select p_organization_id, r.id, 'property_manager'::app.task_recipient_role
  from task_notification_rules r
  where r.organization_id = p_organization_id
    and r.name = 'No activity for 5 days'
  on conflict do nothing;
end;
$fn$;

comment on function app.seed_task_board(uuid) is
  'Seeds the default board columns and task notification rules for a new organization.';

grant execute on function app.seed_task_board(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- Fold the board into organization creation
--
-- Replaces the 0012 function so a new workspace arrives with a usable board as
-- well as its recipient groups and email templates. A separate "set up your
-- board" step is a step people skip, and a board with no intake column cannot
-- accept a task at all.
-- -----------------------------------------------------------------------------
create or replace function app.seed_organization_defaults(p_organization_id uuid)
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Not permitted to seed defaults for this organization'
      using errcode = 'insufficient_privilege';
  end if;

  -- Recipient groups, the working-day calendar, and the six published email
  -- templates. Kept in its own function so the two concerns stay separable.
  perform app.seed_rent_defaults(p_organization_id);

  -- The maintenance board and its notification rules.
  perform app.seed_task_board(p_organization_id);
end;
$fn$;

comment on function app.seed_organization_defaults(uuid) is
  'Seeds everything a new organization needs: rent defaults, templates, and the task board.';

-- -----------------------------------------------------------------------------
-- Operator visibility for stuck events
--
-- notification_outbox has RLS with no policies, so it is unreachable by the
-- API role. Administrators still need to see what is stuck, so this exposes
-- counts and error text scoped to their own organization, without opening the
-- table or its payloads.
-- -----------------------------------------------------------------------------
create or replace function app.list_stuck_notification_events(
  p_organization_id uuid,
  p_limit integer default 50
)
returns table (
  outbox_id       bigint,
  aggregate_type  text,
  aggregate_id    uuid,
  event_type      text,
  status          app.outbox_status,
  attempt_count   integer,
  occurred_at     timestamptz,
  last_error_message text
)
language plpgsql
stable
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Not permitted to view notification events for this organization'
      using errcode = 'insufficient_privilege';
  end if;

  return query
  select o.id, o.aggregate_type, o.aggregate_id, o.event_type, o.status,
         o.attempt_count, o.occurred_at, o.last_error_message
  from notification_outbox o
  where o.organization_id = p_organization_id
    and o.status in ('failed', 'dead_letter')
  order by o.occurred_at desc
  limit least(greatest(p_limit, 1), 200);
end;
$fn$;

comment on function app.list_stuck_notification_events(uuid, integer) is
  'Failed and dead-lettered events for one organization. Exposes no payload.';

grant execute on function app.list_stuck_notification_events(uuid, integer) to app_api;

-- -----------------------------------------------------------------------------
-- Board summary, for the column counts the board header shows
--
-- One query rather than one per column. A board with eight columns would
-- otherwise cost eight round trips before anything renders.
-- -----------------------------------------------------------------------------
create or replace function app.task_board_summary(
  p_organization_id uuid,
  -- Null means the portfolio board; a value means one property's board.
  p_property_id uuid default null
)
returns table (
  task_column_id  uuid,
  name            text,
  category        app.task_status_category,
  -- Named column_position rather than position: POSITION is a reserved SQL
  -- function name and cannot be used as a column name in a RETURNS TABLE list.
  column_position numeric,
  colour          text,
  collapsed_by_default boolean,
  work_in_progress_limit integer,
  task_count      bigint,
  overdue_count   bigint,
  urgent_count    bigint
)
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select
    c.id,
    c.name,
    c.category,
    c.position,
    c.colour,
    c.collapsed_by_default,
    c.work_in_progress_limit,
    count(t.id),
    -- Overdue is measured against today in the PROPERTY's zone, not the
    -- server's, so a task in Los Angeles is not called overdue hours early.
    count(t.id) filter (
      where t.due_date is not null
        and t.due_date < (now() at time zone p.timezone)::date
    ),
    count(t.id) filter (where t.priority = 'urgent')
  from task_columns c
  left join tasks t
    on t.task_column_id = c.id
   and (p_property_id is null or t.property_id = p_property_id)
  left join properties p on p.id = t.property_id
  where c.organization_id = p_organization_id
    and app.is_org_member(p_organization_id)
  group by c.id, c.name, c.category, c.position, c.colour,
           c.collapsed_by_default, c.work_in_progress_limit
  order by c.position
$fn$;

comment on function app.task_board_summary(uuid, uuid) is
  'Columns with their counts, in one query. Overdue is measured in each property own zone.';

grant execute on function app.task_board_summary(uuid, uuid) to app_api;

-- =============================================================================
-- Close the default-open door on the functions added in this migration
--
-- PostgreSQL grants EXECUTE on every new function to PUBLIC, and
-- ALTER DEFAULT PRIVILEGES does not suppress it (see the note in 0017). So
-- every migration that adds an app.* function ends with this sweep. It
-- revokes from PUBLIC only, leaving the explicit role grants above intact.
-- =============================================================================
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

-- -----------------------------------------------------------------------------
-- Verification
--
-- No app.* function may be callable by PUBLIC. The allowlist is empty on
-- purpose: every function here either reads organization data, writes an
-- event, or touches a credential, and each caller is granted explicitly.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_leaked text;
begin
  select string_agg(p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
                    ', ' order by p.proname)
  into v_leaked
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'EXECUTE');

  if v_leaked is not null then
    raise exception
      'These app.* functions are callable by PUBLIC: %. Revoke from PUBLIC and grant '
      'explicitly to the roles that need them.', v_leaked
      using errcode = 'insufficient_privilege';
  end if;
end;
$verify$;

-- And the API must still be unable to create a notification for an arbitrary
-- user, or to claim worker queue work.
do $verify$
declare
  v_signature text;
begin
  foreach v_signature in array array[
    'app.create_notification_delivery(uuid, bigint, text, uuid, app.delivery_channel, uuid, citext, app.task_recipient_role, uuid, text, text, text, text)',
    'app.claim_outbox_events(text, integer, integer)',
    'app.complete_outbox_event(bigint, text)',
    'app.sweep_task_schedule_events(integer, integer)',
    'app.claim_pending_email_deliveries(text, integer)',
    'app.record_delivery_result(uuid, boolean, text, app.email_error_category, text)',
    'app.read_email_account_token(uuid)'
  ] loop
    if has_function_privilege('app_api', v_signature, 'EXECUTE') then
      raise exception 'app_api can execute %, which belongs to the worker only', v_signature
        using errcode = 'insufficient_privilege';
    end if;
  end loop;
end;
$verify$;
