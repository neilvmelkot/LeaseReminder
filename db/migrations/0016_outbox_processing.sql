-- =============================================================================
-- 0016_outbox_processing.sql
-- The worker side of the task notification engine, plus the notification
-- centre's read and write paths.
--
-- The shape mirrors the rent dispatcher in 0007, for the same reasons: claim
-- atomically in a tiny transaction, do the slow work outside every lock, and
-- record the result in a second transaction. The worker holds no table
-- privileges and reaches all of this through these functions only.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- app.claim_outbox_events
--
-- FOR UPDATE SKIP LOCKED, so several workers claim disjoint batches without
-- blocking each other. Ordered by occurred_at, so notifications about one task
-- arrive in the order the changes happened: "moved to In Progress" then
-- "completed", never the reverse.
-- -----------------------------------------------------------------------------
create or replace function app.claim_outbox_events(
  p_worker_id    text,
  p_batch_size   integer default 25,
  p_lock_seconds integer default 120
)
returns setof notification_outbox
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if p_worker_id is null or char_length(p_worker_id) not between 1 and 200 then
    raise exception 'A worker id between 1 and 200 characters is required'
      using errcode = 'invalid_parameter_value';
  end if;

  if p_batch_size not between 1 and 200 then
    raise exception 'Batch size must be between 1 and 200'
      using errcode = 'invalid_parameter_value';
  end if;

  return query
  with claimable as (
    select o.id
    from notification_outbox o
    where (
        o.status = 'pending'
        or (o.status = 'failed'
              and o.next_attempt_at is not null
              and o.next_attempt_at <= now()
              and o.attempt_count < o.max_attempts)
        -- Work abandoned by a worker that died mid-event.
        or (o.status = 'claimed' and o.lock_expires_at < now())
      )
    order by o.occurred_at
    limit p_batch_size
    for update of o skip locked
  )
  update notification_outbox o
  set status          = 'claimed',
      locked_at       = now(),
      locked_by       = p_worker_id,
      lock_expires_at = now() + make_interval(secs => p_lock_seconds),
      attempt_count   = o.attempt_count + 1
  from claimable c
  where o.id = c.id
  returning o.*;
end;
$fn$;

comment on function app.claim_outbox_events(text, integer, integer) is
  'Atomically claims a batch of pending events with SKIP LOCKED, oldest first.';

-- -----------------------------------------------------------------------------
-- app.resolve_task_notification_recipients
--
-- Turns a rule's roles into actual addressees, against the task as it is now.
--
-- Role resolution rather than stored addresses is the whole point: a rule
-- saying "notify the assignee" keeps working after a reassignment, where a
-- stored address would keep emailing the person who handed the job over.
--
-- Three rules are applied here and nowhere else, because this is the only
-- place that has all the facts:
--
--   * A tenant who has opted out of notifications is never returned.
--   * A reporting tenant is never returned for an internal note.
--   * The actor is never returned for their own action. Being emailed about
--     the thing you just did is the fastest way to teach somebody to ignore
--     the notifications.
-- -----------------------------------------------------------------------------
create or replace function app.resolve_task_notification_recipients(
  p_task_id         uuid,
  p_rule_id         uuid,
  p_actor_user_id   uuid default null,
  p_is_internal     boolean default false
)
returns table (
  recipient_user_id uuid,
  recipient_email   citext,
  display_name      text,
  recipient_role    app.task_recipient_role,
  recipient_kind    app.recipient_kind
)
language plpgsql
stable
security definer
set search_path = pg_catalog, public
as $fn$
-- The RETURNS TABLE output names (recipient_kind, recipient_role, ...) are
-- PL/pgSQL variables, and the CTEs below select columns with the same names.
-- Without this pragma an unqualified reference is ambiguous and the function
-- fails at run time rather than at creation. use_column makes a column win,
-- which is what every reference in this body intends.
#variable_conflict use_column
declare
  v_task tasks;
