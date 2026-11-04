-- =============================================================================
-- 0024_fix_schedule_claim_types.sql
--
-- Fixes a column type mismatch in app.claim_leases_for_schedule_generation.
--
-- The function declares grace_period_days and late_fee_after_days as integer
-- in its RETURNS TABLE, but leases stores both as smallint. PostgreSQL does
-- not widen them implicitly for a set-returning function's output, so every
-- call failed at runtime with:
--
--   42804: structure of query does not match function result type
--
-- Nothing caught this earlier because the function parses, the grants verify,
-- and the SQL suites never call it: it only runs when the worker finds an
-- approved lease with an active rent schedule. The first real worker start
-- surfaced it immediately, once per poll.
--
-- The declared types stay integer rather than changing to smallint. A grace
-- period is read into an Int32 on the way out, and a function signature that
-- tracks a column's storage width is a signature that changes whenever the
-- column does. The cast belongs in the query.
-- =============================================================================

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
as $fn$
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
    -- The fix. Both are smallint on leases; the declared output is integer,
    -- and a set-returning function will not widen them for you.
    l.grace_period_days::integer, l.late_fee_after_days::integer
  from leases l
  join rent_schedules s
    on s.lease_id = l.id and s.organization_id = l.organization_id and s.active
  where
    -- Only an approved lease. Section 13.C again: an unapproved lease must
    -- not have a schedule generated for it, because a schedule is what
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
$fn$;

comment on function app.claim_leases_for_schedule_generation is
  'Finds approved leases whose rent occurrences are missing or no longer reach the horizon.';

-- The grant is on the signature, which has not changed, so CREATE OR REPLACE
-- preserves it. Asserted rather than assumed: a lost grant here would stop
-- rent generation silently.
do $$
begin
  if not has_function_privilege(
       'app_worker',
       'app.claim_leases_for_schedule_generation(integer, integer)',
       'execute')
  then
    raise exception 'app_worker lost EXECUTE on the schedule claim function.';
  end if;

  if has_function_privilege(
       'app_api',
       'app.claim_leases_for_schedule_generation(integer, integer)',
       'execute')
  then
    raise exception 'app_api can claim schedules; only the worker may.';
  end if;

  raise notice 'Schedule claim: result types corrected, grants intact.';
end;
$$;

-- -----------------------------------------------------------------------------
-- A regression test for the thing that actually broke.
--
-- The failure was not in the logic but in the function's shape, and calling it
-- is the only way to find that. One call against an empty result set is enough:
-- PostgreSQL checks the row descriptor before it has any rows to return.
-- -----------------------------------------------------------------------------
do $$
declare
  v_count integer;
begin
  select count(*) into v_count from app.claim_leases_for_schedule_generation(1, 400);

  raise notice 'Schedule claim executed successfully (% row(s) matched).', v_count;
end;
$$;
