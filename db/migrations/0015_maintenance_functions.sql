-- =============================================================================
-- 0015_maintenance_functions.sql
-- The operations the task module performs, and the event-raising that goes
-- with them.
--
-- Everything here exists for one reason: a task change, its timeline entry,
-- and its outbox event must commit together or not at all. Doing that in
-- application code across three round trips leaves two windows in which a
-- crash produces a task whose history or notification is missing. Doing it in
-- one function leaves none.
--
-- The functions are SECURITY DEFINER but every one of them re-checks
-- authorization against the caller, because bypassing RLS is exactly what
-- SECURITY DEFINER does and these are reachable by the API role.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Parent cycle guard
--
-- A CHECK constraint can stop a task being its own parent, but not A -> B -> A.
-- An unchecked cycle is not a cosmetic problem: every subtask walk, every
-- rollup of cost, and every cascade would recurse until the stack runs out.
-- -----------------------------------------------------------------------------
create or replace function app.guard_task_parent_cycle()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
declare
  v_ancestor uuid := new.parent_task_id;
  v_depth    integer := 0;
begin
  if v_ancestor is null then
    return new;
  end if;

  -- Walk up from the proposed parent. If this task appears, the link would
  -- close a cycle. The depth bound also caps hierarchy nesting, which keeps
  -- the UI's subtask tree finite.
  while v_ancestor is not null loop
    v_depth := v_depth + 1;

    if v_ancestor = new.id then
      raise exception
        'Task % cannot be a descendant of itself; the parent link would create a cycle.',
        new.id
        using errcode = 'integrity_constraint_violation';
    end if;

    if v_depth > 10 then
      raise exception
        'Task hierarchy deeper than 10 levels is not supported.'
        using errcode = 'program_limit_exceeded';
    end if;

    select parent_task_id into v_ancestor from tasks where id = v_ancestor;
  end loop;

  return new;
end;
$fn$;

comment on function app.guard_task_parent_cycle() is
  'Rejects a parent link that would create a cycle, and caps hierarchy depth.';

create trigger tasks_guard_parent_cycle
  before insert or update of parent_task_id on tasks
  for each row execute function app.guard_task_parent_cycle();

-- -----------------------------------------------------------------------------
-- Position arithmetic
--
-- Cards and columns are ordered by a fractional position, so a drag is one
-- UPDATE. The only cost is that repeated midpoint insertion between the same
-- two cards halves the gap each time; after about fifty the gap reaches the
-- limit of what numeric can usefully represent. app.move_task detects that and
-- renumbers the one affected column, which in practice happens almost never.
-- -----------------------------------------------------------------------------

/** The spacing between freshly numbered items. Large, to leave room between. */
create or replace function app.position_step()
returns numeric
language sql
immutable
as $fn$
  select 1024::numeric
$fn$;

create or replace function app.position_between(
  p_previous numeric,
  p_next     numeric
)
returns numeric
language plpgsql
immutable
as $fn$
begin
  -- Appending to an empty list.
  if p_previous is null and p_next is null then
    return app.position_step();
  end if;

  -- Inserting at the top: half of the first item's position.
  if p_previous is null then
    return p_next / 2;
  end if;

  -- Appending at the bottom.
  if p_next is null then
    return p_previous + app.position_step();
  end if;

  return (p_previous + p_next) / 2;
end;
$fn$;

comment on function app.position_between(numeric, numeric) is
  'The fractional midpoint for an insertion, so reordering touches one row.';

-- -----------------------------------------------------------------------------
-- app.record_outbox_event
--
-- The single door through which every event enters the outbox. Called from
-- inside the functions below, so the event and the change it describes share a
-- transaction by construction rather than by remembering to.
-- -----------------------------------------------------------------------------
create or replace function app.record_outbox_event(
  p_organization_id uuid,
  p_aggregate_type  text,
  p_aggregate_id    uuid,
  p_event_type      text,
  p_payload         jsonb,
  p_actor_user_id   uuid,
  p_actor_display_name text,
  -- Supplied by the caller when a natural key exists (a scheduled sweep uses
  -- the date, so re-running it the same day is a no-op). Null generates a
  -- unique key, which is correct for a user action: assigning twice really is
  -- two events.
  p_idempotency_key text default null
)
returns bigint
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_key text;
  v_id  bigint;
