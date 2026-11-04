-- =============================================================================
-- 0020_rent_schedule_pipeline.sql
--
-- The write path for rent schedules and reminder rules (phase 3).
--
-- Two kinds of function, for two different reasons.
--
-- ATOMICITY. Regenerating a lease's schedule replaces every future occurrence
-- and every pending reminder derived from them. Done as separate statements,
-- an interruption leaves a lease with half a schedule and reminders pointing
-- at dates that no longer exist. app.replace_rent_occurrences does the whole
-- swap in one transaction.
--
-- IDEMPOTENCE. A reminder must never be sent twice because a worker ran
-- twice. scheduled_notifications already carries a unique idempotency key per
-- organization; app.upsert_scheduled_notification is written so that a second
-- attempt updates the pending row rather than inserting a duplicate, and so
-- that a row already sent is never touched again.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- app.upsert_rent_schedule
--
-- A lease has at most one active rent schedule. Editing it supersedes the old
-- one rather than mutating it, so a schedule that has already produced
-- occurrences keeps its identity and those occurrences keep meaning something.
-- -----------------------------------------------------------------------------
create or replace function app.upsert_rent_schedule(
  p_lease_id            uuid,
  p_frequency           text,
  p_due_day_rule        text,
  p_due_day_of_month    smallint,
  p_start_date          date,
  p_end_date            date,
  p_amount              numeric,
  p_currency            char(3),
  p_timezone            text,
  p_short_month_fallback text default 'last_day_of_month',
  p_business_day_shift  text default 'none',
  p_holiday_calendar_id uuid default null,
  p_anchor_date         date default null,
  p_custom_dates        date[] default null
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
  v_schedule_id     uuid;
  v_existing_id     uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Editing a rent schedule requires an authenticated user.'
      using errcode = '28000';
  end if;

  select l.organization_id into v_organization_id
  from leases l
  where l.id = p_lease_id;

  if v_organization_id is null then
    raise exception 'No such lease.' using errcode = 'P0002';
  end if;

  if not app.can_write_org(v_organization_id) then
    raise exception 'You do not have permission to edit rent schedules here.'
      using errcode = '42501';
  end if;

  select s.id into v_existing_id
  from rent_schedules s
  where s.lease_id = p_lease_id and s.active
  limit 1;

  if v_existing_id is not null then
    -- Deactivated, not deleted. Its occurrences are the record of what was
    -- actually charged, and a foreign key would take them with it.
    update rent_schedules
    set active = false, updated_at = now()
    where id = v_existing_id;
  end if;

  insert into rent_schedules (
    organization_id, lease_id, frequency, due_day_rule, due_day_of_month,
    custom_dates, anchor_date, start_date, end_date, short_month_fallback,
    business_day_shift, holiday_calendar_id, timezone, amount, currency,
    active,
    -- Bumped past whatever the superseded schedule reached, so occurrences
    -- generated under the old definition are distinguishable from the new
    -- ones and a partially-applied regeneration is detectable.
    generation_version
  )
  values (
    v_organization_id, p_lease_id, p_frequency::app.rent_frequency,
    p_due_day_rule::app.due_day_rule, p_due_day_of_month, p_custom_dates,
    p_anchor_date, p_start_date, p_end_date,
    p_short_month_fallback::app.short_month_fallback,
    p_business_day_shift::app.business_day_shift, p_holiday_calendar_id,
    p_timezone, p_amount, p_currency, true,
    coalesce(
      (select max(s.generation_version) + 1 from rent_schedules s
       where s.lease_id = p_lease_id),
      1)
  )
  returning id into v_schedule_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'rent_schedule.saved', 'rent_schedule', v_schedule_id);

  return v_schedule_id;
end;
$$;

comment on function app.upsert_rent_schedule is
  'Replaces a lease''s active rent schedule, superseding rather than mutating the old one.';

