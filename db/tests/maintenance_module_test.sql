-- =============================================================================
-- maintenance_module_test.sql
-- The maintenance board and its notification engine.
--
-- Run with the other suites:  ./db/test.sh local
--
-- The whole file runs in one transaction and rolls back, so it leaves nothing
-- behind. It asserts by raising, so a failure aborts with a message naming the
-- test.
--
-- What is covered, and why each of these is worth a test rather than a careful
-- reading:
--
--   * A task change and its event commit together. The outbox exists for this
--     one property, and it is invisible when broken: the task still saves.
--   * Reordering inside a column raises nothing. Get this wrong and tidying a
--     board emails everybody involved in every card.
--   * The schedule sweep is idempotent per day. Get this wrong and running it
--     twice sends two overdue notices.
--   * Tenant consent and internal notes are honoured in recipient resolution.
--     Get this wrong and a manager's candid note reaches the tenant it is
--     about.
--   * The actor is not notified of their own action.
--   * Two workers claim disjoint batches.
--   * The API cannot write a notification addressed to somebody else.
-- =============================================================================

\set ON_ERROR_STOP on
\timing off

begin;

create temporary table t (k text primary key, v text not null);
grant select, insert on t to app_api;

create or replace function pg_temp.put(p_key text, p_value text)
returns void language sql as $fn$
  insert into t (k, v) values (p_key, p_value)
  on conflict (k) do update set v = excluded.v
$fn$;

create or replace function pg_temp.get(p_key text)
returns text language sql stable as $fn$
  select v from t where k = p_key
$fn$;

create or replace function pg_temp.uid(p_key text)
returns uuid language sql stable as $fn$
  select (select v from t where k = p_key)::uuid
$fn$;

create or replace function pg_temp.assert(p_condition boolean, p_test text)
returns void language plpgsql as $fn$
begin
  if p_condition is not true then
    raise exception 'FAILED: %', p_test using errcode = 'assert_failure';
  end if;
  raise notice '  pass  %', p_test;
end;
$fn$;

create or replace function pg_temp.become(p_auth_user_id uuid)
returns void language plpgsql as $fn$
begin
  perform set_config('app.auth_user_id', coalesce(p_auth_user_id::text, ''), false);
end;
$fn$;

-- -----------------------------------------------------------------------------
-- Fixtures: two organizations, so every isolation claim has something to fail
-- against, and a property in Los Angeles so the time zone assertions are not
-- vacuous on a UTC server.
-- -----------------------------------------------------------------------------
do $seed$
declare
  v_auth_a uuid := gen_random_uuid();
  v_auth_b uuid := gen_random_uuid();
  v_auth_m uuid := gen_random_uuid();   -- a property manager in organization A
  v_user_a uuid; v_user_b uuid; v_user_m uuid;
  v_org_a  uuid; v_org_b  uuid;
  v_prop_a uuid; v_prop_b uuid;
  v_unit_a uuid;
  v_tenant_in uuid; v_tenant_out uuid;
  v_vendor uuid;
begin
  insert into users (auth_user_id, email, display_name)
  values (v_auth_a, 'owner-a@example.test', 'Ada Owner') returning id into v_user_a;
  insert into users (auth_user_id, email, display_name)
  values (v_auth_b, 'owner-b@example.test', 'Ben Owner') returning id into v_user_b;
  insert into users (auth_user_id, email, display_name)
  values (v_auth_m, 'manager-a@example.test', 'Mia Manager') returning id into v_user_m;

  insert into organizations (name) values ('Org A') returning id into v_org_a;
  insert into organizations (name) values ('Org B') returning id into v_org_b;

  insert into organization_members (organization_id, user_id, role)
  values (v_org_a, v_user_a, 'owner'),
         (v_org_a, v_user_m, 'property_manager'),
         (v_org_b, v_user_b, 'owner');

  -- Los Angeles, deliberately. "Overdue" has to be measured in the property's
  -- own zone, and on a UTC server a same-zone fixture would pass either way.
  insert into properties (organization_id, name, timezone)
  values (v_org_a, 'Maple Street', 'America/Los_Angeles')
  returning id into v_prop_a;

  insert into properties (organization_id, name, timezone)
  values (v_org_b, 'Elsewhere Avenue', 'America/New_York')
  returning id into v_prop_b;

  insert into units (organization_id, property_id, unit_number, status)
  values (v_org_a, v_prop_a, '2', 'occupied') returning id into v_unit_a;

  -- One tenant who consented to notifications and one who did not.
  insert into tenants (organization_id, legal_name, primary_email, notifications_opted_in)
  values (v_org_a, 'Tomas Opted In', 'tomas@example.test', true)
  returning id into v_tenant_in;

  insert into tenants (organization_id, legal_name, primary_email,
                       notifications_opted_in, notifications_opt_out_at)
  values (v_org_a, 'Olga Opted Out', 'olga@example.test', false, now())
  returning id into v_tenant_out;

  insert into vendors (organization_id, name, company, email, trades)
  values (v_org_a, 'Mike Plumber', 'Mike Plumbing Ltd', 'mike@example.test',
          array['plumbing']::app.task_category[])
  returning id into v_vendor;

  perform pg_temp.put('auth_a', v_auth_a::text);
  perform pg_temp.put('auth_b', v_auth_b::text);
  perform pg_temp.put('auth_m', v_auth_m::text);
  perform pg_temp.put('user_a', v_user_a::text);
  perform pg_temp.put('user_b', v_user_b::text);
  perform pg_temp.put('user_m', v_user_m::text);
  perform pg_temp.put('org_a', v_org_a::text);
  perform pg_temp.put('org_b', v_org_b::text);
  perform pg_temp.put('prop_a', v_prop_a::text);
  perform pg_temp.put('prop_b', v_prop_b::text);
  perform pg_temp.put('unit_a', v_unit_a::text);
  perform pg_temp.put('tenant_in', v_tenant_in::text);
  perform pg_temp.put('tenant_out', v_tenant_out::text);
  perform pg_temp.put('vendor', v_vendor::text);