begin
  v_key := coalesce(
    p_idempotency_key,
    p_event_type || ':' || p_aggregate_id::text || ':' || gen_random_uuid()::text
  );

  insert into notification_outbox (
    organization_id, aggregate_type, aggregate_id, event_type,
    payload, actor_user_id, actor_display_name, idempotency_key
  )
  values (
    p_organization_id, p_aggregate_type, p_aggregate_id, p_event_type,
    coalesce(p_payload, '{}'::jsonb), p_actor_user_id,
    left(p_actor_display_name, 200), v_key
  )
  -- A replayed request collapses onto the existing event rather than raising,
  -- which is what makes the whole call idempotent instead of just the insert.
  on conflict (organization_id, idempotency_key) do nothing
  returning id into v_id;

  return v_id;
end;
$fn$;

comment on function app.record_outbox_event(uuid, text, uuid, text, jsonb, uuid, text, text) is
  'Writes one event to the outbox in the caller transaction. The only entry point.';

-- -----------------------------------------------------------------------------
-- app.create_task
--
-- Creates the card, its timeline entry, and its created event in one
-- transaction, and lands it in the organization's intake column.
-- -----------------------------------------------------------------------------
create or replace function app.create_task(
  p_organization_id uuid,
  p_property_id     uuid,
  p_title           text,
  p_actor_user_id   uuid,
  p_actor_display_name text,
  p_unit_id         uuid default null,
  p_description     text default null,
  p_priority        app.task_priority default 'medium',
  p_category        app.task_category default 'other',
  p_source          app.task_source default 'staff',
  p_due_date        date default null,
  p_reported_by_tenant_id uuid default null,
  p_estimated_cost  numeric default null,
  p_vendor_id       uuid default null,
  p_parent_task_id  uuid default null,
  p_recurrence_interval_days integer default null,
  p_task_column_id  uuid default null
)
returns tasks
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_column_id uuid;
  v_position  numeric;
  v_task      tasks;
begin
  -- SECURITY DEFINER bypasses RLS, so authorization is re-checked here.
  if not app.can_write_org(p_organization_id) then
    raise exception 'Not permitted to create a task in this organization'
      using errcode = 'insufficient_privilege';
  end if;

  if p_title is null or char_length(btrim(p_title)) = 0 then
    raise exception 'A task needs a title' using errcode = 'invalid_parameter_value';
  end if;

  -- An explicit column, or the intake column.
  v_column_id := p_task_column_id;

  if v_column_id is null then
    select id into v_column_id
    from task_columns
    where organization_id = p_organization_id
      and is_default_intake
    limit 1;
  end if;

  if v_column_id is null then
    -- Falling back to "the leftmost column" would silently put work somewhere
    -- unexpected. Better to say the board is not set up.
    raise exception
      'No intake column is configured for this organization. '
      'Seed the board with app.seed_task_board first.'
      using errcode = 'invalid_parameter_value';
  end if;

  -- New work goes to the TOP of intake, not the bottom. A queue people read
  -- top-down should surface the newest report, and an urgent leak arriving
  -- below thirty stale cards is how it gets missed.
  select app.position_between(null, min(position))
  into v_position
  from tasks
  where organization_id = p_organization_id
    and task_column_id = v_column_id;

  insert into tasks (
    organization_id, property_id, unit_id, reported_by_tenant_id,
    title, description, task_column_id, status_category, position,
    priority, category, source, due_date, estimated_cost, currency,
    vendor_id, parent_task_id, recurrence_interval_days, created_by_user_id
  )
  values (
    p_organization_id, p_property_id, p_unit_id, p_reported_by_tenant_id,
    btrim(p_title), p_description, v_column_id,
    -- Overwritten by the sync trigger; a value is needed to satisfy NOT NULL.
    'new', coalesce(v_position, app.position_step()),
    p_priority, p_category, p_source, p_due_date, p_estimated_cost, 'USD',
    p_vendor_id, p_parent_task_id, p_recurrence_interval_days, p_actor_user_id
  )
  returning * into v_task;

  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary, detail
  )
  values (
    p_organization_id, v_task.id, p_actor_user_id,
    coalesce(p_actor_display_name, 'System'),
    case when p_actor_user_id is null then 'system' else 'user' end,
    'task_created',
    format('created this task in %s',
           (select name from task_columns where id = v_column_id)),
    jsonb_build_object('priority', p_priority, 'category', p_category)
  );

  perform app.record_outbox_event(
    p_organization_id, 'task', v_task.id, 'task_created',
    jsonb_build_object(
      'task_id', v_task.id,
      'reference_number', v_task.reference_number,
      'title', v_task.title,
      'priority', p_priority,
      'category', p_category,
      'source', p_source,
      'property_id', p_property_id,
      'unit_id', p_unit_id,
      'due_date', p_due_date,
      'reported_by_tenant_id', p_reported_by_tenant_id
    ),
    p_actor_user_id, p_actor_display_name
  );

  return v_task;