-- -----------------------------------------------------------------------------
-- app.claim_leases_for_schedule_generation
--
-- A lease needs regenerating when its active schedule is newer than the
-- occurrences on record, or when the horizon has moved far enough that the
-- furthest occurrence is no longer far enough ahead.
-- -----------------------------------------------------------------------------
create or replace function app.claim_leases_for_schedule_generation(
  p_batch_size   integer default 10,
  p_horizon_days integer default 400
)
returns table (
  lease_id           uuid,
  organization_id    uuid,
  rent_schedule_id   uuid,
  generation_version integer,
  frequency          text,
  due_day_rule       text,
  due_day_of_month   smallint,
  custom_dates       date[],
  anchor_date        date,
  start_date         date,
  end_date           date,
  short_month_fallback text,
  business_day_shift text,
  holiday_calendar_id uuid,
  timezone           text,
  amount             numeric,
  currency           char(3),
  grace_period_days  integer,
  late_fee_after_days integer
)
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
begin
  if p_batch_size is null or p_batch_size < 1 or p_batch_size > 100 then
    raise exception 'Batch size must be between 1 and 100.' using errcode = '22023';
  end if;

  return query
  select
    l.id, l.organization_id, s.id, s.generation_version,
    s.frequency::text, s.due_day_rule::text, s.due_day_of_month,
    s.custom_dates, s.anchor_date, s.start_date, s.end_date,
    s.short_month_fallback::text, s.business_day_shift::text,
    s.holiday_calendar_id, s.timezone, s.amount, s.currency,
    l.grace_period_days, l.late_fee_after_days
  from leases l
  join rent_schedules s
    on s.lease_id = l.id and s.organization_id = l.organization_id and s.active
  where
    -- Only an approved lease. This is section 13.C again: an unapproved lease
    -- must not have a schedule generated for it, because a schedule is what
    -- reminders are built from.
    l.approval_status = 'approved'
    and l.lifecycle_status in ('upcoming', 'active')
    and (
      -- Never generated under this version of the schedule.
      not exists (
        select 1 from rent_occurrences o
        where o.rent_schedule_id = s.id
          and o.generation_version = s.generation_version
      )
      -- Or the horizon has outrun what exists.
      or (
        select max(o.due_date) from rent_occurrences o
        where o.rent_schedule_id = s.id
          and o.generation_version = s.generation_version
      ) < least(
            coalesce(s.end_date, 'infinity'::date),
            (current_date + p_horizon_days)
          )
    )
  order by l.updated_at
  limit p_batch_size
  for update of l skip locked;
end;
$$;

comment on function app.claim_leases_for_schedule_generation is
  'Finds approved leases whose rent occurrences are missing or no longer reach the horizon.';

-- -----------------------------------------------------------------------------
-- app.replace_rent_occurrences
--
-- The atomic swap. Future occurrences for this schedule are replaced; past
-- ones are left alone, because they are the record of what was charged.
-- -----------------------------------------------------------------------------
create or replace function app.replace_rent_occurrences(
  p_rent_schedule_id   uuid,
  p_generation_version integer,
  p_occurrences        jsonb
)
returns integer
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_organization_id uuid;
  v_lease_id        uuid;
  v_occurrence      jsonb;
  v_written         integer := 0;
begin
  select s.organization_id, s.lease_id into v_organization_id, v_lease_id
  from rent_schedules s
  where s.id = p_rent_schedule_id;

  if v_organization_id is null then
    raise exception 'No such rent schedule.' using errcode = 'P0002';
  end if;

  -- Only from today forward. Rewriting a past occurrence would change what the
  -- tenant was told was due, after they were told it.
  delete from rent_occurrences
  where rent_schedule_id = p_rent_schedule_id
    and due_date >= current_date;

  for v_occurrence in select * from jsonb_array_elements(coalesce(p_occurrences, '[]'::jsonb))
  loop
    insert into rent_occurrences (
      organization_id, lease_id, rent_schedule_id, sequence_number,
      nominal_due_date, due_date, grace_period_end_date, late_fee_date,
      amount, currency, adjustment_reason, generation_version
    )
    values (
      v_organization_id, v_lease_id, p_rent_schedule_id,
      (v_occurrence ->> 'sequence_number')::integer,
      (v_occurrence ->> 'nominal_due_date')::date,
      (v_occurrence ->> 'due_date')::date,
      (v_occurrence ->> 'grace_period_end_date')::date,
      (v_occurrence ->> 'late_fee_date')::date,
      (v_occurrence ->> 'amount')::numeric,
      (v_occurrence ->> 'currency')::char(3),
      v_occurrence ->> 'adjustment_reason',
      p_generation_version
    );

    v_written := v_written + 1;
  end loop;

  -- Touch the lease so the claim query's ordering moves it to the back of the
  -- queue. Without this a lease that produced no occurrences would be claimed
  -- again immediately, forever.
  update leases set updated_at = now() where id = v_lease_id;

  return v_written;
end;
$$;

comment on function app.replace_rent_occurrences is
  'Replaces a schedule''s future occurrences in one transaction. Past ones are never touched.';

