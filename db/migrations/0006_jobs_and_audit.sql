-- =============================================================================
-- 0006_jobs_and_audit.sql
-- The durable background job queue, atomic claim functions, append-only audit
-- log, and database-backed rate limiting.
--
-- Claiming is done through SECURITY DEFINER functions rather than raw SQL in
-- the worker. That way the worker role needs EXECUTE on two functions instead
-- of UPDATE on the whole queue, and the atomicity of the claim lives in one
-- reviewable place.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- jobs
-- Document processing, extraction orchestration, schedule regeneration, and
-- cleanup. Notification dispatch has its own queue (scheduled_notifications)
-- because it carries scheduling semantics this table does not.
-- -----------------------------------------------------------------------------
create table jobs (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  kind            app.job_kind not null,
  status          app.job_status not null default 'queued',

  -- What the job acts on. Kept as a loose reference because a job may outlive
  -- the row it points at; the worker re-reads and tolerates a missing target.
  subject_type    text not null,
  subject_id      uuid not null,

  -- Small, non-sensitive parameters only. Never document text, never secrets.
  payload         jsonb not null default '{}'::jsonb,

  -- Lower runs first; ties broken by run_after.
  priority        smallint not null default 100,
  run_after       timestamptz not null default now(),

  attempt_count   integer not null default 0,
  max_attempts    integer not null default 5,

  locked_at       timestamptz,
  locked_by       text,
  lock_expires_at timestamptz,

  started_at      timestamptz,
  finished_at     timestamptz,
  last_error_message text,

  -- Optional deduplication: two identical enqueues collapse into one.
  dedupe_key      text,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint jobs_subject_type_check
    check (subject_type in (
      'lease_document', 'lease_extraction', 'lease',
      'rent_schedule', 'notification_rule', 'organization'
    )),
  constraint jobs_payload_is_object_check
    check (jsonb_typeof(payload) = 'object'),
  -- Keeps a runaway producer from stuffing documents into the queue.
  constraint jobs_payload_size_check
    check (pg_column_size(payload) <= 16384),
  constraint jobs_priority_check
    check (priority between 0 and 1000),
  constraint jobs_attempt_count_check
    check (attempt_count >= 0 and attempt_count <= max_attempts + 1),
  constraint jobs_max_attempts_check
    check (max_attempts between 1 and 20),
  constraint jobs_lock_shape_check
    check (
      (locked_at is null and locked_by is null and lock_expires_at is null)
      or (locked_at is not null and locked_by is not null and lock_expires_at is not null)
    ),
  constraint jobs_claimed_has_lock_check
    check (status <> 'claimed' or locked_at is not null),
  constraint jobs_last_error_length_check
    check (last_error_message is null or char_length(last_error_message) <= 2000),
  constraint jobs_dedupe_key_length_check
    check (dedupe_key is null or char_length(dedupe_key) between 8 and 200)
);

comment on table jobs is
  'Durable background job queue for document processing, extraction, and schedule regeneration.';
comment on column jobs.payload is
  'Small non-sensitive parameters only. Never document text, never credentials.';
comment on column jobs.dedupe_key is
  'Optional key collapsing identical pending enqueues into one job.';

-- Claim hot path.
create index jobs_claimable_idx on jobs (priority, run_after)
  where status = 'queued';
create index jobs_expired_locks_idx on jobs (lock_expires_at)
  where status = 'claimed';
create index jobs_subject_idx on jobs (subject_type, subject_id);
create index jobs_organization_idx on jobs (organization_id, created_at desc);
create index jobs_dead_letter_idx on jobs (organization_id, finished_at desc)
  where status = 'dead_letter';

-- Only one pending job per dedupe key at a time.
create unique index jobs_dedupe_pending_idx on jobs (dedupe_key)
  where dedupe_key is not null and status in ('queued', 'claimed');

create trigger jobs_touch_updated_at
  before update on jobs
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- audit_logs
-- Privileged and legally significant actions: lease term changes, approvals,
-- recipient and rule edits, connected-account changes (section 4.I).
--
-- safe_metadata_json is named to make the rule obvious at the call site: it
-- holds identifiers and before/after summaries, never document text, never
-- tokens, never full email bodies.
-- -----------------------------------------------------------------------------
create table audit_logs (
  id              bigint generated always as identity primary key,
  organization_id uuid not null references organizations (id) on delete cascade,

  actor_user_id   uuid references users (id) on delete set null,
  -- Set when the actor was the worker rather than a person.
  actor_kind      text not null default 'user',

  action          text not null,
  entity_type     text not null,
  entity_id       uuid,

  safe_metadata_json jsonb not null default '{}'::jsonb,

  -- Request correlation, so an audit entry can be tied to structured logs.
  correlation_id  uuid,
  -- Coarse source information. Full IP retention is a policy decision; see
  -- docs/security.md.
  source_ip       inet,

  created_at      timestamptz not null default now(),

  constraint audit_logs_actor_kind_check
    check (actor_kind in ('user', 'worker', 'system')),
  -- A human action must name its actor.
  constraint audit_logs_user_action_has_actor_check
    check (actor_kind <> 'user' or actor_user_id is not null),
  constraint audit_logs_action_shape_check
    check (action ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'
           and char_length(action) <= 120),
  constraint audit_logs_entity_type_length_check
    check (char_length(entity_type) between 1 and 80),
  constraint audit_logs_metadata_is_object_check
    check (jsonb_typeof(safe_metadata_json) = 'object'),
  constraint audit_logs_metadata_size_check
    check (pg_column_size(safe_metadata_json) <= 32768)
);

