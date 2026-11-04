-- =============================================================================
-- 0014_task_notifications_and_outbox.sql
-- Task notification rules, the transactional outbox, and the unified
-- notification centre.
--
-- -----------------------------------------------------------------------------
-- WHY AN OUTBOX
--
-- The requirement is that a task change and its notification cannot come
-- apart. Three obvious approaches all fail:
--
--   Send the email inside the transaction.
--       The transaction now lasts as long as a Gmail call. Worse, if the
--       transaction later rolls back, the email has already gone and cannot be
--       recalled. People receive "your task was assigned" for a task that does
--       not exist.
--
--   Send the email after committing.
--       The window between commit and send is unprotected. A crash, a deploy,
--       or an OOM kill in that window loses the notification permanently, with
--       nothing anywhere recording that it was owed.
--
--   Fire a database trigger that calls out to the network.
--       Now the network is inside the transaction, with all of problem one,
--       plus a failure mode where a slow provider blocks writes to the tasks
--       table.
--
-- The outbox resolves it: the event row is INSERTed in the same transaction as
-- the task change, so the two commit or roll back together. A worker then
-- claims pending rows and does the delivery work afterwards. The event is
-- durable from the instant the change is durable, and not one moment before.
--
-- This is the same reliability property the rent dispatcher gets from
-- scheduled_notifications, reached the same way: PostgreSQL is the queue.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Enum types
-- -----------------------------------------------------------------------------

-- What happened. Each value is something a person might want to be told about.
create type app.task_event_type as enum (
  'task_created',
  'task_assigned',
  'task_unassigned',
  'task_reassigned',
  'task_status_changed',
  'task_priority_changed',
  'task_due_date_changed',
  'task_comment_added',
  'task_attachment_added',
  'task_completed',
  'task_reopened',
  'task_cancelled',
  'task_cost_updated',
  -- Time-based, raised by the worker's sweep rather than by a user action.
  'task_due_soon',
  'task_due_today',
  'task_overdue',
  'task_stale'
);

-- An event rule fires on a change; a schedule rule fires on a date.
create type app.task_rule_trigger as enum ('event', 'schedule');

-- What a schedule rule measures from.
create type app.task_schedule_basis as enum (
  'before_due',   -- N days before the due date
  'on_due',       -- on the due date
  'after_due',    -- N days after the due date, repeatable for escalation
  'no_activity'   -- N days since last_activity_at
);

-- Who to tell. Resolved at send time against the task, so a rule saying
-- "notify the assignee" follows a reassignment without being rewritten.
create type app.task_recipient_role as enum (
  'assignee',
  'task_creator',
  'reporting_tenant',
  'property_manager',
  'property_owner',
  'vendor',
  'custom_recipient',
  'recipient_group'
);

create type app.outbox_status as enum (
  'pending', 'claimed', 'processed', 'failed', 'dead_letter'
);

create type app.delivery_channel as enum ('email', 'in_app');

create type app.delivery_status as enum (
  'pending', 'sent', 'failed', 'suppressed', 'skipped'
);

-- -----------------------------------------------------------------------------
-- task_recipients
-- People attached to one specific task, beyond those the roles already reach.
-- -----------------------------------------------------------------------------
create table task_recipients (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  task_id         uuid not null,

  -- Exactly one of the three.
  user_id         uuid,
  recipient_id    uuid,
  vendor_id       uuid,

  -- True when this person asked to follow the task rather than being assigned
  -- to it. A watcher gets status changes and comments, not assignment notices.
  is_watcher      boolean not null default false,

  added_at        timestamptz not null default now(),
  added_by_user_id uuid references users (id) on delete set null,

  constraint task_recipients_task_fk
    foreign key (task_id, organization_id)
    references tasks (id, organization_id) on delete cascade,
  constraint task_recipients_recipient_fk
    foreign key (recipient_id, organization_id)
    references recipients (id, organization_id) on delete cascade,
  constraint task_recipients_vendor_fk
    foreign key (vendor_id, organization_id)
    references vendors (id, organization_id) on delete cascade,
  constraint task_recipients_user_fk
    foreign key (user_id) references users (id) on delete cascade,

  constraint task_recipients_exactly_one_target_check
    check (
      (case when user_id is not null then 1 else 0 end
       + case when recipient_id is not null then 1 else 0 end
       + case when vendor_id is not null then 1 else 0 end) = 1
    )
);