begin
  select * into v_task from tasks where id = p_task_id;

  if not found then
    return;
  end if;

  return query
  with targets as (
    select r.recipient_role, r.recipient_id, r.recipient_group_id, r.recipient_kind
    from task_notification_rule_recipients r
    where r.task_notification_rule_id = p_rule_id
  ),

  -- Assignees: workspace members and vendors currently on the task.
  assignee_users as (
    select u.id as user_id, u.email, coalesce(u.display_name, u.email::text) as name,
           t.recipient_kind
    from targets t
    join task_assignees a on a.task_id = p_task_id and a.user_id is not null
    join users u on u.id = a.user_id
    where t.recipient_role = 'assignee'
  ),
  assignee_vendors as (
    select null::uuid as user_id, v.email, coalesce(v.company, v.name) as name,
           t.recipient_kind
    from targets t
    join task_assignees a on a.task_id = p_task_id and a.vendor_id is not null
    join vendors v on v.id = a.vendor_id
    where t.recipient_role in ('assignee', 'vendor')
      and v.email is not null
      and v.active
  ),

  -- The person who created the task.
  creators as (
    select u.id as user_id, u.email, coalesce(u.display_name, u.email::text) as name,
           t.recipient_kind
    from targets t
    join users u on u.id = v_task.created_by_user_id
    where t.recipient_role = 'task_creator'
  ),

  -- The tenant who reported it. Gated twice: by their own consent, and by
  -- whether this particular message is an internal note.
  reporting_tenants as (
    select null::uuid as user_id, te.primary_email as email,
           coalesce(te.preferred_name, te.legal_name) as name,
           t.recipient_kind
    from targets t
    join tenants te on te.id = v_task.reported_by_tenant_id
    where t.recipient_role = 'reporting_tenant'
      and te.primary_email is not null
      -- Consent. Section 6.C: a rule targeting tenants must honour it.
      and te.notifications_opted_in
      -- An internal note never reaches the tenant it may be about.
      and not coalesce(p_is_internal, false)
  ),

  -- Everyone who can act on the property, by role in the workspace.
  property_managers as (
    select u.id as user_id, u.email, coalesce(u.display_name, u.email::text) as name,
           t.recipient_kind
    from targets t
    join organization_members m on m.organization_id = v_task.organization_id
    join users u on u.id = m.user_id
    where t.recipient_role = 'property_manager'
      and m.role in ('property_manager', 'admin', 'owner')
  ),
  property_owners as (
    select u.id as user_id, u.email, coalesce(u.display_name, u.email::text) as name,
           t.recipient_kind
    from targets t
    join organization_members m on m.organization_id = v_task.organization_id
    join users u on u.id = m.user_id
    where t.recipient_role = 'property_owner'
      and m.role = 'owner'
  ),

  -- Explicit addresses and groups.
  custom_recipients as (
    select null::uuid as user_id, rc.email, coalesce(rc.display_name, rc.email::text) as name,
           t.recipient_kind
    from targets t
    join recipients rc on rc.id = t.recipient_id
    where t.recipient_role = 'custom_recipient' and rc.active
  ),
  group_recipients as (
    select null::uuid as user_id, rc.email, coalesce(rc.display_name, rc.email::text) as name,
           t.recipient_kind
    from targets t
    join recipient_group_members gm on gm.recipient_group_id = t.recipient_group_id
    join recipients rc on rc.id = gm.recipient_id
    where t.recipient_role = 'recipient_group' and rc.active
  ),

  -- Watchers attached to this specific task.
  watchers as (
    select u.id as user_id, u.email, coalesce(u.display_name, u.email::text) as name,
           'to'::app.recipient_kind as recipient_kind
    from task_recipients tr
    join users u on u.id = tr.user_id
    where tr.task_id = p_task_id and tr.user_id is not null
  ),

  combined as (
    select user_id, email, name, 'assignee'::app.task_recipient_role as role, recipient_kind
      from assignee_users
    union all
    select user_id, email, name, 'vendor', recipient_kind from assignee_vendors
    union all
    select user_id, email, name, 'task_creator', recipient_kind from creators
    union all
    select user_id, email, name, 'reporting_tenant', recipient_kind from reporting_tenants
    union all
    select user_id, email, name, 'property_manager', recipient_kind from property_managers
    union all
    select user_id, email, name, 'property_owner', recipient_kind from property_owners
    union all
    select user_id, email, name, 'custom_recipient', recipient_kind from custom_recipients
    union all
    select user_id, email, name, 'recipient_group', recipient_kind from group_recipients
    union all
    select user_id, email, name, 'assignee', recipient_kind from watchers
  )

  select
    c.user_id,
    c.email,
    c.name,
    -- One row per person. When somebody is reached by two roles, the first in
    -- this order wins, so the explanation shown is the most specific one:
    -- "you are the assignee" rather than "you are in the Landlord group".
    (array_agg(c.role order by
       case c.role
         when 'assignee' then 1
         when 'vendor' then 2
         when 'task_creator' then 3
         when 'reporting_tenant' then 4
         when 'property_manager' then 5
         when 'property_owner' then 6
         when 'custom_recipient' then 7
         else 8
       end))[1] as role,
    -- To beats cc beats bcc, so being named twice never downgrades somebody to
    -- a blind copy.
    (array_agg(c.recipient_kind order by
       case c.recipient_kind when 'to' then 1 when 'cc' then 2 else 3 end))[1] as kind
  from combined c
  where
    -- Must be reachable somehow.
    (c.user_id is not null or c.email is not null)
    -- Never notify somebody about their own action.
    and (p_actor_user_id is null or c.user_id is null or c.user_id <> p_actor_user_id)
    -- Never notify a suppressed address.
    and (
      c.email is null
      or not exists (
        select 1 from email_suppressions s
        where s.organization_id = v_task.organization_id
          and s.email = c.email
          and (s.expires_at is null or s.expires_at > now())
      )
    )
  group by c.user_id, c.email, c.name;