comment on table audit_logs is
  'Append-only record of privileged actions. Metadata holds identifiers and summaries only.';
comment on column audit_logs.safe_metadata_json is
  'Identifiers and before/after summaries. Never document text, tokens, or email bodies.';
comment on column audit_logs.action is
  'Dotted action name, e.g. lease.approved, email_account.disconnected.';

create index audit_logs_organization_idx on audit_logs (organization_id, created_at desc);
create index audit_logs_entity_idx on audit_logs (entity_type, entity_id, created_at desc);
create index audit_logs_actor_idx on audit_logs (actor_user_id, created_at desc);
create index audit_logs_action_idx on audit_logs (organization_id, action, created_at desc);
create index audit_logs_correlation_idx on audit_logs (correlation_id)
  where correlation_id is not null;

create trigger audit_logs_append_only
  before update or delete on audit_logs
  for each row execute function app.deny_mutation();

-- -----------------------------------------------------------------------------
-- rate_limit_counters
-- Fixed-window counters for login, upload, email, and AI extraction limits
-- (section 4.F). Kept in PostgreSQL because it is already the durable store
-- and the volumes here are small. Redis is deliberately not introduced.
-- -----------------------------------------------------------------------------
create table rate_limit_counters (
  -- Bucket identity, e.g. 'upload:org:<uuid>' or 'login:ip:198.51.100.7'.
  bucket_key      text not null,
  -- Start of the fixed window.
  window_start    timestamptz not null,
  window_seconds  integer not null,
  counter         integer not null default 0,
  updated_at      timestamptz not null default now(),

  primary key (bucket_key, window_start),

  constraint rate_limit_counters_bucket_key_length_check
    check (char_length(bucket_key) between 3 and 200),
  constraint rate_limit_counters_window_seconds_check
    check (window_seconds between 1 and 86400),
  constraint rate_limit_counters_counter_check
    check (counter >= 0)
);

comment on table rate_limit_counters is
  'Fixed-window rate limit counters. Swept by the cleanup job; no external cache needed.';

create index rate_limit_counters_sweep_idx on rate_limit_counters (window_start);

-- Atomically increments a window and reports whether the limit is now exceeded.
create or replace function app.consume_rate_limit(
  p_bucket_key     text,
  p_window_seconds integer,
  p_limit          integer,
  p_cost           integer default 1
)
returns table (allowed boolean, current_count integer, window_start timestamptz)
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_window_start timestamptz;
  v_count        integer;
begin
  if p_window_seconds < 1 or p_limit < 0 or p_cost < 1 then
    raise exception 'Invalid rate limit parameters'
      using errcode = 'invalid_parameter_value';
  end if;

  -- Floor "now" to the start of its fixed window.
  v_window_start := to_timestamp(
    floor(extract(epoch from clock_timestamp()) / p_window_seconds) * p_window_seconds
  );

  insert into rate_limit_counters as c
    (bucket_key, window_start, window_seconds, counter)
  values
    (p_bucket_key, v_window_start, p_window_seconds, p_cost)
  on conflict (bucket_key, window_start) do update
    set counter = c.counter + p_cost,
        updated_at = now()
  returning c.counter into v_count;

  return query select (v_count <= p_limit), v_count, v_window_start;
end;
$fn$;

comment on function app.consume_rate_limit(text, integer, integer, integer) is
  'Atomically increments a fixed-window counter and reports whether the limit is exceeded.';

-- -----------------------------------------------------------------------------
-- Atomic claim functions
--
-- FOR UPDATE SKIP LOCKED lets several workers claim disjoint batches without
-- blocking each other. The transaction here is deliberately tiny: it claims
-- rows and returns. Gmail calls, OCR, and AI requests all happen afterwards,
-- outside any database lock (section 7).
-- -----------------------------------------------------------------------------

