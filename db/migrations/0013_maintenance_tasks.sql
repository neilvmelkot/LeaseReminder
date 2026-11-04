-- =============================================================================
-- 0013_maintenance_tasks.sql
-- Maintenance and operations tasks: the Kanban board.
--
-- Three design decisions carry most of the weight here, and each is worth
-- stating before the tables.
--
-- 1. A COLUMN HAS A CATEGORY, SEPARATE FROM ITS NAME.
--    Users can rename columns and add their own ("Waiting for Parts",
--    "Awaiting Tenant", "Inspection Required"). Automation cannot be written
--    against a name, because a board whose last column is called "Done",
--    "Closed", or "Finito" must still stop sending overdue reminders. So every
--    column declares a category from a fixed set, and every rule, every
--    overdue check, and every metric reads the category. The name is for
--    people; the category is for code.
--
-- 2. POSITION IS FRACTIONAL.
--    Dragging a card between two others has to be one UPDATE, not a renumber
--    of the whole column. With a numeric position, a card dropped between 2.0
--    and 3.0 becomes 2.5 and nothing else is touched. The alternative, integer
--    positions with a full rewrite, turns one drag into forty writes and makes
--    two people reordering at once a conflict instead of a merge.
--
-- 3. A TASK IS NOT A LEASE.
--    Tasks hang off a property and optionally a unit, NOT off a lease. A
--    leaking roof outlives the tenancy that reported it, and binding the task
--    to a lease would orphan it at renewal. The reporting tenant is recorded
--    separately, as a fact about who raised it.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Enum types
-- -----------------------------------------------------------------------------

create type app.task_priority as enum ('urgent', 'high', 'medium', 'low');

-- The fixed semantics behind a user-renameable column (decision 1 above).
create type app.task_status_category as enum (
  'backlog',      -- captured, not yet triaged
  'new',          -- triaged, awaiting assignment
  'assigned',     -- has an owner, not started
  'in_progress',  -- being worked
  'blocked',      -- waiting on a part, a tenant, an approval
  'review',       -- done, awaiting inspection or sign-off
  'completed',    -- finished
  'cancelled'     -- abandoned; not completed, and must not count as such
);

create type app.task_category as enum (
  'plumbing', 'electrical', 'hvac', 'appliance', 'structural',
  'roofing', 'pest', 'landscaping', 'cleaning', 'safety',
  'inspection', 'turnover', 'administrative', 'other'
);

-- Who raised it. Affects what the reporter is told, and how much.
create type app.task_source as enum (
  'tenant_report', 'inspection', 'preventive_maintenance',
  'staff', 'vendor', 'system'
);