end;
$fn$;

comment on function app.resolve_task_notification_recipients(uuid, uuid, uuid, boolean) is
  'Resolves a rule roles into addressees against the task now, honouring consent and suppression.';

-- -----------------------------------------------------------------------------
-- app.matching_task_notification_rules
--
-- Which enabled rules apply to one event on one task. Property rules override
-- organization rules for the same event, which is the same narrowing idea as
-- the rent rules, with the levels that actually exist here.
-- -----------------------------------------------------------------------------
create or replace function app.matching_task_notification_rules(
  p_task_id    uuid,
  p_event_type text
)
returns setof task_notification_rules
language plpgsql
stable
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_task tasks;
begin
  select * into v_task from tasks where id = p_task_id;

  if not found then
    return;
  end if;

  return query
  with applicable as (
    select r.*
    from task_notification_rules r
    where r.organization_id = v_task.organization_id
      and r.enabled
      and r.paused_at is null
      and r.trigger_kind = 'event'
      and r.event_type::text = p_event_type
      -- Organization-wide, or this property specifically.
      and (r.property_id is null or r.property_id = v_task.property_id)
      -- Narrowing filters. Null means "any", so an unconfigured filter does
      -- not accidentally exclude everything.
      and (r.only_priorities is null or v_task.priority = any (r.only_priorities))
      and (r.only_categories is null or v_task.category = any (r.only_categories))
      and (r.only_status_categories is null
             or v_task.status_category = any (r.only_status_categories))
  )
  select distinct on (name) *
  from applicable
  -- A property rule wins over an organization rule of the same name, so a
  -- property can tighten a default without the default also firing.
  order by name, (property_id is not null) desc, created_at;
end;
$fn$;

comment on function app.matching_task_notification_rules(uuid, text) is
  'Enabled rules for an event on a task. A property rule supersedes the organization default.';