end;
$fn$;

comment on function app.create_task is
  'Creates a task, its first timeline entry, and its created event in one transaction.';

-- -----------------------------------------------------------------------------
-- app.move_task
--
-- The drag-and-drop operation. Takes the neighbouring card ids rather than a
-- computed position, because the client's view of positions may already be
-- stale: two people dragging at once would otherwise both compute "2.5" from
-- the same stale neighbours and collide. Neighbours are stable identifiers, so
-- the server recomputes the position from current truth.
-- -----------------------------------------------------------------------------
create or replace function app.move_task(
  p_task_id         uuid,
  p_target_column_id uuid,
  -- The card immediately ABOVE the drop point, or null when dropping at the top.
  p_previous_task_id uuid,
  -- The card immediately BELOW it, or null when dropping at the bottom.
  p_next_task_id    uuid,
  p_actor_user_id   uuid,
  p_actor_display_name text
)
returns tasks
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_task          tasks;
  v_from_column   task_columns;
  v_to_column     task_columns;
  v_previous_pos  numeric;
  v_next_pos      numeric;
  v_new_pos       numeric;
  v_renumbered    boolean := false;
  v_recurrence_id uuid;
begin
  -- Lock the card for the duration, so two simultaneous drags of the SAME card
  -- serialise instead of interleaving. Dragging two different cards still
  -- proceeds in parallel.
  select * into v_task from tasks where id = p_task_id for update;

  if not found then
    raise exception 'Task % does not exist', p_task_id using errcode = 'no_data_found';
  end if;

  if not app.can_write_org(v_task.organization_id) then
    raise exception 'Not permitted to move this task'
      using errcode = 'insufficient_privilege';
  end if;

  select * into v_from_column from task_columns where id = v_task.task_column_id;

  select * into v_to_column
  from task_columns
  where id = p_target_column_id
    and organization_id = v_task.organization_id;

  if not found then
    raise exception 'Column % does not belong to this organization', p_target_column_id
      using errcode = 'foreign_key_violation';
  end if;

  -- Neighbour positions, read now rather than trusted from the client. Both
  -- are constrained to the target column, so a stale neighbour from another
  -- column is ignored rather than producing a nonsense position.
  select position into v_previous_pos
  from tasks
  where id = p_previous_task_id
    and task_column_id = p_target_column_id
    and organization_id = v_task.organization_id;

  select position into v_next_pos
  from tasks
  where id = p_next_task_id
    and task_column_id = p_target_column_id
    and organization_id = v_task.organization_id;

  v_new_pos := app.position_between(v_previous_pos, v_next_pos);

  -- Repeated midpoint insertion between the same pair eventually exhausts the
  -- representable gap. When that happens, renumber this one column on a clean
  -- scale and recompute. Rare enough to be a cold path, cheap enough not to
  -- matter when it fires.
  if v_previous_pos is not null and v_next_pos is not null
     and (v_next_pos - v_previous_pos) < 0.0000001 then
    with ordered as (
      select id, row_number() over (order by position, created_at) as rank
      from tasks
      where organization_id = v_task.organization_id
        and task_column_id = p_target_column_id
    )
    update tasks t
    set position = o.rank * app.position_step()
    from ordered o
    where t.id = o.id;

    v_renumbered := true;

    select position into v_previous_pos
    from tasks where id = p_previous_task_id;

    select position into v_next_pos
    from tasks where id = p_next_task_id;

    v_new_pos := app.position_between(v_previous_pos, v_next_pos);
  end if;

  update tasks
  set task_column_id = p_target_column_id,
      position = v_new_pos
  where id = p_task_id
  returning * into v_task;

  -- Reordering within a column is not a status change and must not raise one.
  -- Otherwise tidying a column would email everybody involved in every card.
  if v_from_column.id = v_to_column.id then
    return v_task;
  end if;

  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary, detail
  )
  values (
    v_task.organization_id, p_task_id, p_actor_user_id,
    coalesce(p_actor_display_name, 'System'),
    case when p_actor_user_id is null then 'system' else 'user' end,
    'task_status_changed',
    format('moved this from %s to %s', v_from_column.name, v_to_column.name),
    jsonb_build_object(
      'from_column', v_from_column.name,
      'to_column', v_to_column.name,
      'from_category', v_from_column.category,
      'to_category', v_to_column.category,
      'renumbered', v_renumbered
    )
  );

  perform app.record_outbox_event(
    v_task.organization_id, 'task', p_task_id, 'task_status_changed',
    jsonb_build_object(
      'task_id', p_task_id,
      'reference_number', v_task.reference_number,
      'title', v_task.title,
      'from_column', v_from_column.name,
      'to_column', v_to_column.name,
      'from_category', v_from_column.category,
      'to_category', v_to_column.category,
      'priority', v_task.priority,
      'property_id', v_task.property_id
    ),
    p_actor_user_id, p_actor_display_name
  );

  -- Completion and reopening are their own events, because people subscribe to
  -- them differently: a tenant wants to hear "your repair is done", not every
  -- intermediate move.
  if v_to_column.category = 'completed' and v_from_column.category <> 'completed' then
    perform app.record_outbox_event(
      v_task.organization_id, 'task', p_task_id, 'task_completed',
      jsonb_build_object(
        'task_id', p_task_id,
        'reference_number', v_task.reference_number,
        'title', v_task.title,
        'completed_at', v_task.completed_at,
        'property_id', v_task.property_id,
        'reported_by_tenant_id', v_task.reported_by_tenant_id,
        'actual_cost', v_task.actual_cost
      ),
      p_actor_user_id, p_actor_display_name
    );

    -- A recurring task spawns its successor on completion rather than having a
    -- year of them pre-generated. Cancelling the series then leaves nothing to
    -- clean up, and a changed interval takes effect immediately.
    if v_task.recurrence_interval_days is not null then
      insert into tasks (
        organization_id, property_id, unit_id, title, description,
        task_column_id, status_category, position, priority, category,
        source, due_date, estimated_cost, currency, vendor_id,
        recurrence_interval_days, recurrence_parent_id, created_by_user_id
      )
      select
        v_task.organization_id, v_task.property_id, v_task.unit_id,
        v_task.title, v_task.description,
        (select id from task_columns
         where organization_id = v_task.organization_id and is_default_intake),
        'new',
        app.position_between(
          null,
          (select min(position) from tasks
           where organization_id = v_task.organization_id
             and task_column_id = (select id from task_columns
                                   where organization_id = v_task.organization_id
                                     and is_default_intake))),
        v_task.priority, v_task.category, 'preventive_maintenance',
        -- Counted from the completion date, not the old due date, so a task
        -- finished late does not immediately come back overdue.
        (current_date + v_task.recurrence_interval_days),
        v_task.estimated_cost, v_task.currency, v_task.vendor_id,
        v_task.recurrence_interval_days,
        coalesce(v_task.recurrence_parent_id, v_task.id),
        p_actor_user_id
      returning id into v_recurrence_id;

      insert into task_activity (
        organization_id, task_id, actor_display_name, actor_kind,
        activity_type, summary, detail
      )
      values (
        v_task.organization_id, v_recurrence_id, 'System', 'system',
        'task_created',
        format('created automatically, repeating every %s days',
               v_task.recurrence_interval_days),
        jsonb_build_object('recurrence_parent_id',
                           coalesce(v_task.recurrence_parent_id, v_task.id))
      );
    end if;

  elsif v_from_column.category = 'completed' and v_to_column.category <> 'completed' then
    perform app.record_outbox_event(
      v_task.organization_id, 'task', p_task_id, 'task_reopened',
      jsonb_build_object(
        'task_id', p_task_id,
        'reference_number', v_task.reference_number,
        'title', v_task.title,
        'to_column', v_to_column.name
      ),
      p_actor_user_id, p_actor_display_name
    );
  end if;

  return v_task;