-- -----------------------------------------------------------------------------
-- vendors
-- Contractors and suppliers. Kept as records rather than free text on a task,
-- so a phone number is entered once and a task can be reassigned without
-- retyping it.
-- -----------------------------------------------------------------------------
create table vendors (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  name            text not null,
  company         text,
  email           citext,
  phone           text,
  trades          app.task_category[] not null default '{}',
  hourly_rate     numeric(12, 2),
  currency        char(3) not null default 'USD',
  notes           text,
  active          boolean not null default true,

  -- Insurance expiry is a real operational date a manager needs reminding
  -- about, so it sits here rather than in a note.
  insurance_expires_on date,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint vendors_name_length_check
    check (char_length(name) between 1 and 200),
  constraint vendors_company_length_check
    check (company is null or char_length(company) <= 200),
  constraint vendors_email_shape_check
    check (email is null or
           email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint vendors_phone_length_check
    check (phone is null or char_length(phone) between 3 and 40),
  constraint vendors_hourly_rate_check
    check (hourly_rate is null or (hourly_rate >= 0 and hourly_rate <= 99999999.99)),
  constraint vendors_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  constraint vendors_notes_length_check
    check (notes is null or char_length(notes) <= 10000),
  constraint vendors_trades_bounded_check
    check (array_length(trades, 1) is null or array_length(trades, 1) <= 14),

  constraint vendors_id_organization_key unique (id, organization_id)
);

comment on table vendors is
  'Contractors and suppliers. Referenced by tasks so contact details are entered once.';
comment on column vendors.trades is
  'Which task categories this vendor handles, for suggesting an assignee.';

create index vendors_organization_idx on vendors (organization_id, name)
  where active;
create index vendors_insurance_expiry_idx on vendors (organization_id, insurance_expires_on)
  where active and insurance_expires_on is not null;

create trigger vendors_touch_updated_at
  before update on vendors
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- task_columns
-- The board's columns, owned by the organization rather than by a board.
--
-- Columns are organization-wide and the "portfolio board" and "property board"
-- are two filters over the same set. The alternative, per-property column sets,
-- was rejected: a manager moving between properties would face a different
-- workflow on each, and a cross-property view could not be rendered at all
-- without inventing a merge rule.
-- -----------------------------------------------------------------------------
create table task_columns (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  name            text not null,
  -- The fixed meaning behind the name. See decision 1.
  category        app.task_status_category not null,

  position        numeric(20, 10) not null,
  -- Optional cap, shown as a warning on the board. Advisory, not enforced:
  -- refusing to accept an urgent repair because a column is full would be
  -- worse than the overload it prevents.
  work_in_progress_limit integer,

  -- Hex colour for the column header accent.
  colour          text,
  -- Collapsed by default, for a column like Completed that is usually noise.
  collapsed_by_default boolean not null default false,

  -- Set on exactly one column: where a newly created task lands.
  is_default_intake boolean not null default false,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint task_columns_name_length_check
    check (char_length(name) between 1 and 60),
  constraint task_columns_position_check
    check (position > 0),
  constraint task_columns_wip_limit_check
    check (work_in_progress_limit is null
           or work_in_progress_limit between 1 and 1000),
  constraint task_columns_colour_check
    check (colour is null or colour ~ '^#[0-9a-fA-F]{6}$'),

  constraint task_columns_organization_name_key unique (organization_id, name),
  constraint task_columns_id_organization_key unique (id, organization_id)
);

comment on table task_columns is
  'Board columns. Renameable; automation reads category, never name.';
comment on column task_columns.category is
  'Fixed semantics behind a user-chosen name, so rules survive a rename.';
comment on column task_columns.position is
  'Fractional, so inserting between two columns is one UPDATE.';

-- Exactly one intake column per organization, so task creation never has to
-- guess where a card goes.
create unique index task_columns_single_intake_idx
  on task_columns (organization_id)
  where is_default_intake;

create index task_columns_order_idx on task_columns (organization_id, position);

create trigger task_columns_touch_updated_at
  before update on task_columns
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- tasks
-- -----------------------------------------------------------------------------
create table tasks (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  -- Human-facing sequential reference, unique within the organization. People
  -- say "task 142" on the phone; nobody reads a UUID aloud.
  reference_number integer not null,

  -- Where it is. A task belongs to a property and optionally a unit, never to
  -- a lease (see decision 3).
  property_id     uuid not null,
  unit_id         uuid,

  -- Who reported it, when a tenant did. Recorded as a fact about the report,
  -- which is why it survives the tenancy ending.
  reported_by_tenant_id uuid,

  title           text not null,
  description     text,

  task_column_id  uuid not null,
  -- Denormalised from the column, maintained by a trigger below. Present so
  -- that "all open tasks" is an index scan rather than a join on every
  -- dashboard query.
  status_category app.task_status_category not null,

  position        numeric(20, 10) not null,

  priority        app.task_priority not null default 'medium',
  category        app.task_category not null default 'other',
  source          app.task_source not null default 'staff',

  -- Dates ------------------------------------------------------------------
  due_date        date,
  started_at      timestamptz,
  completed_at    timestamptz,

  -- Money. Estimate and actual kept apart so a variance is visible.
  estimated_cost  numeric(14, 2),
  actual_cost     numeric(14, 2),
  currency        char(3) not null default 'USD',

  -- Vendor, when the work is contracted out.
  vendor_id       uuid,

  -- Hierarchy, so a roof replacement can be one parent with subtasks for
  -- inspection, quotes, materials, and the work itself.
  parent_task_id  uuid,

  -- Recurrence, for preventive maintenance. Null means one-off. The recurrence
  -- produces the NEXT task on completion rather than pre-generating a year of
  -- them, so a cancelled series leaves no debris.
  recurrence_interval_days integer,
  recurrence_parent_id uuid,

  -- Activity tracking, for the no-activity escalation rule.
  last_activity_at timestamptz not null default now(),

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,

  -- Referential integrity across the organization boundary ----------------
  constraint tasks_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete cascade,
  constraint tasks_unit_fk
    foreign key (unit_id, property_id)
    references units (id, property_id) on delete set null,
  constraint tasks_tenant_fk
    foreign key (reported_by_tenant_id, organization_id)
    references tenants (id, organization_id) on delete set null,
  constraint tasks_column_fk
    foreign key (task_column_id, organization_id)
    references task_columns (id, organization_id) on delete restrict,
  constraint tasks_vendor_fk
    foreign key (vendor_id, organization_id)
    references vendors (id, organization_id) on delete set null,
  constraint tasks_parent_fk
    foreign key (parent_task_id, organization_id)
    references tasks (id, organization_id) on delete set null,

  -- Value constraints ------------------------------------------------------
  constraint tasks_reference_number_check
    check (reference_number > 0),
  constraint tasks_title_length_check
    check (char_length(title) between 1 and 300),
  constraint tasks_description_length_check
    check (description is null or char_length(description) <= 20000),
  constraint tasks_position_check
    check (position > 0),
  constraint tasks_estimated_cost_check
    check (estimated_cost is null
           or (estimated_cost >= 0 and estimated_cost <= 99999999.99)),
  constraint tasks_actual_cost_check
    check (actual_cost is null
           or (actual_cost >= 0 and actual_cost <= 99999999.99)),
  constraint tasks_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  constraint tasks_recurrence_interval_check
    check (recurrence_interval_days is null
           or recurrence_interval_days between 1 and 3650),

  -- A task cannot be its own parent. Deeper cycles are prevented by a trigger
  -- in 0015, since a CHECK cannot walk the chain.
  constraint tasks_no_self_parent_check
    check (parent_task_id is null or parent_task_id <> id),

  -- completed_at and the completed category must agree. Without this, a task
  -- shown as done could have no completion date, and every duration metric
  -- built on it would be quietly wrong.
  constraint tasks_completed_metadata_check
    check (
      (status_category = 'completed' and completed_at is not null)
      or (status_category <> 'completed' and completed_at is null)
    ),

  -- A task that has been worked must say when work began.
  constraint tasks_started_metadata_check
    check (
      status_category not in ('in_progress', 'blocked', 'review', 'completed')
      or started_at is not null
    ),

  constraint tasks_completed_after_started_check
    check (completed_at is null or started_at is null or completed_at >= started_at),

  constraint tasks_organization_reference_key unique (organization_id, reference_number),
  constraint tasks_id_organization_key unique (id, organization_id)
);

comment on table tasks is
  'A maintenance or operations task: one card on the Kanban board.';
comment on column tasks.reference_number is
  'Sequential per organization. The number people actually quote to each other.';
comment on column tasks.status_category is
  'Denormalised from the column, maintained by trigger, so open-task queries need no join.';
comment on column tasks.reported_by_tenant_id is
  'The tenant who raised it. A fact about the report, so it outlives the tenancy.';
comment on column tasks.last_activity_at is
  'Touched by any change. Drives the no-activity escalation rule.';

-- The board query: one column of one organization, in order.
create index tasks_board_idx on tasks (organization_id, task_column_id, position);
-- The property board.
create index tasks_property_board_idx on tasks (property_id, task_column_id, position);
-- Open work, which is most dashboard queries.
create index tasks_open_idx on tasks (organization_id, status_category)
  where status_category not in ('completed', 'cancelled');
-- Due and overdue sweeps.
create index tasks_due_idx on tasks (organization_id, due_date)
  where due_date is not null
    and status_category not in ('completed', 'cancelled');
-- The no-activity escalation sweep.
create index tasks_stale_idx on tasks (organization_id, last_activity_at)
  where status_category not in ('completed', 'cancelled');
create index tasks_vendor_idx on tasks (vendor_id) where vendor_id is not null;
create index tasks_parent_idx on tasks (parent_task_id) where parent_task_id is not null;
create index tasks_tenant_idx on tasks (reported_by_tenant_id)
  where reported_by_tenant_id is not null;

create trigger tasks_touch_updated_at
  before update on tasks
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- Keep status_category in step with the column
--
-- The denormalised column is only safe if nothing can set it inconsistently.
-- A trigger derives it from task_column_id on every write, so an application
-- bug that sets one without the other cannot produce a task whose displayed
-- status disagrees with the column it sits in.
-- -----------------------------------------------------------------------------
create or replace function app.sync_task_status_category()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
declare
  v_category app.task_status_category;
begin
  select c.category
  into v_category
  from task_columns c
  where c.id = new.task_column_id
    and c.organization_id = new.organization_id;

  if v_category is null then
    raise exception 'Column % does not belong to organization %',
      new.task_column_id, new.organization_id
      using errcode = 'foreign_key_violation';
  end if;

  new.status_category := v_category;

  -- Timestamps that follow from the category, applied here so they are
  -- correct however the move was made: through the API, through the move
  -- function, or by an operator fixing data by hand.
  if v_category = 'completed' and new.completed_at is null then
    new.completed_at := now();
  elsif v_category <> 'completed' then
    -- Reopening clears the completion date. Leaving a stale one behind is how
    -- a "completed last Tuesday" task shows up in an open column.
    new.completed_at := null;
  end if;

  if v_category in ('in_progress', 'blocked', 'review', 'completed')
     and new.started_at is null then
    new.started_at := now();
  end if;

  return new;
end;
$fn$;

comment on function app.sync_task_status_category() is
  'Derives tasks.status_category and the completion timestamps from the task column.';

create trigger tasks_sync_status_category
  before insert or update of task_column_id on tasks
  for each row execute function app.sync_task_status_category();

-- -----------------------------------------------------------------------------
-- Sequential reference numbers
--
-- A per-organization counter rather than a global sequence, so the first task
-- in every workspace is number 1. A sequence would leak how many tasks every
-- other organization has created, and would start a new customer at 40,000.
--
-- Serialised per organization by an advisory lock on the organization id, so
-- two concurrent inserts cannot claim the same number.
-- -----------------------------------------------------------------------------
create or replace function app.assign_task_reference_number()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  if new.reference_number is not null and new.reference_number > 0 then
    return new;
  end if;

  -- Transaction-scoped advisory lock keyed on the organization. Held only for
  -- the remainder of this transaction, and contended only by another insert
  -- into the same workspace.
  perform pg_advisory_xact_lock(hashtext('task_reference:' || new.organization_id::text));

  select coalesce(max(reference_number), 0) + 1
  into new.reference_number
  from tasks
  where organization_id = new.organization_id;

  return new;
end;
$fn$;

comment on function app.assign_task_reference_number() is
  'Assigns the next per-organization task number, serialised by an advisory lock.';

create trigger tasks_assign_reference_number
  before insert on tasks
  for each row execute function app.assign_task_reference_number();

-- -----------------------------------------------------------------------------
-- task_assignees
-- Several people can own one task: a contractor plus the manager supervising.
-- -----------------------------------------------------------------------------
create table task_assignees (
  task_id         uuid not null,
  organization_id uuid not null,

  -- Exactly one of these. A task is assigned either to a member of the
  -- workspace or to an external vendor.
  user_id         uuid,
  vendor_id       uuid,

  assigned_at     timestamptz not null default now(),
  assigned_by_user_id uuid references users (id) on delete set null,
  -- The primary owner, when several are assigned.
  is_lead         boolean not null default false,

  constraint task_assignees_task_fk
    foreign key (task_id, organization_id)
    references tasks (id, organization_id) on delete cascade,
  constraint task_assignees_vendor_fk
    foreign key (vendor_id, organization_id)
    references vendors (id, organization_id) on delete cascade,
  constraint task_assignees_user_fk
    foreign key (user_id) references users (id) on delete cascade,

  constraint task_assignees_exactly_one_target_check
    check ((user_id is null) <> (vendor_id is null))
);

comment on table task_assignees is
  'Who owns a task. Either a workspace member or an external vendor, never both on one row.';

-- A person or vendor is assigned at most once to a task. Two partial indexes,
-- because a single unique constraint over nullable columns would not prevent
-- the duplicate.
create unique index task_assignees_user_key on task_assignees (task_id, user_id)
  where user_id is not null;
create unique index task_assignees_vendor_key on task_assignees (task_id, vendor_id)
  where vendor_id is not null;
-- At most one lead.
create unique index task_assignees_single_lead_idx on task_assignees (task_id)
  where is_lead;

create index task_assignees_user_idx on task_assignees (user_id)
  where user_id is not null;
create index task_assignees_vendor_idx on task_assignees (vendor_id)
  where vendor_id is not null;
create index task_assignees_organization_idx on task_assignees (organization_id);

-- -----------------------------------------------------------------------------
-- task_comments
-- -----------------------------------------------------------------------------
create table task_comments (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  task_id         uuid not null,

  author_user_id  uuid references users (id) on delete set null,
  -- Set when a vendor comments through a shared link rather than signing in.
  author_vendor_id uuid,
  -- Preserved so a deleted user's comments still show a name.
  author_display_name text not null,

  body            text not null,

  -- An internal note is not shown to a reporting tenant and is never included
  -- in a notification sent to one. Without this distinction, a manager's
  -- candid note about a tenant could be emailed to that tenant.
  is_internal     boolean not null default false,

  edited_at       timestamptz,
  created_at      timestamptz not null default now(),

  constraint task_comments_task_fk
    foreign key (task_id, organization_id)
    references tasks (id, organization_id) on delete cascade,
  constraint task_comments_vendor_fk
    foreign key (author_vendor_id, organization_id)
    references vendors (id, organization_id) on delete set null,

  constraint task_comments_body_length_check
    check (char_length(body) between 1 and 10000),
  constraint task_comments_author_name_length_check
    check (char_length(author_display_name) between 1 and 200),

  constraint task_comments_id_organization_key unique (id, organization_id)
);

comment on table task_comments is 'Discussion on a task.';
comment on column task_comments.is_internal is
  'An internal note is never shown or emailed to a reporting tenant.';

create index task_comments_task_idx on task_comments (task_id, created_at);
create index task_comments_organization_idx on task_comments (organization_id);

-- -----------------------------------------------------------------------------
-- task_attachments
-- Photos, invoices, quotes. Same storage discipline as lease documents:
-- private bucket, server-generated path, no user filename in the path.
-- -----------------------------------------------------------------------------
create table task_attachments (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  task_id         uuid not null,
  task_comment_id uuid,

  storage_bucket  text not null,
  storage_path    text not null,
  original_filename text not null,

  declared_mime_type text not null,
  detected_mime_type text,
  file_size_bytes bigint not null,
  sha256          bytea,

  -- Set for images, so the board can lay out a thumbnail without downloading
  -- the original.
  image_width     integer,
  image_height    integer,

  malware_scan_status app.malware_scan_status not null default 'pending',
  scan_completed_at timestamptz,

  uploaded_by_user_id uuid references users (id) on delete set null,
  uploaded_at     timestamptz not null default now(),
  deleted_at      timestamptz,
  purged_at       timestamptz,

  constraint task_attachments_task_fk
    foreign key (task_id, organization_id)
    references tasks (id, organization_id) on delete cascade,
  constraint task_attachments_comment_fk
    foreign key (task_comment_id, organization_id)
    references task_comments (id, organization_id) on delete set null,

  -- Server-generated path: org/<org uuid>/tasks/<task uuid>/<uuid>.<ext>.
  -- A user-controlled filename cannot satisfy this, so traversal and
  -- cross-organization writes are structurally impossible.
  constraint task_attachments_storage_path_shape_check
    check (storage_path ~ '^org/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/tasks/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(pdf|docx|jpg|jpeg|png|webp|heic)$'),
  constraint task_attachments_storage_path_tenant_check
    check (storage_path like ('org/' || organization_id::text || '/tasks/%')),

  constraint task_attachments_original_filename_length_check
    check (char_length(original_filename) between 1 and 400),

  -- Allowlist. Photos and documents only: no archives, no executables, and
  -- nothing the browser would treat as active content.
  constraint task_attachments_declared_mime_check
    check (declared_mime_type in (
      'application/pdf',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      'image/jpeg', 'image/png', 'image/webp', 'image/heic'
    )),

  -- 25 MB. Lower than a lease document, because these are phone photos and a
  -- larger ceiling only invites an accidental video upload.
  constraint task_attachments_file_size_check
    check (file_size_bytes > 0 and file_size_bytes <= 26214400),
  constraint task_attachments_sha256_length_check
    check (sha256 is null or octet_length(sha256) = 32),
  constraint task_attachments_image_dimensions_check
    check ((image_width is null) = (image_height is null)),

  constraint task_attachments_id_organization_key unique (id, organization_id)
);

comment on table task_attachments is
  'Photos and documents on a task. Private bucket, server-generated path.';

create unique index task_attachments_storage_path_key
  on task_attachments (storage_bucket, storage_path);
create index task_attachments_task_idx on task_attachments (task_id, uploaded_at)
  where deleted_at is null;
create index task_attachments_comment_idx on task_attachments (task_comment_id)
  where task_comment_id is not null;
create index task_attachments_organization_idx on task_attachments (organization_id);
create index task_attachments_purge_queue_idx on task_attachments (deleted_at)
  where deleted_at is not null and purged_at is null;

-- -----------------------------------------------------------------------------
-- task_activity
-- Append-only history: who changed what, and when.
--
-- Separate from audit_logs on purpose. audit_logs is an operator's security
-- record, readable only by administrators. This is a user-facing timeline
-- shown on the card, readable by anybody who can see the task. Merging them
-- would mean either exposing the security log or hiding the timeline.
-- -----------------------------------------------------------------------------
create table task_activity (
  id              bigint generated always as identity primary key,
  organization_id uuid not null,
  task_id         uuid not null,

  actor_user_id   uuid references users (id) on delete set null,
  actor_display_name text not null,
  -- 'user', 'vendor', 'system' for an automated change such as a recurrence.
  actor_kind      text not null default 'user',

  -- Matches app.task_event_type in 0014. Text here rather than the enum, so
  -- the timeline can record a change that has no notification rule attached.
  activity_type   text not null,

  -- Short, display-ready summary, for example "moved from New to In Progress".
  summary         text not null,
  -- Structured before and after, for a diff view. Identifiers and values
  -- only, never a document or a photo.
  detail          jsonb not null default '{}'::jsonb,

  created_at      timestamptz not null default now(),

  constraint task_activity_task_fk
    foreign key (task_id, organization_id)
    references tasks (id, organization_id) on delete cascade,

  constraint task_activity_actor_kind_check
    check (actor_kind in ('user', 'vendor', 'system')),
  constraint task_activity_type_check
    check (activity_type ~ '^[a-z][a-z0-9_]*$' and char_length(activity_type) <= 60),
  constraint task_activity_summary_length_check
    check (char_length(summary) between 1 and 500),
  constraint task_activity_detail_is_object_check
    check (jsonb_typeof(detail) = 'object'),
  constraint task_activity_detail_size_check
    check (pg_column_size(detail) <= 8192)
);

comment on table task_activity is
  'Append-only, user-facing task timeline. Distinct from audit_logs, which is the security record.';

create index task_activity_task_idx on task_activity (task_id, created_at desc);
create index task_activity_organization_idx on task_activity (organization_id, created_at desc);

create trigger task_activity_append_only
  before update or delete on task_activity
  for each row execute function app.deny_mutation();

-- -----------------------------------------------------------------------------
-- Touch last_activity_at
--
-- The no-activity escalation rule needs a reliable "last touched" value, and
-- updated_at is not it: updated_at changes when a nightly job rewrites a
-- denormalised field, which would silently reset the staleness clock. This
-- advances only on real activity.
-- -----------------------------------------------------------------------------
create or replace function app.touch_task_last_activity()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  update tasks
  set last_activity_at = now()
  where id = new.task_id;

  return new;
end;
$fn$;

comment on function app.touch_task_last_activity() is
  'Advances tasks.last_activity_at on real activity, which drives the staleness rule.';

create trigger task_activity_touch_task
  after insert on task_activity
  for each row execute function app.touch_task_last_activity();

create trigger task_comments_touch_task
  after insert on task_comments
  for each row execute function app.touch_task_last_activity();