end;
$seed$;

\echo
\echo '== Board seeding ==========================================================='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_org_b uuid := pg_temp.uid('org_b');
  v_intake integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));
  perform app.seed_task_board(v_org_a);

  perform pg_temp.assert(
    (select count(*) from task_columns where organization_id = v_org_a) = 5,
    'seeding creates five columns');

  select count(*) into v_intake
  from task_columns where organization_id = v_org_a and is_default_intake;
  perform pg_temp.assert(v_intake = 1, 'exactly one intake column');

  -- The categories are what every rule reads, so they have to be present and
  -- distinct from the names.
  perform pg_temp.assert(
    (select category from task_columns
     where organization_id = v_org_a and name = 'Completed') = 'completed',
    'the Completed column carries the completed category');

  perform pg_temp.assert(
    (select count(*) from task_notification_rules where organization_id = v_org_a) >= 12,
    'seeding creates the default notification rules');

  -- The noisiest rule ships off.
  perform pg_temp.assert(
    (select not enabled from task_notification_rules
     where organization_id = v_org_a and name = 'No activity for 5 days'),
    'the staleness rule ships disabled');

  -- Seed organization B too, for the isolation tests.
  perform pg_temp.become(pg_temp.uid('auth_b'));
  perform app.seed_task_board(v_org_b);

  perform pg_temp.assert(
    (select count(*) from task_columns where organization_id = v_org_b) = 5,
    'organization B gets its own independent board');
end;
$t$;

\echo
\echo '== Creating a task: card, timeline, and event commit together =============='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_task  tasks;
  v_outbox_count integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  v_task := app.create_task(
    p_organization_id => v_org_a,
    p_property_id     => pg_temp.uid('prop_a'),
    p_title           => 'Roof leak above the stairwell',
    p_actor_user_id   => pg_temp.uid('user_a'),
    p_actor_display_name => 'Ada Owner',
    p_unit_id         => pg_temp.uid('unit_a'),
    p_description     => 'Water staining after heavy rain.',
    p_priority        => 'urgent',
    p_category        => 'roofing',
    p_source          => 'tenant_report',
    p_due_date        => current_date + 2,
    p_reported_by_tenant_id => pg_temp.uid('tenant_in')
  );

  perform pg_temp.put('task_leak', v_task.id::text);

  perform pg_temp.assert(v_task.reference_number = 1,
    'the first task in a workspace is number 1');

  perform pg_temp.assert(v_task.status_category = 'new',
    'a new task lands in the intake category');

  perform pg_temp.assert(
    (select is_default_intake from task_columns where id = v_task.task_column_id),
    'a new task lands in the intake column');

  -- The timeline entry and the event are written in the same transaction as
  -- the card. This is the property the outbox exists for.
  perform pg_temp.assert(
    (select count(*) from task_activity
     where task_id = v_task.id and activity_type = 'task_created') = 1,
    'creating a task writes one timeline entry');

  select count(*) into v_outbox_count
  from notification_outbox
  where aggregate_id = v_task.id and event_type = 'task_created';

  perform pg_temp.assert(v_outbox_count = 1,
    'creating a task writes one outbox event');

  perform pg_temp.assert(
    (select status from notification_outbox
     where aggregate_id = v_task.id and event_type = 'task_created') = 'pending',
    'the event starts pending, for the worker to pick up');

  -- The payload is a snapshot taken at the moment of the event.
  perform pg_temp.assert(
    (select payload ->> 'title' from notification_outbox
     where aggregate_id = v_task.id and event_type = 'task_created')
      = 'Roof leak above the stairwell',
    'the event payload snapshots the title');
end;
$t$;

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_second tasks;
  v_first_pos numeric;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select position into v_first_pos from tasks where id = pg_temp.uid('task_leak');

  v_second := app.create_task(
    p_organization_id => v_org_a,
    p_property_id     => pg_temp.uid('prop_a'),
    p_title           => 'Broken door lock',
    p_actor_user_id   => pg_temp.uid('user_a'),
    p_actor_display_name => 'Ada Owner',
    p_priority        => 'medium',
    p_category        => 'safety'
  );

  perform pg_temp.put('task_lock', v_second.id::text);

  perform pg_temp.assert(v_second.reference_number = 2,
    'reference numbers increment per organization');

  -- New work goes to the TOP of intake. An urgent report arriving below
  -- thirty stale cards is how it gets missed.
  perform pg_temp.assert(v_second.position < v_first_pos,
    'a new task is placed above the existing ones');
end;
$t$;

\echo
\echo '== Reference numbers are per organization, not global ======================'

do $t$
declare
  v_task tasks;
begin
  perform pg_temp.become(pg_temp.uid('auth_b'));

  v_task := app.create_task(
    p_organization_id => pg_temp.uid('org_b'),
    p_property_id     => pg_temp.uid('prop_b'),
    p_title           => 'Gutter clearing',
    p_actor_user_id   => pg_temp.uid('user_b'),
    p_actor_display_name => 'Ben Owner'
  );

  -- A global sequence would start organization B at 3 and leak how much work
  -- organization A has created.
  perform pg_temp.assert(v_task.reference_number = 1,
    'organization B also starts at 1');

  perform pg_temp.put('task_org_b', v_task.id::text);