end;
$fn$;

comment on function app.move_task is
  'Moves a card, records the timeline entry and events, and spawns a recurrence on completion.';

-- -----------------------------------------------------------------------------
-- app.assign_task
-- -----------------------------------------------------------------------------
create or replace function app.assign_task(
  p_task_id       uuid,
  p_user_id       uuid,
  p_vendor_id     uuid,
  p_is_lead       boolean,
  p_actor_user_id uuid,
  p_actor_display_name text
)
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_task tasks;
  v_assignee_name text;
  v_previous_names text;
  v_was_reassignment boolean;
begin
  select * into v_task from tasks where id = p_task_id for update;

  if not found then
    raise exception 'Task % does not exist', p_task_id using errcode = 'no_data_found';
  end if;

  if not app.can_write_org(v_task.organization_id) then
    raise exception 'Not permitted to assign this task'
      using errcode = 'insufficient_privilege';
  end if;

  if (p_user_id is null) = (p_vendor_id is null) then
    raise exception 'Assign to exactly one of a user or a vendor'
      using errcode = 'invalid_parameter_value';
  end if;

  v_assignee_name := coalesce(
    (select coalesce(u.display_name, u.email::text) from users u where u.id = p_user_id),
    (select coalesce(v.company, v.name) from vendors v where v.id = p_vendor_id)
  );

  if v_assignee_name is null then
    raise exception 'The assignee does not exist' using errcode = 'no_data_found';
  end if;

  -- Captured before the change, so a reassignment can name who it was taken
  -- from. "Reassigned from Mike to John" is the useful message; "assigned to
  -- John" leaves Mike wondering whether he is still responsible.
  select string_agg(name, ', ') into v_previous_names
  from (
    select coalesce(u.display_name, u.email::text) as name
    from task_assignees a join users u on u.id = a.user_id
    where a.task_id = p_task_id
    union all
    select coalesce(v.company, v.name)
    from task_assignees a join vendors v on v.id = a.vendor_id
    where a.task_id = p_task_id
  ) existing;

  v_was_reassignment := v_previous_names is not null;

  -- A lead replaces the existing lead rather than failing on the unique index.
  if p_is_lead then
    update task_assignees set is_lead = false
    where task_id = p_task_id and is_lead;
  end if;

  insert into task_assignees (
    task_id, organization_id, user_id, vendor_id, assigned_by_user_id, is_lead
  )
  values (
    p_task_id, v_task.organization_id, p_user_id, p_vendor_id, p_actor_user_id,
    coalesce(p_is_lead, false)
  )
  on conflict do nothing;

  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary, detail
  )
  values (
    v_task.organization_id, p_task_id, p_actor_user_id,
    coalesce(p_actor_display_name, 'System'),
    case when p_actor_user_id is null then 'system' else 'user' end,
    case when v_was_reassignment then 'task_reassigned' else 'task_assigned' end,
    case
      when v_was_reassignment
        then format('reassigned this from %s to %s', v_previous_names, v_assignee_name)
      else format('assigned this to %s', v_assignee_name)
    end,
    jsonb_build_object(
      'assignee_name', v_assignee_name,
      'previous_assignees', v_previous_names,
      'is_lead', coalesce(p_is_lead, false)
    )
  );

  perform app.record_outbox_event(
    v_task.organization_id, 'task', p_task_id,
    case when v_was_reassignment then 'task_reassigned' else 'task_assigned' end,
    jsonb_build_object(
      'task_id', p_task_id,
      'reference_number', v_task.reference_number,
      'title', v_task.title,
      'priority', v_task.priority,
      'due_date', v_task.due_date,
      'property_id', v_task.property_id,
      'assignee_user_id', p_user_id,
      'assignee_vendor_id', p_vendor_id,
      'assignee_name', v_assignee_name,
      'previous_assignees', v_previous_names
    ),
    p_actor_user_id, p_actor_display_name
  );