comment on table task_recipients is
  'Extra people attached to one task: watchers and ad-hoc participants.';

create unique index task_recipients_user_key on task_recipients (task_id, user_id)
  where user_id is not null;
create unique index task_recipients_recipient_key on task_recipients (task_id, recipient_id)
  where recipient_id is not null;
create unique index task_recipients_vendor_key on task_recipients (task_id, vendor_id)
  where vendor_id is not null;
create index task_recipients_organization_idx on task_recipients (organization_id);

-- -----------------------------------------------------------------------------
-- task_notification_rules
--
-- Configurable per organization and, optionally, per property. The same
-- three-level inheritance idea as the rent rules, with one level fewer: a
-- per-task override would be configuration nobody maintains, and the watcher
-- list already covers the per-task case.
-- -----------------------------------------------------------------------------
create table task_notification_rules (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  property_id     uuid,

  name            text not null,

  trigger_kind    app.task_rule_trigger not null,

  -- For an event rule.
  event_type      app.task_event_type,

  -- For a schedule rule.
  schedule_basis  app.task_schedule_basis,
  schedule_offset_days integer,
  -- Repeat daily while the condition holds, for escalation. Bounded, so a
  -- forgotten overdue task cannot generate mail forever.
  repeat_daily    boolean not null default false,
  max_repeats     integer,

  -- Narrowing. Null means "any".
  only_priorities app.task_priority[],
  only_categories app.task_category[],
  only_status_categories app.task_status_category[],

  -- Delivery.
  channels        app.delivery_channel[] not null default '{email,in_app}',
  email_template_id uuid,
  send_local_time time,
  timezone        text,

  enabled         boolean not null default true,

  -- Guard rails, matching the rent rules.
  max_sends_per_task_per_day integer not null default 5,

  paused_at       timestamptz,
  paused_reason   text,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,

  constraint task_notification_rules_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete cascade,
  constraint task_notification_rules_template_fk
    foreign key (email_template_id, organization_id)
    references email_templates (id, organization_id) on delete restrict,

  constraint task_notification_rules_name_length_check
    check (char_length(name) between 1 and 160),

  -- An event rule needs an event and no schedule; a schedule rule the
  -- reverse. Mixing them would produce a rule with no defined firing moment.
  constraint task_notification_rules_event_shape_check
    check (
      (trigger_kind = 'event'
         and event_type is not null
         and schedule_basis is null
         and schedule_offset_days is null)
      or
      (trigger_kind = 'schedule'
         and schedule_basis is not null
         and event_type is null)
    ),

  -- on_due measures from the date itself, so an offset is meaningless there.
  constraint task_notification_rules_offset_shape_check
    check (
      trigger_kind <> 'schedule'
      or (schedule_basis = 'on_due' and coalesce(schedule_offset_days, 0) = 0)
      or (schedule_basis <> 'on_due'
            and schedule_offset_days between 1 and 365)
    ),

  constraint task_notification_rules_repeat_shape_check
    check (
      not repeat_daily
      or (schedule_basis in ('after_due', 'no_activity')
            and max_repeats between 1 and 60)
    ),

  constraint task_notification_rules_channels_check
    check (array_length(channels, 1) between 1 and 2),
  constraint task_notification_rules_priorities_check
    check (only_priorities is null or array_length(only_priorities, 1) between 1 and 4),
  constraint task_notification_rules_max_sends_check
    check (max_sends_per_task_per_day between 1 and 50),
  constraint task_notification_rules_paused_metadata_check
    check ((paused_at is null) = (paused_reason is null)),

  constraint task_notification_rules_id_organization_key unique (id, organization_id)
);