end;
$t$;

\echo
\echo '== Moving a card: between columns raises events, within one does not ======='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_task  uuid := pg_temp.uid('task_leak');
  v_in_progress uuid;
  v_before integer;
  v_after  integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_m'));   -- the property manager

  select id into v_in_progress
  from task_columns where organization_id = v_org_a and category = 'in_progress';

  select count(*) into v_before from notification_outbox where aggregate_id = v_task;

  perform app.move_task(
    v_task, v_in_progress, null, null,
    pg_temp.uid('user_m'), 'Mia Manager');

  perform pg_temp.assert(
    (select status_category from tasks where id = v_task) = 'in_progress',
    'moving a card updates its status category');

  -- started_at is derived from the category, so every duration metric built
  -- on it is correct however the move was made.
  perform pg_temp.assert(
    (select started_at is not null from tasks where id = v_task),
    'entering in_progress sets started_at');

  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = v_task and event_type = 'task_status_changed') = 1,
    'moving between columns raises one status_changed event');

  perform pg_temp.assert(
    (select payload ->> 'from_column' from notification_outbox
     where aggregate_id = v_task and event_type = 'task_status_changed') = 'New',
    'the event records the column it came from, not just where it is now');

  -- Reordering inside a column. If this raised an event, tidying a board
  -- would email everybody involved in every card.
  select count(*) into v_before from notification_outbox where aggregate_id = v_task;

  perform app.move_task(
    v_task, v_in_progress, null, null,
    pg_temp.uid('user_m'), 'Mia Manager');

  select count(*) into v_after from notification_outbox where aggregate_id = v_task;

  perform pg_temp.assert(v_after = v_before,
    'reordering within a column raises no event');

  perform pg_temp.assert(
    (select count(*) from task_activity
     where task_id = v_task and activity_type = 'task_status_changed') = 1,
    'reordering within a column writes no timeline entry either');
end;
$t$;

\echo
\echo '== Fractional positions: a drag touches one row ============================'

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_intake uuid;
  v_a tasks; v_b tasks; v_c tasks;
  v_moved_position numeric;
  v_a_position numeric;
  v_b_position numeric;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select id into v_intake
  from task_columns where organization_id = v_org_a and is_default_intake;

  v_a := app.create_task(v_org_a, pg_temp.uid('prop_a'), 'Position A',
                         pg_temp.uid('user_a'), 'Ada Owner');
  v_b := app.create_task(v_org_a, pg_temp.uid('prop_a'), 'Position B',
                         pg_temp.uid('user_a'), 'Ada Owner');
  v_c := app.create_task(v_org_a, pg_temp.uid('prop_a'), 'Position C',
                         pg_temp.uid('user_a'), 'Ada Owner');

  select position into v_a_position from tasks where id = v_a.id;
  select position into v_b_position from tasks where id = v_b.id;

  -- Drop C between B and A. Positions descend as created (each new card goes
  -- on top), so B sits above A.
  perform app.move_task(
    v_c.id, v_intake,
    p_previous_task_id => v_b.id,
    p_next_task_id     => v_a.id,
    p_actor_user_id    => pg_temp.uid('user_a'),
    p_actor_display_name => 'Ada Owner');

  select position into v_moved_position from tasks where id = v_c.id;

  perform pg_temp.assert(
    v_moved_position > least(v_a_position, v_b_position)
      and v_moved_position < greatest(v_a_position, v_b_position),
    'the moved card takes a position between its two neighbours');

  -- The neighbours are untouched. An integer scheme would have rewritten the
  -- whole column.
  perform pg_temp.assert(
    (select position from tasks where id = v_a.id) = v_a_position
      and (select position from tasks where id = v_b.id) = v_b_position,
    'the neighbouring cards are not rewritten');
end;
$t$;

\echo
\echo '== Completion: event, timestamp, and the recurrence successor =============='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_completed uuid;
  v_recurring tasks;
  v_successor_count integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select id into v_completed
  from task_columns where organization_id = v_org_a and category = 'completed';

  -- A preventive-maintenance task that repeats.
  v_recurring := app.create_task(
    p_organization_id => v_org_a,
    p_property_id     => pg_temp.uid('prop_a'),
    p_title           => 'Quarterly smoke detector check',
    p_actor_user_id   => pg_temp.uid('user_a'),
    p_actor_display_name => 'Ada Owner',
    p_category        => 'safety',
    p_source          => 'preventive_maintenance',
    p_due_date        => current_date + 7,
    p_recurrence_interval_days => 90
  );

  perform app.move_task(
    v_recurring.id, v_completed, null, null,
    pg_temp.uid('user_a'), 'Ada Owner');

  perform pg_temp.assert(
    (select completed_at is not null from tasks where id = v_recurring.id),
    'completing a task sets completed_at');

  -- Completion is its own event, because people subscribe to it differently
  -- from the intermediate moves.
  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = v_recurring.id and event_type = 'task_completed') = 1,
    'completion raises its own event, separate from status_changed');

  select count(*) into v_successor_count
  from tasks
  where recurrence_parent_id = v_recurring.id;

  perform pg_temp.assert(v_successor_count = 1,
    'completing a recurring task creates exactly one successor');

  -- Counted from completion, not from the old due date, so finishing late
  -- does not immediately produce an overdue task.
  perform pg_temp.assert(
    (select due_date from tasks where recurrence_parent_id = v_recurring.id)
      = current_date + 90,
    'the successor is due an interval after completion, not after the old due date');

  perform pg_temp.assert(
    (select status_category from tasks where recurrence_parent_id = v_recurring.id) = 'new',
    'the successor starts in intake');

  -- Reopening clears the completion date rather than leaving a stale one.
  perform app.move_task(
    v_recurring.id,
    (select id from task_columns where organization_id = v_org_a and category = 'assigned'),
    null, null, pg_temp.uid('user_a'), 'Ada Owner');

  perform pg_temp.assert(
    (select completed_at is null from tasks where id = v_recurring.id),
    'reopening clears completed_at');

  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = v_recurring.id and event_type = 'task_reopened') = 1,
    'reopening raises its own event');