end;
$fn$;

comment on function app.assign_task is
  'Assigns a task and raises assigned or reassigned, naming who it was taken from.';

-- -----------------------------------------------------------------------------
-- app.add_task_comment
-- -----------------------------------------------------------------------------
create or replace function app.add_task_comment(
  p_task_id       uuid,
  p_body          text,
  p_is_internal   boolean,
  p_actor_user_id uuid,
  p_actor_display_name text
)
returns task_comments
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_task    tasks;
  v_comment task_comments;
begin
  select * into v_task from tasks where id = p_task_id;

  if not found then
    raise exception 'Task % does not exist', p_task_id using errcode = 'no_data_found';
  end if;

  -- Commenting is deliberately open to any member, including read-only ones.
  -- A read-only role is about not changing the portfolio, not about being
  -- unable to say "the contractor cannot get access on Tuesday".
  if not app.is_org_member(v_task.organization_id) then
    raise exception 'Not permitted to comment on this task'
      using errcode = 'insufficient_privilege';
  end if;

  if p_body is null or char_length(btrim(p_body)) = 0 then
    raise exception 'A comment cannot be empty' using errcode = 'invalid_parameter_value';
  end if;

  insert into task_comments (
    organization_id, task_id, author_user_id, author_display_name,
    body, is_internal
  )
  values (
    v_task.organization_id, p_task_id, p_actor_user_id,
    coalesce(p_actor_display_name, 'System'), btrim(p_body),
    coalesce(p_is_internal, false)
  )
  returning * into v_comment;

  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary, detail
  )
  values (
    v_task.organization_id, p_task_id, p_actor_user_id,
    coalesce(p_actor_display_name, 'System'),
    case when p_actor_user_id is null then 'system' else 'user' end,
    'task_comment_added',
    case when coalesce(p_is_internal, false)
      then 'added an internal note' else 'commented' end,
    jsonb_build_object('comment_id', v_comment.id, 'is_internal', p_is_internal)
  );

  perform app.record_outbox_event(
    v_task.organization_id, 'task', p_task_id, 'task_comment_added',
    jsonb_build_object(
      'task_id', p_task_id,
      'reference_number', v_task.reference_number,
      'title', v_task.title,
      'comment_id', v_comment.id,
      -- The excerpt is bounded, and the internal flag travels with it so the
      -- rule engine can keep an internal note away from a reporting tenant.
      'comment_excerpt', left(v_comment.body, 300),
      'is_internal', v_comment.is_internal,
      'property_id', v_task.property_id,
      'reported_by_tenant_id', v_task.reported_by_tenant_id
    ),
    p_actor_user_id, p_actor_display_name
  );

  return v_comment;