-- -----------------------------------------------------------------------------
-- app.create_notification_delivery
--
-- Writes one delivery row. The UNIQUE constraint on the idempotency key, not
-- this function, is what prevents a duplicate: re-processing an outbox row
-- after a crash collapses onto the existing row instead of notifying somebody
-- a second time.
-- -----------------------------------------------------------------------------
create or replace function app.create_notification_delivery(
  p_organization_id uuid,
  p_outbox_id       bigint,
  p_subject_type    text,
  p_subject_id      uuid,
  p_channel         app.delivery_channel,
  p_recipient_user_id uuid,
  p_recipient_email citext,
  p_recipient_role  app.task_recipient_role,
  p_rule_id         uuid,
  p_title           text,
  p_body            text,
  p_link_path       text,
  p_idempotency_key text
)
returns uuid
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_id uuid;
begin
  insert into notification_deliveries (
    organization_id, outbox_id, subject_type, subject_id, channel,
    recipient_user_id, recipient_email, recipient_role,
    task_notification_rule_id, title, body, link_path,
    -- In-app is complete the moment it is written; there is nothing to send.
    -- Email starts pending and is picked up by the dispatcher.
    status, idempotency_key
  )
  values (
    p_organization_id, p_outbox_id, p_subject_type, p_subject_id, p_channel,
    p_recipient_user_id, p_recipient_email, p_recipient_role,
    p_rule_id, left(p_title, 300), left(p_body, 5000), p_link_path,
    case when p_channel = 'in_app' then 'sent'::app.delivery_status
         else 'pending'::app.delivery_status end,
    p_idempotency_key
  )
  on conflict (organization_id, idempotency_key) do nothing
  returning id into v_id;

  return v_id;
end;
$fn$;

comment on function app.create_notification_delivery is
  'Writes one delivery. The unique idempotency key is what actually prevents a duplicate.';

-- -----------------------------------------------------------------------------
-- Outbox completion
-- -----------------------------------------------------------------------------