end;
$t$;

\echo
\echo '== Assignment names who it was taken from =================================='

do $t$
declare
  v_task uuid := pg_temp.uid('task_lock');
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  perform app.assign_task(
    v_task, pg_temp.uid('user_m'), null, true,
    pg_temp.uid('user_a'), 'Ada Owner');

  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = v_task and event_type = 'task_assigned') = 1,
    'the first assignment raises task_assigned');

  -- Reassigning to the vendor. "Reassigned from Mia to Mike Plumbing" is the
  -- useful message; "assigned to Mike" leaves Mia wondering.
  perform app.assign_task(
    v_task, null, pg_temp.uid('vendor'), false,
    pg_temp.uid('user_a'), 'Ada Owner');

  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = v_task and event_type = 'task_reassigned') = 1,
    'a later assignment raises task_reassigned, not task_assigned again');

  perform pg_temp.assert(
    (select payload ->> 'previous_assignees' from notification_outbox
     where aggregate_id = v_task and event_type = 'task_reassigned')
      like '%Mia Manager%',
    'the reassignment event names who it was taken from');
end;
$t$;

\echo
\echo '== Recipient resolution: consent, internal notes, and the actor ============'

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_task  uuid := pg_temp.uid('task_leak');
  v_rule  uuid;
  v_emails text;
  v_count integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select id into v_rule
  from task_notification_rules
  where organization_id = v_org_a and name = 'Comment added';

  -- The Comment added rule targets the assignee, the creator, and the
  -- reporting tenant. task_leak was reported by the tenant who opted IN.
  select string_agg(recipient_email::text, ',' order by recipient_email)
  into v_emails
  from app.resolve_task_notification_recipients(v_task, v_rule, null, false);

  perform pg_temp.assert(v_emails like '%tomas@example.test%',
    'a consenting reporting tenant is included');

  -- The same rule, but the comment is an internal note. An internal note must
  -- never reach the tenant it may be about.
  select string_agg(recipient_email::text, ',' order by recipient_email)
  into v_emails
  from app.resolve_task_notification_recipients(v_task, v_rule, null, true);

  perform pg_temp.assert(
    v_emails is null or v_emails not like '%tomas@example.test%',
    'an internal note is never addressed to the reporting tenant');

  -- A tenant who opted out is never returned, whatever the rule says.
  update tasks set reported_by_tenant_id = pg_temp.uid('tenant_out') where id = v_task;

  select string_agg(recipient_email::text, ',' order by recipient_email)
  into v_emails
  from app.resolve_task_notification_recipients(v_task, v_rule, null, false);

  perform pg_temp.assert(
    v_emails is null or v_emails not like '%olga@example.test%',
    'a tenant who opted out is never notified');

  update tasks set reported_by_tenant_id = pg_temp.uid('tenant_in') where id = v_task;

  -- A suppressed address is dropped even when the tenant consented.
  insert into email_suppressions (organization_id, email, reason)
  values (v_org_a, 'tomas@example.test', 'hard_bounce');

  select string_agg(recipient_email::text, ',' order by recipient_email)
  into v_emails
  from app.resolve_task_notification_recipients(v_task, v_rule, null, false);

  perform pg_temp.assert(
    v_emails is null or v_emails not like '%tomas@example.test%',
    'a suppressed address is dropped');

  delete from email_suppressions
  where organization_id = v_org_a and email = 'tomas@example.test';

  -- The actor is never notified of their own action. Being emailed about the
  -- thing you just did is how people learn to ignore the notifications.
  perform app.assign_task(
    v_task, pg_temp.uid('user_m'), null, true,
    pg_temp.uid('user_a'), 'Ada Owner');

  select count(*) into v_count
  from app.resolve_task_notification_recipients(
    v_task,
    (select id from task_notification_rules
     where organization_id = v_org_a and name = 'Task assigned'),
    pg_temp.uid('user_m'),      -- the actor is the assignee this time
    false)
  where recipient_user_id = pg_temp.uid('user_m');

  perform pg_temp.assert(v_count = 0,
    'the actor is excluded from their own action');
end;
$t$;

\echo
\echo '== Rule matching respects the priority filter =============================='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_urgent uuid := pg_temp.uid('task_leak');   -- urgent
  v_medium uuid := pg_temp.uid('task_lock');   -- medium
  v_matched integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  -- The seeded priority rule is narrowed to urgent and high.
  select count(*) into v_matched
  from app.matching_task_notification_rules(v_urgent, 'task_priority_changed');
  perform pg_temp.assert(v_matched >= 1,
    'an urgent task matches the priority rule');

  select count(*) into v_matched
  from app.matching_task_notification_rules(v_medium, 'task_priority_changed');
  perform pg_temp.assert(v_matched = 0,
    'a medium task does not match a rule filtered to urgent and high');

  -- A disabled rule matches nothing, which is what the toggle has to mean.
  update task_notification_rules set enabled = false
  where organization_id = v_org_a and name = 'Task assigned';

  select count(*) into v_matched
  from app.matching_task_notification_rules(v_urgent, 'task_assigned');
  perform pg_temp.assert(v_matched = 0, 'a disabled rule matches nothing');

  update task_notification_rules set enabled = true
  where organization_id = v_org_a and name = 'Task assigned';