end;
$fn$;

comment on function app.add_task_comment is
  'Adds a comment, its timeline entry, and its event. Internal notes never reach a tenant.';

-- -----------------------------------------------------------------------------
-- app.update_task_fields
--
-- One function for the editable fields, so each change raises its own specific
-- event. A single generic "task_updated" would force every subscriber to
-- diff the payload to find out whether they cared.
-- -----------------------------------------------------------------------------
create or replace function app.update_task_fields(
  p_task_id       uuid,
  p_actor_user_id uuid,
  p_actor_display_name text,
  p_title         text default null,
  p_description   text default null,
  p_priority      app.task_priority default null,
  p_category      app.task_category default null,
  p_due_date      date default null,
  p_clear_due_date boolean default false,
  p_estimated_cost numeric default null,
  p_actual_cost   numeric default null,
  p_vendor_id     uuid default null
)
returns tasks
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_before tasks;
  v_after  tasks;
begin
  select * into v_before from tasks where id = p_task_id for update;

  if not found then
    raise exception 'Task % does not exist', p_task_id using errcode = 'no_data_found';
  end if;

  if not app.can_write_org(v_before.organization_id) then
    raise exception 'Not permitted to edit this task'
      using errcode = 'insufficient_privilege';
  end if;

  update tasks
  set title          = coalesce(btrim(p_title), title),
      description    = coalesce(p_description, description),
      priority       = coalesce(p_priority, priority),
      category       = coalesce(p_category, category),
      -- An explicit clear flag, because coalesce cannot distinguish "leave it
      -- alone" from "remove it" when both arrive as null.
      due_date       = case when p_clear_due_date then null
                            else coalesce(p_due_date, due_date) end,
      estimated_cost = coalesce(p_estimated_cost, estimated_cost),
      actual_cost    = coalesce(p_actual_cost, actual_cost),
      vendor_id      = coalesce(p_vendor_id, vendor_id)
  where id = p_task_id
  returning * into v_after;

  -- Priority -----------------------------------------------------------------
  if v_after.priority <> v_before.priority then
    insert into task_activity (
      organization_id, task_id, actor_user_id, actor_display_name,
      actor_kind, activity_type, summary, detail
    )
    values (
      v_after.organization_id, p_task_id, p_actor_user_id,
      coalesce(p_actor_display_name, 'System'), 'user', 'task_priority_changed',
      format('changed priority from %s to %s', v_before.priority, v_after.priority),
      jsonb_build_object('from', v_before.priority, 'to', v_after.priority)
    );

    perform app.record_outbox_event(
      v_after.organization_id, 'task', p_task_id, 'task_priority_changed',
      jsonb_build_object(
        'task_id', p_task_id,
        'reference_number', v_after.reference_number,
        'title', v_after.title,
        'from_priority', v_before.priority,
        'to_priority', v_after.priority,
        'property_id', v_after.property_id
      ),
      p_actor_user_id, p_actor_display_name
    );
  end if;

  -- Due date -----------------------------------------------------------------
  if v_after.due_date is distinct from v_before.due_date then
    insert into task_activity (
      organization_id, task_id, actor_user_id, actor_display_name,
      actor_kind, activity_type, summary, detail
    )
    values (
      v_after.organization_id, p_task_id, p_actor_user_id,
      coalesce(p_actor_display_name, 'System'), 'user', 'task_due_date_changed',
      case
        when v_after.due_date is null then 'removed the due date'
        when v_before.due_date is null
          then format('set the due date to %s', v_after.due_date)
        else format('moved the due date from %s to %s',
                    v_before.due_date, v_after.due_date)
      end,
      jsonb_build_object('from', v_before.due_date, 'to', v_after.due_date)
    );

    perform app.record_outbox_event(
      v_after.organization_id, 'task', p_task_id, 'task_due_date_changed',
      jsonb_build_object(
        'task_id', p_task_id,
        'reference_number', v_after.reference_number,
        'title', v_after.title,
        'from_due_date', v_before.due_date,
        'to_due_date', v_after.due_date,
        'property_id', v_after.property_id
      ),
      p_actor_user_id, p_actor_display_name
    );
  end if;

  -- Cost ---------------------------------------------------------------------
  if v_after.actual_cost is distinct from v_before.actual_cost then
    insert into task_activity (
      organization_id, task_id, actor_user_id, actor_display_name,
      actor_kind, activity_type, summary, detail
    )
    values (
      v_after.organization_id, p_task_id, p_actor_user_id,
      coalesce(p_actor_display_name, 'System'), 'user', 'task_cost_updated',
      format('recorded an actual cost of %s %s',
             v_after.currency, v_after.actual_cost),
      jsonb_build_object('from', v_before.actual_cost, 'to', v_after.actual_cost)
    );

    perform app.record_outbox_event(
      v_after.organization_id, 'task', p_task_id, 'task_cost_updated',
      jsonb_build_object(
        'task_id', p_task_id,
        'reference_number', v_after.reference_number,
        'title', v_after.title,
        'estimated_cost', v_after.estimated_cost,
        'actual_cost', v_after.actual_cost,
        'currency', v_after.currency,
        'property_id', v_after.property_id
      ),
      p_actor_user_id, p_actor_display_name
    );
  end if;

  -- Title and description changes are recorded on the timeline but raise no
  -- event. Nobody wants an email because a typo was fixed.
  if v_after.title <> v_before.title then
    insert into task_activity (
      organization_id, task_id, actor_user_id, actor_display_name,
      actor_kind, activity_type, summary, detail
    )
    values (
      v_after.organization_id, p_task_id, p_actor_user_id,
      coalesce(p_actor_display_name, 'System'), 'user', 'task_edited',
      'renamed this task',
      jsonb_build_object('from', v_before.title, 'to', v_after.title)
    );
  end if;

  return v_after;
end;
$fn$;

comment on function app.update_task_fields is
  'Edits a task, raising a specific event per meaningful change and none for a typo fix.';