comment on table task_notification_rules is
  'Event and deadline notification rules for tasks. Each configurable independently.';
comment on column task_notification_rules.repeat_daily is
  'Daily escalation while the condition holds, bounded by max_repeats so it cannot run forever.';
comment on column task_notification_rules.channels is
  'Email, in-app, or both. In-app alone is the quiet option for low-priority noise.';

create index task_notification_rules_event_idx
  on task_notification_rules (organization_id, event_type)
  where enabled and trigger_kind = 'event';
create index task_notification_rules_schedule_idx
  on task_notification_rules (organization_id, schedule_basis)
  where enabled and trigger_kind = 'schedule';
create index task_notification_rules_property_idx
  on task_notification_rules (property_id) where property_id is not null;

create trigger task_notification_rules_touch_updated_at
  before update on task_notification_rules
  for each row execute function app.touch_updated_at();

create trigger task_notification_rules_validate_timezone
  before insert or update of timezone on task_notification_rules
  for each row execute function app.validate_timezone_column('timezone');

-- -----------------------------------------------------------------------------
-- task_notification_rule_recipients
-- Which roles and addresses a rule notifies. Roles are resolved at send time
-- against the task, so "the assignee" follows a reassignment.
-- -----------------------------------------------------------------------------
create table task_notification_rule_recipients (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  task_notification_rule_id uuid not null,

  recipient_role  app.task_recipient_role not null,

  -- Required only for the two explicit roles.
  recipient_id    uuid,
  recipient_group_id uuid,

  recipient_kind  app.recipient_kind not null default 'to',

  created_at      timestamptz not null default now(),

  constraint task_notification_rule_recipients_rule_fk
    foreign key (task_notification_rule_id, organization_id)
    references task_notification_rules (id, organization_id) on delete cascade,
  constraint task_notification_rule_recipients_recipient_fk
    foreign key (recipient_id, organization_id)
    references recipients (id, organization_id) on delete cascade,
  constraint task_notification_rule_recipients_group_fk
    foreign key (recipient_group_id, organization_id)
    references recipient_groups (id, organization_id) on delete cascade,

  constraint task_notification_rule_recipients_target_shape_check
    check (
      (recipient_role = 'custom_recipient'
         and recipient_id is not null and recipient_group_id is null)
      or (recipient_role = 'recipient_group'
         and recipient_group_id is not null and recipient_id is null)
      or (recipient_role not in ('custom_recipient', 'recipient_group')
         and recipient_id is null and recipient_group_id is null)
    )
);

comment on table task_notification_rule_recipients is
  'Rule targets. A role is resolved against the task at send time, so it follows reassignment.';

create index task_notification_rule_recipients_rule_idx
  on task_notification_rule_recipients (task_notification_rule_id);
create index task_notification_rule_recipients_organization_idx
  on task_notification_rule_recipients (organization_id);