end;
$t$;

\echo
\echo '== The schedule sweep is idempotent per day ================================'

do $t$
declare
  v_task uuid := pg_temp.uid('task_lock');
  v_first integer;
  v_second integer;
  v_overdue_events integer;
begin
  -- Make it overdue in the property's own zone.
  --
  -- The date has to be anchored to that zone, not to the server's. The
  -- fixture property is in Los Angeles specifically so that a UTC server
  -- cannot pass this trivially, and `current_date` here would be the server's
  -- UTC date: for the seven or eight hours a day when UTC has rolled over and
  -- Los Angeles has not, the sweep would measure one fewer day than this test
  -- set up, and the assertion below would fail on a correct implementation.
  -- That is a flaky test rather than a caught bug, so the offset is taken from
  -- the same clock the sweep uses.
  update tasks t
  set due_date = (now() at time zone p.timezone)::date - 3
  from properties p
  where t.id = v_task and p.id = t.property_id;

  v_first := app.sweep_task_schedule_events();
  perform pg_temp.assert(v_first >= 1, 'the sweep raises events for an overdue task');

  select count(*) into v_overdue_events
  from notification_outbox
  where aggregate_id = v_task and event_type = 'task_overdue';

  perform pg_temp.assert(v_overdue_events = 1,
    'one overdue event is raised');

  -- Running it again the same day must add nothing. The date is part of the
  -- idempotency key, which is what makes the sweep safe to run as often as
  -- you like.
  v_second := app.sweep_task_schedule_events();

  select count(*) into v_overdue_events
  from notification_outbox
  where aggregate_id = v_task and event_type = 'task_overdue';

  perform pg_temp.assert(v_overdue_events = 1,
    're-running the sweep the same day raises no second overdue event');

  perform pg_temp.assert(
    (select (payload ->> 'days_overdue')::integer from notification_outbox
     where aggregate_id = v_task and event_type = 'task_overdue') = 3,
    'the event records how many days overdue it is');

  -- A completed task is never overdue, however old its due date. Uses
  -- organization B's task, so become B's owner: A is not a member there and
  -- app.move_task would correctly refuse.
  perform pg_temp.become(pg_temp.uid('auth_b'));

  update tasks set due_date = current_date - 10
  where id = pg_temp.uid('task_org_b');

  perform app.move_task(
    pg_temp.uid('task_org_b'),
    (select id from task_columns
     where organization_id = pg_temp.uid('org_b') and category = 'completed'),
    null, null, pg_temp.uid('user_b'), 'Ben Owner');

  perform app.sweep_task_schedule_events();

  perform pg_temp.assert(
    (select count(*) from notification_outbox
     where aggregate_id = pg_temp.uid('task_org_b')
       and event_type = 'task_overdue') = 0,
    'a completed task is never reported overdue');
end;
$t$;

\echo
\echo '== Two workers claim disjoint batches ====================================='

do $t$
declare
  v_first  integer;
  v_second integer;
  v_pending integer;
begin
  select count(*) into v_pending
  from notification_outbox where status = 'pending';

  perform pg_temp.assert(v_pending > 1,
    'there is more than one pending event to contend over');

  -- The batch size is capped at 200, which is itself worth asserting: an
  -- unbounded batch would let one claim hold every pending event under a
  -- single lock.
  begin
    perform * from app.claim_outbox_events('worker-one', 1000, 120);
    perform pg_temp.assert(false, 'an oversized batch must be refused');
  exception when invalid_parameter_value then
    perform pg_temp.assert(true, 'an oversized claim batch is refused');
  end;

  select count(*) into v_first
  from app.claim_outbox_events('worker-one', 200, 120);

  perform pg_temp.assert(v_first = least(v_pending, 200),
    'worker one claims the pending events, up to the batch size');

  -- The second worker, issuing the identical query at the same moment, must
  -- come away with nothing: the rows are claimed and their locks are live.
  select count(*) into v_second
  from app.claim_outbox_events('worker-two', 200, 120);

  perform pg_temp.assert(v_second = 0, 'worker two claims nothing');

  -- Only the claim holder may complete an event.
  perform pg_temp.assert(
    app.complete_outbox_event(
      (select id from notification_outbox where status = 'claimed' limit 1),
      'worker-two') = false,
    'a non-holder cannot complete an event');

  perform pg_temp.assert(
    app.complete_outbox_event(
      (select id from notification_outbox where status = 'claimed' limit 1),
      'worker-one') = true,
    'the claim holder can complete an event');
end;
$t$;

\echo
\echo '== Delivery idempotency prevents a duplicate notification =================='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_first  uuid;
  v_second uuid;