create or replace function app.complete_outbox_event(
  p_outbox_id bigint,
  p_worker_id text
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_updated integer;
begin
  update notification_outbox
  set status = 'processed',
      processed_at = now(),
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      last_error_message = null,
      next_attempt_at = null
  where id = p_outbox_id
    and status = 'claimed'
    -- Only the claim holder may complete it, so a worker whose lock expired
    -- and was reclaimed elsewhere cannot overwrite the new holder's result.
    and locked_by = p_worker_id;

  get diagnostics v_updated = row_count;
  return v_updated = 1;
end;
$fn$;

comment on function app.complete_outbox_event(bigint, text) is
  'Marks an event processed. Only the claim holder may do so.';

create or replace function app.fail_outbox_event(
  p_outbox_id     bigint,
  p_worker_id     text,
  p_error_message text,
  p_permanent     boolean default false
)
returns app.outbox_status
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_event  notification_outbox;
  v_status app.outbox_status;
  v_delay  integer;
begin
  select * into v_event
  from notification_outbox
  where id = p_outbox_id and status = 'claimed' and locked_by = p_worker_id
  for update;

  if not found then
    return null;
  end if;

  if p_permanent or v_event.attempt_count >= v_event.max_attempts then
    v_status := 'dead_letter';
  else
    v_status := 'failed';
  end if;

  -- 30s, 60s, 120s ... capped at fifteen minutes, plus jitter so a batch that
  -- failed together does not retry in lockstep.
  v_delay := least(900, (30 * power(2, greatest(v_event.attempt_count - 1, 0)))::integer);
  v_delay := v_delay + floor(random() * 15)::integer;

  update notification_outbox
  set status = v_status,
      next_attempt_at = case when v_status = 'failed'
                             then now() + make_interval(secs => v_delay)
                             else null end,
      processed_at = case when v_status = 'dead_letter' then now() else null end,
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      last_error_message = left(coalesce(p_error_message, 'unspecified error'), 2000)
  where id = p_outbox_id;

  return v_status;
end;
$fn$;

comment on function app.fail_outbox_event(bigint, text, text, boolean) is
  'Records an event failure with exponential backoff and jitter, or dead-letters it.';

-- -----------------------------------------------------------------------------
-- app.sweep_task_schedule_events
--
-- The time-based half of the engine: due soon, due today, overdue, and stale.
--
-- The idempotency key for each event includes the DATE, which is what makes a
-- daily sweep safe to run as often as you like: running it five times on the
-- same day produces one "overdue" event, not five. That property is why the
-- sweep can be a simple unconditional pass rather than needing to track what
-- it has already done.
-- -----------------------------------------------------------------------------
create or replace function app.sweep_task_schedule_events(
  p_due_soon_days integer default 1,
  p_stale_days    integer default 5
)
returns integer
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_task    record;
  v_raised  integer := 0;
  v_today   date;
  v_event   text;
  v_key     text;
  v_outbox_id bigint;
begin
  for v_task in
    select t.id, t.organization_id, t.reference_number, t.title, t.priority,
           t.due_date, t.property_id, t.last_activity_at, t.status_category,
           p.timezone
    from tasks t
    join properties p on p.id = t.property_id
    where t.status_category not in ('completed', 'cancelled')
  loop
    -- Today in the PROPERTY's zone, not the server's. A task in Los Angeles
    -- becomes due on the Californian first of the month, and a UTC server
    -- would call it overdue several hours early.
    v_today := (now() at time zone v_task.timezone)::date;

    -- Due-date events. At most one per task per sweep: today's state is one
    -- of due soon, due today, or overdue, never two at once.
    if v_task.due_date is not null then
      v_event := null;

      if v_task.due_date < v_today then
        v_event := 'task_overdue';
      elsif v_task.due_date = v_today then
        v_event := 'task_due_today';
      elsif v_task.due_date <= v_today + p_due_soon_days then
        v_event := 'task_due_soon';
      end if;

      if v_event is not null then
        -- The date in the key is what makes re-running the sweep a no-op.
        v_key := v_event || ':' || v_task.id::text || ':' || v_today::text;

        select app.record_outbox_event(
          v_task.organization_id, 'task', v_task.id, v_event,
          jsonb_build_object(
            'task_id', v_task.id,
            'reference_number', v_task.reference_number,
            'title', v_task.title,
            'priority', v_task.priority,
            'due_date', v_task.due_date,
            'property_id', v_task.property_id,
            'local_date', v_today,
            'days_overdue', greatest(v_today - v_task.due_date, 0)
          ),
          null, 'System', v_key
        ) into v_outbox_id;

        if v_outbox_id is not null then
          v_raised := v_raised + 1;
        end if;
      end if;
    end if;

    -- Staleness: nothing has happened for a while. Independent of the due
    -- date, because a task with no due date can still be forgotten, which is
    -- precisely the case this catches.
    if v_task.last_activity_at < now() - make_interval(days => p_stale_days) then
      v_key := 'task_stale:' || v_task.id::text || ':' || v_today::text;

      select app.record_outbox_event(
        v_task.organization_id, 'task', v_task.id, 'task_stale',
        jsonb_build_object(
          'task_id', v_task.id,
          'reference_number', v_task.reference_number,
          'title', v_task.title,
          'priority', v_task.priority,
          'property_id', v_task.property_id,
          'status_category', v_task.status_category,
          'last_activity_at', v_task.last_activity_at,
          'days_inactive', extract(day from now() - v_task.last_activity_at)::integer
        ),
        null, 'System', v_key
      ) into v_outbox_id;

      if v_outbox_id is not null then
        v_raised := v_raised + 1;
      end if;
    end if;
  end loop;

  return v_raised;
end;
$fn$;

comment on function app.sweep_task_schedule_events(integer, integer) is
  'Raises due, overdue, and stale task events. Date-keyed, so re-running it the same day is a no-op.';

-- -----------------------------------------------------------------------------
-- The notification centre
-- -----------------------------------------------------------------------------

/**
 * One user's in-app notifications.
 *
 * Takes no user argument: it reads the caller's identity, so there is no
 * parameter through which one user could request another's inbox.
 */
create or replace function app.list_notifications(
  p_include_read boolean default false,
  p_limit        integer default 50,
  p_before       timestamptz default null
)
returns table (
  id            uuid,
  subject_type  text,
  subject_id    uuid,
  title         text,
  body          text,
  link_path     text,
  recipient_role app.task_recipient_role,
  read_at       timestamptz,
  created_at    timestamptz
)
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select d.id, d.subject_type, d.subject_id, d.title, d.body, d.link_path,
         d.recipient_role, d.read_at, d.created_at
  from notification_deliveries d
  where d.channel = 'in_app'
    and d.recipient_user_id = app.current_user_id()
    and d.dismissed_at is null
    and (p_include_read or d.read_at is null)
    and (p_before is null or d.created_at < p_before)
  order by d.created_at desc
  limit least(greatest(p_limit, 1), 200)
$fn$;

comment on function app.list_notifications(boolean, integer, timestamptz) is
  'The caller own in-app notifications. Takes no user parameter, by design.';

create or replace function app.unread_notification_count()
returns integer
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select count(*)::integer
  from notification_deliveries d
  where d.channel = 'in_app'
    and d.recipient_user_id = app.current_user_id()
    and d.read_at is null
    and d.dismissed_at is null
$fn$;

comment on function app.unread_notification_count() is
  'Badge count for the caller.';

/**
 * Marks notifications read.
 *
 * The WHERE clause pins recipient_user_id to the caller, so passing somebody
 * else's notification id updates nothing rather than silently clearing their
 * badge. Returns the number actually affected, so the client can tell.
 */
create or replace function app.mark_notifications_read(
  -- Null marks everything read, which is the "mark all" button.
  p_notification_ids uuid[] default null
)
returns integer
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_user_id uuid := app.current_user_id();
  v_updated integer;
begin
  if v_user_id is null then
    return 0;
  end if;

  update notification_deliveries
  set read_at = now()
  where channel = 'in_app'
    and recipient_user_id = v_user_id
    and read_at is null
    and (p_notification_ids is null or id = any (p_notification_ids));

  get diagnostics v_updated = row_count;
  return v_updated;
end;
$fn$;

comment on function app.mark_notifications_read(uuid[]) is
  'Marks the caller own notifications read. Another user id affects nothing.';

create or replace function app.dismiss_notification(p_notification_id uuid)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_user_id uuid := app.current_user_id();
  v_updated integer;
begin
  if v_user_id is null then
    return false;
  end if;

  update notification_deliveries
  set dismissed_at = now(),
      -- Dismissing implies reading, so the badge and the list agree.
      read_at = coalesce(read_at, now())
  where id = p_notification_id
    and channel = 'in_app'
    and recipient_user_id = v_user_id;

  get diagnostics v_updated = row_count;
  return v_updated = 1;
end;
$fn$;

comment on function app.dismiss_notification(uuid) is
  'Dismisses one of the caller own notifications, keeping it in history.';

-- -----------------------------------------------------------------------------
-- Email delivery, for the worker
-- -----------------------------------------------------------------------------

create or replace function app.claim_pending_email_deliveries(
  p_worker_id  text,
  p_batch_size integer default 20
)
returns table (
  delivery_id     uuid,
  organization_id uuid,
  recipient_email citext,
  title           text,
  body            text,
  link_path       text,
  subject_type    text,
  subject_id      uuid
)
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
-- Same reason as above: the output names shadow the columns selected below.
#variable_conflict use_column
begin
  return query
  with claimable as (
    select d.id
    from notification_deliveries d
    join email_accounts a
      on a.organization_id = d.organization_id
    where d.channel = 'email'
      and d.status = 'pending'
      -- No connected sender means nothing is claimable, which is what stops
      -- the worker spinning on work it cannot complete and is the same gate
      -- the rent dispatcher uses.
      and a.connection_status = 'connected'
    order by d.created_at
    limit p_batch_size
    for update of d skip locked
  )
  update notification_deliveries d
  set status = 'pending'
  from claimable c
  where d.id = c.id
  returning d.id, d.organization_id, d.recipient_email, d.title, d.body,
            d.link_path, d.subject_type, d.subject_id;
end;
$fn$;

comment on function app.claim_pending_email_deliveries(text, integer) is
  'Claims pending notification emails. Returns nothing while no Gmail account is connected.';

create or replace function app.record_delivery_result(
  p_delivery_id   uuid,
  p_accepted      boolean,
  p_provider_message_id text default null,
  p_error_category app.email_error_category default 'none',
  p_error_message text default null
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_updated integer;
begin
  update notification_deliveries
  set status = case when p_accepted then 'sent'::app.delivery_status
                    else 'failed'::app.delivery_status end,
      provider_message_id = p_provider_message_id,
      provider_accepted_at = case when p_accepted then now() else null end,
      error_category = p_error_category,
      error_message = left(p_error_message, 2000)
  where id = p_delivery_id
    and channel = 'email';

  get diagnostics v_updated = row_count;
  return v_updated = 1;
end;
$fn$;

comment on function app.record_delivery_result is
  'Records the provider outcome for one notification email.';