-- -----------------------------------------------------------------------------
-- notification_outbox
--
-- The durable event log. One row per thing that happened, written in the same
-- transaction as the change itself.
--
-- Deliberately generic: `aggregate_type` is 'task' today, and will be 'lease'
-- and 'document' as later phases raise events. One outbox and one worker loop
-- is simpler to reason about, and to monitor, than one per module.
-- -----------------------------------------------------------------------------
create table notification_outbox (
  id              bigint generated always as identity primary key,
  organization_id uuid not null references organizations (id) on delete cascade,

  aggregate_type  text not null,
  aggregate_id    uuid not null,

  event_type      text not null,

  -- Everything the rule engine needs to decide recipients and render a
  -- message, captured AT THE MOMENT OF THE EVENT.
  --
  -- Snapshotting rather than re-reading matters: by the time the worker runs,
  -- the task may have moved again. "Moved from New to In Progress" has to
  -- describe what happened, not what is true now. Identifiers, names, and
  -- changed values only; never a photo or a document.
  payload         jsonb not null,

  -- Who caused it, for "X assigned this to you" and for suppressing the
  -- actor's own notification.
  actor_user_id   uuid references users (id) on delete set null,
  actor_display_name text,

  occurred_at     timestamptz not null default now(),

  status          app.outbox_status not null default 'pending',
  attempt_count   integer not null default 0,
  max_attempts    integer not null default 5,
  next_attempt_at timestamptz,

  locked_at       timestamptz,
  locked_by       text,
  lock_expires_at timestamptz,

  processed_at    timestamptz,
  last_error_message text,

  -- Deterministic, so an event raised twice by a retried request collapses to
  -- one row instead of two notifications.
  idempotency_key text not null,

  constraint notification_outbox_aggregate_type_check
    check (aggregate_type in ('task', 'lease', 'lease_document', 'vendor')),
  constraint notification_outbox_event_type_check
    check (event_type ~ '^[a-z][a-z0-9_]*$' and char_length(event_type) <= 60),
  constraint notification_outbox_payload_is_object_check
    check (jsonb_typeof(payload) = 'object'),
  -- Bounded, so a malformed producer cannot put a document in the queue.
  constraint notification_outbox_payload_size_check
    check (pg_column_size(payload) <= 65536),
  constraint notification_outbox_attempt_count_check
    check (attempt_count >= 0 and attempt_count <= max_attempts + 1),
  constraint notification_outbox_max_attempts_check
    check (max_attempts between 1 and 20),
  constraint notification_outbox_lock_shape_check
    check (
      (locked_at is null and locked_by is null and lock_expires_at is null)
      or (locked_at is not null and locked_by is not null and lock_expires_at is not null)
    ),
  constraint notification_outbox_claimed_has_lock_check
    check (status <> 'claimed' or locked_at is not null),
  constraint notification_outbox_processed_metadata_check
    check ((status = 'processed') = (processed_at is not null)),
  constraint notification_outbox_error_length_check
    check (last_error_message is null or char_length(last_error_message) <= 2000),
  constraint notification_outbox_idempotency_key_shape_check
    check (char_length(idempotency_key) between 8 and 200),

  constraint notification_outbox_idempotency_key_unique
    unique (organization_id, idempotency_key)
);

comment on table notification_outbox is
  'Durable event log, written in the same transaction as the change. A worker processes it afterwards.';
comment on column notification_outbox.payload is
  'Snapshot at the moment of the event, so a message describes what happened rather than what is now true.';
comment on column notification_outbox.idempotency_key is
  'Deterministic, so a retried request produces one event rather than two notifications.';

-- The worker's hot path: pending work, oldest first. Ordering by occurred_at
-- rather than id keeps a backfilled event in its logical place.
create index notification_outbox_pending_idx
  on notification_outbox (occurred_at)
  where status = 'pending';
create index notification_outbox_retry_idx
  on notification_outbox (next_attempt_at)
  where status = 'failed';
create index notification_outbox_expired_locks_idx
  on notification_outbox (lock_expires_at)
  where status = 'claimed';
create index notification_outbox_aggregate_idx
  on notification_outbox (aggregate_type, aggregate_id, occurred_at desc);
create index notification_outbox_dead_letter_idx
  on notification_outbox (organization_id, occurred_at desc)
  where status = 'dead_letter';