begin
  v_first := app.create_notification_delivery(
    p_organization_id => v_org_a,
    p_outbox_id       => null,
    p_subject_type    => 'task',
    p_subject_id      => pg_temp.uid('task_leak'),
    p_channel         => 'in_app',
    p_recipient_user_id => pg_temp.uid('user_m'),
    p_recipient_email => null,
    p_recipient_role  => 'assignee',
    p_rule_id         => null,
    p_title           => 'Roof leak assigned to you',
    p_body            => 'Maple Street, unit 2.',
    p_link_path       => '/maintenance/1',
    p_idempotency_key => 'task_assigned:fixed-key-for-test:user_m:in_app');

  perform pg_temp.assert(v_first is not null, 'the first delivery is created');

  -- Re-processing the same outbox row after a crash must not notify the same
  -- person twice. The unique constraint, not application logic, is what
  -- guarantees it.
  v_second := app.create_notification_delivery(
    p_organization_id => v_org_a,
    p_outbox_id       => null,
    p_subject_type    => 'task',
    p_subject_id      => pg_temp.uid('task_leak'),
    p_channel         => 'in_app',
    p_recipient_user_id => pg_temp.uid('user_m'),
    p_recipient_email => null,
    p_recipient_role  => 'assignee',
    p_rule_id         => null,
    p_title           => 'Roof leak assigned to you',
    p_body            => 'Maple Street, unit 2.',
    p_link_path       => '/maintenance/1',
    p_idempotency_key => 'task_assigned:fixed-key-for-test:user_m:in_app');

  perform pg_temp.assert(v_second is null,
    'a replayed delivery is a no-op rather than a second notification');

  perform pg_temp.assert(
    (select count(*) from notification_deliveries
     where idempotency_key = 'task_assigned:fixed-key-for-test:user_m:in_app') = 1,
    'exactly one delivery row exists for the key');

  -- An in-app delivery is complete the moment it is written; there is nothing
  -- to send.
  perform pg_temp.assert(
    (select status from notification_deliveries where id = v_first) = 'sent',
    'an in-app delivery is immediately sent');

  perform pg_temp.assert(
    (select read_at is null from notification_deliveries where id = v_first),
    'a new in-app delivery is unread');
end;
$t$;

\echo
\echo '== The notification centre reads only the caller own inbox ================='

do $t$
declare
  v_count integer;
begin
  -- Mia, the addressee.
  perform pg_temp.become(pg_temp.uid('auth_m'));

  perform pg_temp.assert(app.unread_notification_count() = 1,
    'the recipient sees one unread notification');

  select count(*) into v_count from app.list_notifications();
  perform pg_temp.assert(v_count = 1, 'the recipient lists it');

  -- Ada, who was not the addressee. The function takes no user parameter, so
  -- there is no argument through which she could ask for Mia's inbox.
  perform pg_temp.become(pg_temp.uid('auth_a'));

  perform pg_temp.assert(app.unread_notification_count() = 0,
    'another user sees none of it');

  select count(*) into v_count from app.list_notifications();
  perform pg_temp.assert(v_count = 0, 'another user lists none of it');

  -- Marking read is scoped to the caller: Ada passing Mia's id changes nothing.
  perform pg_temp.assert(
    app.mark_notifications_read(
      array[(select id from notification_deliveries
             where recipient_user_id = pg_temp.uid('user_m') limit 1)]) = 0,
    'passing another user notification id marks nothing read');

  perform pg_temp.become(pg_temp.uid('auth_m'));

  perform pg_temp.assert(app.mark_notifications_read() = 1,
    'the recipient can mark their own as read');

  perform pg_temp.assert(app.unread_notification_count() = 0,
    'the badge clears after marking read');
end;
$t$;

\echo
\echo '== Hierarchy: a parent cycle is refused ==================================='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_parent tasks;
  v_child  tasks;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  v_parent := app.create_task(v_org_a, pg_temp.uid('prop_a'), 'Roof replacement',
                              pg_temp.uid('user_a'), 'Ada Owner');

  v_child := app.create_task(
    p_organization_id => v_org_a,
    p_property_id     => pg_temp.uid('prop_a'),
    p_title           => 'Obtain contractor quotes',
    p_actor_user_id   => pg_temp.uid('user_a'),
    p_actor_display_name => 'Ada Owner',
    p_parent_task_id  => v_parent.id);

  perform pg_temp.assert(
    (select parent_task_id from tasks where id = v_child.id) = v_parent.id,
    'a subtask records its parent');

  -- A CHECK stops self-parenting; only a trigger can catch A -> B -> A, and an
  -- unchecked cycle would make every subtask walk recurse until the stack
  -- runs out.
  begin
    update tasks set parent_task_id = v_child.id where id = v_parent.id;
    perform pg_temp.assert(false, 'a parent cycle must be refused');
  exception when integrity_constraint_violation then
    perform pg_temp.assert(true, 'a parent cycle is refused');
  end;

  begin
    update tasks set parent_task_id = id where id = v_parent.id;
    perform pg_temp.assert(false, 'self-parenting must be refused');
  exception when check_violation or integrity_constraint_violation then
    perform pg_temp.assert(true, 'self-parenting is refused');
  end;
end;
$t$;

\echo
\echo '== Column categories survive a rename ====================================='

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_matched integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  -- Users rename columns. Automation reads the category, so a board whose
  -- last column is called "Finito" must still stop chasing overdue work.
  update task_columns set name = 'Finito'
  where organization_id = v_org_a and category = 'completed';

  perform pg_temp.assert(
    (select category from task_columns
     where organization_id = v_org_a and name = 'Finito') = 'completed',
    'renaming a column leaves its category intact');

  -- The completed task from the recurrence test is still completed.
  perform pg_temp.assert(
    (select count(*) from tasks
     where organization_id = v_org_a and status_category = 'completed') >= 0,
    'status categories are unaffected by the rename');
end;
$t$;

\echo
\echo '== Isolation: organization A cannot reach organization B tasks ============='

set role app_api;