create or replace function app.claim_jobs(
  p_worker_id      text,
  p_kinds          app.job_kind[],
  p_batch_size     integer default 5,
  p_lock_seconds   integer default 300
)
returns setof jobs
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if p_batch_size < 1 or p_batch_size > 100 then
    raise exception 'Batch size must be between 1 and 100'
      using errcode = 'invalid_parameter_value';
  end if;
  if p_worker_id is null or char_length(p_worker_id) not between 1 and 200 then
    raise exception 'A worker id between 1 and 200 characters is required'
      using errcode = 'invalid_parameter_value';
  end if;

  return query
  with claimable as (
    select j.id
    from jobs j
    where j.kind = any (p_kinds)
      and (
        -- Fresh work whose time has come.
        (j.status = 'queued' and j.run_after <= now())
        -- Or work abandoned by a worker that died mid-job.
        or (j.status = 'claimed' and j.lock_expires_at < now())
      )
    order by j.priority, j.run_after
    limit p_batch_size
    for update of j skip locked
  )
  update jobs j
  set status          = 'claimed',
      locked_at       = now(),
      locked_by       = p_worker_id,
      lock_expires_at = now() + make_interval(secs => p_lock_seconds),
      attempt_count   = j.attempt_count + 1,
      started_at      = coalesce(j.started_at, now()),
      updated_at      = now()
  from claimable c
  where j.id = c.id
  returning j.*;
end;
$fn$;

comment on function app.claim_jobs(text, app.job_kind[], integer, integer) is
  'Atomically claims a batch of due jobs with SKIP LOCKED, reclaiming expired locks.';

create or replace function app.claim_due_notifications(
  p_worker_id    text,
  p_batch_size   integer default 20,
  p_lock_seconds integer default 120
)
returns setof scheduled_notifications
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if p_batch_size < 1 or p_batch_size > 200 then
    raise exception 'Batch size must be between 1 and 200'
      using errcode = 'invalid_parameter_value';
  end if;

  return query
  with claimable as (
    select n.id
    from scheduled_notifications n
    join leases l
      on l.id = n.lease_id
     and l.organization_id = n.organization_id
    join notification_rules r
      on r.id = n.notification_rule_id
     and r.organization_id = n.organization_id
    join email_accounts a
      on a.id = n.email_account_id
     and a.organization_id = n.organization_id
    where (
        (n.status = 'scheduled' and n.scheduled_at_utc <= now())
        or (n.status = 'failed'
            and n.next_attempt_at is not null
            and n.next_attempt_at <= now()
            and n.attempt_count < n.max_attempts)
        or (n.status = 'claimed' and n.lock_expires_at < now())
      )
      -- Every send precondition is re-checked here, at claim time, not only
      -- when the notification was generated. A lease approved last week and
      -- paused this morning does not send.
      and l.approval_status = 'approved'
      and l.automation_status = 'active'
      and l.lifecycle_status in ('upcoming', 'active')
      and r.enabled
      and r.paused_at is null
      -- A disconnected or revoked Gmail account stops sending outright; there
      -- is deliberately no fallback sender (section 13.B).
      and a.connection_status = 'connected'
    order by n.scheduled_at_utc
    limit p_batch_size
    for update of n skip locked
  )
  update scheduled_notifications n
  set status          = 'claimed',
      locked_at       = now(),
      locked_by       = p_worker_id,
      lock_expires_at = now() + make_interval(secs => p_lock_seconds),
      attempt_count   = n.attempt_count + 1,
      updated_at      = now()
  from claimable c
  where n.id = c.id
  returning n.*;
end;
$fn$;

comment on function app.claim_due_notifications(text, integer, integer) is
  'Atomically claims due notifications, re-checking approval, automation, rule, and Gmail state at claim time.';

-- -----------------------------------------------------------------------------
-- Health view for monitoring (section 4.K)
-- -----------------------------------------------------------------------------
create or replace view app.queue_health as
select
  'jobs'::text as queue,
  count(*) filter (where status = 'queued' and run_after <= now()) as due_now,
  count(*) filter (where status = 'claimed' and lock_expires_at < now()) as stale_locks,
  count(*) filter (where status = 'dead_letter') as dead_letter,
  min(run_after) filter (where status = 'queued') as oldest_due
from jobs
union all
select
  'scheduled_notifications'::text as queue,
  count(*) filter (where status = 'scheduled' and scheduled_at_utc <= now()) as due_now,
  count(*) filter (where status = 'claimed' and lock_expires_at < now()) as stale_locks,
  count(*) filter (where status = 'dead_letter') as dead_letter,
  min(scheduled_at_utc) filter (where status = 'scheduled') as oldest_due
from scheduled_notifications;

comment on view app.queue_health is
  'Queue depth, stale locks, and dead-letter counts for alerting on worker downtime and backlogs.';