-- -----------------------------------------------------------------------------
-- notification_deliveries
--
-- One row per recipient per channel. This is both the delivery record and the
-- unified notification centre: an in-app notification IS a delivery with
-- channel = 'in_app'.
--
-- Keeping them in one table rather than two means the centre shows rent
-- reminders, lease expiries, and task updates together without a union across
-- three schemas, and "was this person told?" has one answer.
-- -----------------------------------------------------------------------------
create table notification_deliveries (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  -- Provenance: which event or scheduled rent notification produced this.
  -- Exactly one is set, which is how the centre can mix both sources.
  outbox_id       bigint,
  scheduled_notification_id uuid,

  -- What it is about, for the link the centre renders.
  subject_type    text not null,
  subject_id      uuid,

  channel         app.delivery_channel not null,

  -- The addressee. A user for in-app; an email address for email. A vendor
  -- without a login gets email only.
  recipient_user_id uuid references users (id) on delete cascade,
  recipient_email citext,

  -- Why this person was chosen, for a "why am I getting this?" answer.
  recipient_role  app.task_recipient_role,
  task_notification_rule_id uuid,

  title           text not null,
  body            text not null,
  -- Relative path within the application, for example /maintenance/142.
  link_path       text,

  status          app.delivery_status not null default 'pending',
  error_category  app.email_error_category not null default 'none',
  error_message   text,

  -- Set when a provider accepted the email. Named for what it means.
  provider_message_id text,
  provider_accepted_at timestamptz,

  -- In-app read state. Null means unread.
  read_at         timestamptz,
  -- Dismissed without being opened, so the badge clears but it stays in history.
  dismissed_at    timestamptz,

  created_at      timestamptz not null default now(),

  -- Deterministic, so re-processing an outbox row cannot notify the same
  -- person twice through the same channel. This constraint, not application
  -- logic, is what actually prevents a duplicate.
  idempotency_key text not null,

  constraint notification_deliveries_outbox_fk
    foreign key (outbox_id) references notification_outbox (id) on delete set null,
  constraint notification_deliveries_scheduled_fk
    foreign key (scheduled_notification_id, organization_id)
    references scheduled_notifications (id, organization_id) on delete set null,
  constraint notification_deliveries_rule_fk
    foreign key (task_notification_rule_id, organization_id)
    references task_notification_rules (id, organization_id) on delete set null,

  constraint notification_deliveries_subject_type_check
    check (subject_type in (
      'task', 'lease', 'rent_occurrence', 'lease_document', 'vendor', 'system'
    )),

  -- An email delivery needs an address; an in-app delivery needs a user.
  -- Without this, a delivery could exist that has no possible destination.
  constraint notification_deliveries_destination_check
    check (
      (channel = 'email' and recipient_email is not null)
      or (channel = 'in_app' and recipient_user_id is not null)
    ),
  constraint notification_deliveries_email_shape_check
    check (recipient_email is null or
           recipient_email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),

  constraint notification_deliveries_title_length_check
    check (char_length(title) between 1 and 300),
  constraint notification_deliveries_body_length_check
    check (char_length(body) between 1 and 5000),
  -- Relative only. An absolute URL here would make the notification centre an
  -- open-redirect surface: a notification is a link somebody clicks from an
  -- email, which is exactly where a redirect to a lookalike sign-in page would
  -- be most effective.
  --
  -- The length is a separate check rather than a bounded repetition, because
  -- PostgreSQL caps a regex repetition count at 255 and {0,300} is rejected at
  -- CHECK evaluation time with "invalid repetition count".
  constraint notification_deliveries_link_path_check
    check (link_path is null or link_path ~ '^/[A-Za-z0-9/_?=&.-]*$'),
  constraint notification_deliveries_link_path_length_check
    check (link_path is null or char_length(link_path) <= 300),

  constraint notification_deliveries_sent_metadata_check
    check ((status = 'sent') = (provider_accepted_at is not null) or channel = 'in_app'),
  constraint notification_deliveries_error_length_check
    check (error_message is null or char_length(error_message) <= 2000),
  -- Read state only applies in-app.
  constraint notification_deliveries_read_state_check
    check (channel = 'in_app' or (read_at is null and dismissed_at is null)),

  constraint notification_deliveries_idempotency_key_unique
    unique (organization_id, idempotency_key)
);