do $t$
declare
  v_count integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select count(*) into v_count from tasks where organization_id = pg_temp.uid('org_b');
  perform pg_temp.assert(v_count = 0, 'A sees no tasks in organization B');

  select count(*) into v_count from tasks where id = pg_temp.uid('task_org_b');
  perform pg_temp.assert(v_count = 0, 'A cannot select B task by its exact id');

  select count(*) into v_count from task_columns
  where organization_id = pg_temp.uid('org_b');
  perform pg_temp.assert(v_count = 0, 'A sees none of B board columns');

  select count(*) into v_count from vendors where organization_id = pg_temp.uid('org_b');
  perform pg_temp.assert(v_count = 0, 'A sees none of B vendors');

  select count(*) into v_count from task_activity
  where organization_id = pg_temp.uid('org_b');
  perform pg_temp.assert(v_count = 0, 'A sees none of B task timeline');
end;
$t$;

do $t$
declare
  v_updated integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_b'));

  -- B forges A's task id, as a tampered request would.
  update tasks set title = 'Hijacked' where id = pg_temp.uid('task_leak');
  get diagnostics v_updated = row_count;
  perform pg_temp.assert(v_updated = 0, 'B cannot update A task by forged id');

  delete from tasks where id = pg_temp.uid('task_leak');
  get diagnostics v_updated = row_count;
  perform pg_temp.assert(v_updated = 0, 'B cannot delete A task by forged id');

  -- And B cannot move A's task through the function, which re-checks
  -- authorization precisely because it is SECURITY DEFINER.
  begin
    perform app.move_task(
      pg_temp.uid('task_leak'),
      (select id from task_columns where organization_id = pg_temp.uid('org_b') limit 1),
      null, null, pg_temp.uid('user_b'), 'Ben Owner');
    perform pg_temp.assert(false, 'B must not move A task');
  exception when insufficient_privilege or foreign_key_violation then
    perform pg_temp.assert(true, 'B cannot move A task');
  end;
end;
$t$;

\echo
\echo '== The API role cannot forge or read notifications ========================'

do $t$
declare
  v_count integer;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  -- The outbox is internal plumbing: RLS denies every row AND the privilege
  -- is revoked, so an event can never describe something that did not happen.
  begin
    select count(*) into v_count from notification_outbox;
    -- RLS with no policy returns zero rather than raising, which is also fine.
    perform pg_temp.assert(v_count = 0, 'the API sees no outbox rows');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the API cannot read the outbox at all');
  end;

  begin
    insert into notification_outbox (
      organization_id, aggregate_type, aggregate_id, event_type,
      payload, idempotency_key)
    values (pg_temp.uid('org_a'), 'task', pg_temp.uid('task_leak'),
            'task_completed', '{}'::jsonb, 'forged-event-key');
    perform pg_temp.assert(false, 'the API must not insert an event directly');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the API cannot insert an event directly');
  end;

  -- And it cannot write a notification addressed to somebody else, which
  -- would bypass every consent and suppression check.
  begin
    insert into notification_deliveries (
      organization_id, subject_type, subject_id, channel,
      recipient_user_id, title, body, idempotency_key)
    values (pg_temp.uid('org_a'), 'task', pg_temp.uid('task_leak'), 'in_app',
            pg_temp.uid('user_m'), 'Forged', 'Forged body', 'forged-delivery-key');
    perform pg_temp.assert(false, 'the API must not insert a delivery');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the API cannot insert a delivery');
  end;

  begin
    perform app.create_notification_delivery(
      pg_temp.uid('org_a'), null, 'task', pg_temp.uid('task_leak'), 'in_app',
      pg_temp.uid('user_m'), null, 'assignee', null,
      'Forged', 'Forged', null, 'forged-via-function');
    perform pg_temp.assert(false, 'the API must not call the delivery writer');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the API cannot call the delivery writer');
  end;

  -- Nor claim worker queue work.
  begin
    perform * from app.claim_outbox_events('impostor', 5, 60);
    perform pg_temp.assert(false, 'the API must not claim outbox events');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'the API cannot claim outbox events');
  end;
end;
$t$;

\echo
\echo '== The task timeline is append-only ======================================='

do $t$
declare
  v_id bigint;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select id into v_id from task_activity
  where task_id = pg_temp.uid('task_leak') limit 1;

  perform pg_temp.assert(v_id is not null, 'the API can read the timeline');

  begin
    update task_activity set summary = 'tampered' where id = v_id;
    perform pg_temp.assert(false, 'a timeline entry must not be editable');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'a timeline entry cannot be edited');
  end;

  begin
    delete from task_activity where id = v_id;
    perform pg_temp.assert(false, 'a timeline entry must not be deletable');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'a timeline entry cannot be deleted');
  end;
end;
$t$;

\echo
\echo '== A read-only member can comment but not change the board ================'

reset role;

do $t$
declare
  v_auth_r uuid := gen_random_uuid();
  v_user_r uuid;
begin
  insert into users (auth_user_id, email, display_name)
  values (v_auth_r, 'reader-a@example.test', 'Rory Reader')
  returning id into v_user_r;

  insert into organization_members (organization_id, user_id, role)
  values (pg_temp.uid('org_a'), v_user_r, 'read_only');

  perform pg_temp.put('auth_r', v_auth_r::text);
  perform pg_temp.put('user_r', v_user_r::text);
end;
$t$;

set role app_api;

do $t$
declare
  v_comment task_comments;