-- -----------------------------------------------------------------------------
-- app.upsert_scheduled_notification
--
-- The idempotent write. The unique index on (organization_id,
-- idempotency_key) is what makes "run the generator twice" safe, and this
-- function is what turns a collision into an update rather than an error.
-- -----------------------------------------------------------------------------
create or replace function app.upsert_scheduled_notification(
  p_organization_id uuid,
  p_idempotency_key text,
  p_rule_id         uuid,
  p_lease_id        uuid,
  p_occurrence_id   uuid,
  p_event_type      text,
  p_send_at         timestamptz,
  p_payload         jsonb
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_id uuid;
begin
  insert into scheduled_notifications (
    organization_id, idempotency_key, notification_rule_id, lease_id,
    rent_occurrence_id, event_type, send_at, payload, status
  )
  values (
    p_organization_id, p_idempotency_key, p_rule_id, p_lease_id,
    p_occurrence_id, p_event_type::app.notification_event_type, p_send_at,
    p_payload, 'pending'
  )
  on conflict (organization_id, idempotency_key) do update
    -- Only while still pending. A notification already sent, or being sent, is
    -- never rewritten: moving the send time of something that has gone out
    -- would make the log disagree with what the recipient received.
    set send_at = excluded.send_at,
        payload = excluded.payload,
        updated_at = now()
    where scheduled_notifications.status = 'pending'
  returning id into v_id;

  return v_id;
end;
$$;

comment on function app.upsert_scheduled_notification is
  'Idempotent reminder scheduling. A second generation updates a pending row and never '
  'touches one already sent.';

-- -----------------------------------------------------------------------------
-- app.cancel_orphaned_notifications
--
-- After a regeneration, pending reminders that point at occurrences which no
-- longer exist have to go. Otherwise editing a lease from the 1st to the 15th
-- leaves the tenant receiving both.
-- -----------------------------------------------------------------------------
create or replace function app.cancel_orphaned_notifications(p_lease_id uuid)
returns integer
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_cancelled integer;
begin
  update scheduled_notifications n
  set status = 'cancelled', updated_at = now()
  where n.lease_id = p_lease_id
    and n.status = 'pending'
    and n.rent_occurrence_id is not null
    and not exists (
      select 1 from rent_occurrences o where o.id = n.rent_occurrence_id
    );

  get diagnostics v_cancelled = row_count;

  return v_cancelled;
end;
$$;

-- -----------------------------------------------------------------------------
-- app.effective_notification_rules
--
-- Resolves the three-level hierarchy for display, so the rule builder can show
-- where each rule came from.
--
-- The resolution itself is NOT performed here: NotificationRuleResolver in the
-- application layer does it, and is unit-tested. This function returns the
-- rules at every level with their provenance, and the caller decides. Two
-- implementations of the same precedence logic would be two things to keep in
-- step, and the symptom of their disagreeing would be a reminder that the
-- screen says is off.
-- -----------------------------------------------------------------------------
create or replace function app.effective_notification_rules(
  p_organization_id uuid,
  p_property_id     uuid default null,
  p_lease_id        uuid default null
)
returns table (
  rule_id          uuid,
  scope            text,
  property_id      uuid,
  lease_id         uuid,
  event_type       text,
  trigger_type     text,
  offset_value     integer,
  offset_unit      text,
  send_local_time  time,
  timezone         text,
  inheritance_mode text,
  overrides_rule_id uuid,
  template_id      uuid,
  enabled          boolean,
  priority         smallint,
  description      text
)
language sql
stable
security definer
set search_path = app, public
as $$
  select
    r.id, r.scope, r.property_id, r.lease_id,
    r.event_type::text, r.trigger_type::text, r.offset_value, r.offset_unit::text,
    r.send_local_time, r.timezone, r.inheritance_mode::text, r.overrides_rule_id,
    r.template_id, r.enabled, r.priority, r.description
  from notification_rules r
  where r.organization_id = p_organization_id
    and app.is_org_member(r.organization_id)
    and (
      r.scope = 'global'
      or (r.scope = 'property' and r.property_id = p_property_id)
      or (r.scope = 'lease' and r.lease_id = p_lease_id)
    )
  -- Most specific last, so a consumer applying them in order ends with the
  -- narrowest rule winning.
  order by
    case r.scope when 'global' then 0 when 'property' then 1 else 2 end,
    r.priority,
    r.created_at
$$;

comment on function app.effective_notification_rules is
  'Every rule that could apply at a scope, with its provenance. Precedence is applied by '
  'the caller, which is the only place it is implemented.';

-- -----------------------------------------------------------------------------
-- app.set_notification_rule_enabled
-- -----------------------------------------------------------------------------
create or replace function app.set_notification_rule_enabled(
  p_rule_id uuid,
  p_enabled boolean
)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Changing a reminder rule requires an authenticated user.'
      using errcode = '28000';
  end if;

  select r.organization_id into v_organization_id
  from notification_rules r
  where r.id = p_rule_id;

  if v_organization_id is null then
    raise exception 'No such rule.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can change reminder rules.'
      using errcode = '42501';
  end if;

  update notification_rules
  set enabled = p_enabled, updated_at = now()
  where id = p_rule_id;

  -- Switching a rule off cancels what it has already scheduled. Leaving them
  -- pending would mean turning a reminder off still sent the next one, which
  -- is the behaviour everybody assumes they are preventing.
  if not p_enabled then
    update scheduled_notifications
    set status = 'cancelled', updated_at = now()
    where notification_rule_id = p_rule_id and status = 'pending';
  end if;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    v_organization_id, v_user_id,
    case when p_enabled then 'notification_rule.enabled' else 'notification_rule.disabled' end,
    'notification_rule', p_rule_id);