comment on table notification_deliveries is
  'One row per recipient per channel. Doubles as the unified notification centre: in-app is a delivery.';
comment on column notification_deliveries.recipient_role is
  'Why this person was chosen, so the UI can answer "why am I getting this?".';
comment on column notification_deliveries.link_path is
  'Relative path only. An absolute URL would make the notification centre an open redirect.';

-- The notification centre: one user's unread items, newest first.
create index notification_deliveries_inbox_idx
  on notification_deliveries (recipient_user_id, created_at desc)
  where channel = 'in_app' and read_at is null and dismissed_at is null;
-- The full history for one user.
create index notification_deliveries_user_history_idx
  on notification_deliveries (recipient_user_id, created_at desc)
  where channel = 'in_app';
-- Pending email, for the dispatcher.
create index notification_deliveries_pending_email_idx
  on notification_deliveries (created_at)
  where channel = 'email' and status = 'pending';
create index notification_deliveries_subject_idx
  on notification_deliveries (subject_type, subject_id, created_at desc);
create index notification_deliveries_outbox_idx on notification_deliveries (outbox_id)
  where outbox_id is not null;
create index notification_deliveries_organization_idx
  on notification_deliveries (organization_id, created_at desc);
-- Per-task per-day volume checks.
create index notification_deliveries_rate_idx
  on notification_deliveries (organization_id, subject_id, created_at)
  where channel = 'email';

-- -----------------------------------------------------------------------------
-- A delivery is history
--
-- Its content must never change: a notification that said one thing and later
-- says another is worse than no record at all. Read and dismissed state are
-- the only mutable fields, because they belong to the reader rather than to
-- the event.
-- -----------------------------------------------------------------------------
create or replace function app.protect_delivery_content()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  if new.title is distinct from old.title
     or new.body is distinct from old.body
     or new.channel is distinct from old.channel
     or new.recipient_user_id is distinct from old.recipient_user_id
     or new.recipient_email is distinct from old.recipient_email
     or new.subject_type is distinct from old.subject_type
     or new.subject_id is distinct from old.subject_id
     or new.idempotency_key is distinct from old.idempotency_key
     or new.created_at is distinct from old.created_at then
    raise exception
      'A notification delivery is history. Only read_at, dismissed_at, and the '
      'send result may change.'
      using errcode = 'integrity_constraint_violation';
  end if;

  return new;
end;
$fn$;

comment on function app.protect_delivery_content() is
  'Keeps a delivery immutable except for read state and the send result.';

create trigger notification_deliveries_protect_content
  before update on notification_deliveries
  for each row execute function app.protect_delivery_content();

-- -----------------------------------------------------------------------------
-- Queue health, extended
--
-- Replaces the view from 0006 so the outbox and the delivery queue appear in
-- the same heartbeat the worker already logs, rather than needing a second
-- alert to be configured and remembered.
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
  'scheduled_notifications'::text,
  count(*) filter (where status = 'scheduled' and scheduled_at_utc <= now()),
  count(*) filter (where status = 'claimed' and lock_expires_at < now()),
  count(*) filter (where status = 'dead_letter'),
  min(scheduled_at_utc) filter (where status = 'scheduled')
from scheduled_notifications

union all
select
  'notification_outbox'::text,
  count(*) filter (where status = 'pending'),
  count(*) filter (where status = 'claimed' and lock_expires_at < now()),
  count(*) filter (where status = 'dead_letter'),
  min(occurred_at) filter (where status = 'pending')
from notification_outbox

union all
select
  'notification_deliveries'::text,
  count(*) filter (where status = 'pending' and channel = 'email'),
  0::bigint,
  count(*) filter (where status = 'failed' and channel = 'email'),
  min(created_at) filter (where status = 'pending' and channel = 'email')
from notification_deliveries;

comment on view app.queue_health is
  'Depth, stale locks, and dead-letter counts for all four queues, for one alerting signal.';