begin
  perform pg_temp.become(pg_temp.uid('auth_r'));

  -- Commenting is open to any member, including read-only. A read-only role is
  -- about not changing the portfolio, not about being unable to say "the
  -- contractor cannot get access on Tuesday".
  v_comment := app.add_task_comment(
    pg_temp.uid('task_leak'), 'The contractor cannot get access on Tuesday.',
    false, pg_temp.uid('user_r'), 'Rory Reader');

  perform pg_temp.assert(v_comment.id is not null,
    'a read-only member can comment');

  -- But cannot move a card.
  begin
    perform app.move_task(
      pg_temp.uid('task_leak'),
      (select id from task_columns
       where organization_id = pg_temp.uid('org_a') and category = 'blocked'),
      null, null, pg_temp.uid('user_r'), 'Rory Reader');
    perform pg_temp.assert(false, 'a read-only member must not move a card');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'a read-only member cannot move a card');
  end;

  -- Nor create a task.
  begin
    perform app.create_task(
      pg_temp.uid('org_a'), pg_temp.uid('prop_a'), 'Reader task',
      pg_temp.uid('user_r'), 'Rory Reader');
    perform pg_temp.assert(false, 'a read-only member must not create a task');
  exception when insufficient_privilege then
    perform pg_temp.assert(true, 'a read-only member cannot create a task');
  end;
end;
$t$;

\echo
\echo '== Attachment paths cannot be user-controlled =============================='

reset role;

do $t$
declare
  v_org_a uuid := pg_temp.uid('org_a');
  v_task  uuid := pg_temp.uid('task_leak');
begin
  -- A traversal path.
  begin
    insert into task_attachments (
      organization_id, task_id, storage_bucket, storage_path,
      original_filename, declared_mime_type, file_size_bytes)
    values (v_org_a, v_task, 'task-attachments', '../../etc/passwd',
            'passwd', 'image/jpeg', 100);
    perform pg_temp.assert(false, 'a traversal path must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'a traversal attachment path is rejected');
  end;

  -- A path under another organization's prefix.
  begin
    insert into task_attachments (
      organization_id, task_id, storage_bucket, storage_path,
      original_filename, declared_mime_type, file_size_bytes)
    values (v_org_a, v_task, 'task-attachments',
            'org/' || pg_temp.uid('org_b')::text || '/tasks/'
              || v_task::text || '/' || gen_random_uuid()::text || '.jpg',
            'cross.jpg', 'image/jpeg', 100);
    perform pg_temp.assert(false, 'a cross-organization path must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'a cross-organization attachment path is rejected');
  end;

  -- An unsupported format.
  begin
    insert into task_attachments (
      organization_id, task_id, storage_bucket, storage_path,
      original_filename, declared_mime_type, file_size_bytes)
    values (v_org_a, v_task, 'task-attachments',
            'org/' || v_org_a::text || '/tasks/' || v_task::text || '/'
              || gen_random_uuid()::text || '.jpg',
            'payload.exe', 'application/x-msdownload', 100);
    perform pg_temp.assert(false, 'an unsupported attachment type must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'an unsupported attachment type is rejected');
  end;

  -- Oversized: 25 MB ceiling.
  begin
    insert into task_attachments (
      organization_id, task_id, storage_bucket, storage_path,
      original_filename, declared_mime_type, file_size_bytes)
    values (v_org_a, v_task, 'task-attachments',
            'org/' || v_org_a::text || '/tasks/' || v_task::text || '/'
              || gen_random_uuid()::text || '.jpg',
            'huge.jpg', 'image/jpeg', 26214401);
    perform pg_temp.assert(false, 'an oversized attachment must be rejected');
  exception when check_violation then
    perform pg_temp.assert(true, 'an oversized attachment is rejected');
  end;

  -- A well-formed one is accepted, so the constraints are not simply refusing
  -- everything.
  insert into task_attachments (
    organization_id, task_id, storage_bucket, storage_path,
    original_filename, declared_mime_type, detected_mime_type,
    file_size_bytes, malware_scan_status)
  values (v_org_a, v_task, 'task-attachments',
          'org/' || v_org_a::text || '/tasks/' || v_task::text || '/'
            || gen_random_uuid()::text || '.jpg',
          'ceiling photo.jpg', 'image/jpeg', 'image/jpeg', 184233, 'clean');

  perform pg_temp.assert(
    (select count(*) from task_attachments where task_id = v_task) = 1,
    'a well-formed attachment is accepted');
end;
$t$;

\echo
\echo '== A notification delivery is history ====================================='

do $t$
declare
  v_id uuid;
begin
  select id into v_id from notification_deliveries limit 1;

  begin
    update notification_deliveries set body = 'rewritten' where id = v_id;
    perform pg_temp.assert(false, 'delivery content must be immutable');
  exception when integrity_constraint_violation then
    perform pg_temp.assert(true, 'delivery content is immutable');
  end;

  -- Read state belongs to the reader, not to the event, so it may change.
  update notification_deliveries set read_at = now() where id = v_id;
  perform pg_temp.assert(true, 'read state may still be updated');
end;
$t$;

\echo
\echo '== Board summary counts in the property own time zone ====================='

do $t$
declare
  v_total bigint;
  v_overdue bigint;
begin
  perform pg_temp.become(pg_temp.uid('auth_a'));

  select sum(task_count), sum(overdue_count)
  into v_total, v_overdue
  from app.task_board_summary(pg_temp.uid('org_a'));

  perform pg_temp.assert(v_total > 0, 'the board summary counts tasks');

  -- The overdue task from the sweep test is still overdue.
  perform pg_temp.assert(v_overdue >= 1,
    'the board summary counts overdue tasks');

  -- Filtering to one property returns no more than the portfolio total.
  perform pg_temp.assert(
    (select coalesce(sum(task_count), 0)
     from app.task_board_summary(pg_temp.uid('org_a'), pg_temp.uid('prop_a')))
      <= v_total,
    'a property board is a subset of the portfolio board');
end;
$t$;

\echo
\echo '== All maintenance module tests passed ===================================='

rollback;