end;
$$;

-- -----------------------------------------------------------------------------
-- app.holiday_calendar_dates
--
-- The worker needs a calendar's dates to shift a due date off a holiday, and
-- holds no table grants, so it reads them through a function like everything
-- else.
--
-- Deliberately not filtered by membership. It is granted to the worker alone,
-- which has no user identity to check against; the organization scoping comes
-- from the schedule row that supplied the calendar id, which the worker only
-- obtained from its own claim.
-- -----------------------------------------------------------------------------
create or replace function app.holiday_calendar_dates(p_calendar_id uuid)
returns table (holiday_date date)
language sql
stable
security definer
set search_path = app, public
as $fn$
  select d.holiday_date
  from holiday_dates d
  where d.holiday_calendar_id = p_calendar_id
  order by d.holiday_date
$fn$;

comment on function app.holiday_calendar_dates is
  'A holiday calendar''s dates, for the scheduling worker, which holds no table grants.';

-- =============================================================================
-- Grants
-- =============================================================================

grant execute on function app.upsert_rent_schedule(
  uuid, text, text, smallint, date, date, numeric, char, text, text, text,
  uuid, date, date[]) to app_api;
grant execute on function app.effective_notification_rules(uuid, uuid, uuid) to app_api;
grant execute on function app.set_notification_rule_enabled(uuid, boolean) to app_api;

-- Worker only. The API must never generate a schedule: generation is slow,
-- and a second path would let a request write occurrences without the claim
-- that stops two workers doing it at once.
grant execute on function app.claim_leases_for_schedule_generation(integer, integer)
  to app_worker;
grant execute on function app.replace_rent_occurrences(uuid, integer, jsonb) to app_worker;
grant execute on function app.upsert_scheduled_notification(
  uuid, text, uuid, uuid, uuid, text, timestamptz, jsonb) to app_worker;
grant execute on function app.cancel_orphaned_notifications(uuid) to app_worker;
grant execute on function app.holiday_calendar_dates(uuid) to app_worker;

-- PUBLIC execute is implicit rather than a stored grant, so ALTER DEFAULT
-- PRIVILEGES cannot revoke it. Explicit, every time functions are added.
revoke all on all functions in schema app from public;

-- =============================================================================
-- Verification
-- =============================================================================

do $$
declare
  v_count integer;
begin
  if has_function_privilege(
       'app_api',
       'app.replace_rent_occurrences(uuid, integer, jsonb)', 'execute')
  then
    raise exception 'app_api can write rent occurrences directly; it must not.';
  end if;

  if has_function_privilege(
       'app_api',
       'app.upsert_scheduled_notification(uuid, text, uuid, uuid, uuid, text, timestamptz, jsonb)',
       'execute')
  then
    raise exception 'app_api can schedule notifications directly; it must not.';
  end if;

  -- The worker still holds no privileges on any base table.
  --
  -- Base tables specifically. information_schema.role_table_grants counts
  -- views too, and the worker does hold SELECT on app.queue_health, which is
  -- a read-only monitoring view added in 0014 and is the one thing it is
  -- meant to read directly. The invariant being asserted is that a stolen
  -- worker credential can reach no stored data except through the functions
  -- it is granted, and a view over queue depths is not stored data.
  select count(*) into v_count
  from information_schema.role_table_grants g
  join pg_class c on c.relname = g.table_name
  join pg_namespace n on n.oid = c.relnamespace and n.nspname = g.table_schema
  where g.grantee = 'app_worker'
    and c.relkind = 'r';

  if v_count > 0 then
    raise exception
      'app_worker holds % base-table grant(s). It must reach stored data only through '
      'the functions it is granted.',
      v_count;
  end if;

  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Rent schedule pipeline: grants verified.';
end;
$$;
