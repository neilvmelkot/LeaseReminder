-- =============================================================================
-- 0028 — Archiving a task
--
-- WHY ARCHIVE RATHER THAN DELETE
--   A task carries a comment thread, an activity trail, costs, and the record
--   of who did what. Deleting one would destroy the answer to "what happened
--   with that leak" — which is the question the board exists to answer six
--   months later.
--
--   So archiving hides the task and keeps all of it, and is reversible. There
--   is deliberately no delete: nothing here needs one, and offering it would
--   make the comment thread destructible by a misclick.
--
-- WHY IT IS SEPARATE FROM COMPLETING
--   Completing a task says the work is done. Archiving says the task should
--   not be on the board — a duplicate, something logged in error, a job that
--   was cancelled before it started. Collapsing the two would force somebody
--   to mark work "done" that was never done, in a record other people read.
--
-- WHAT RUNS THE CHECKS
--   Write level, like every other task mutation: the people who can create
--   and move tasks can archive them. Consistent with app.create_task and
--   app.update_task_fields rather than inventing a second rule.
-- =============================================================================

alter table tasks
  add column if not exists archived_at timestamptz,
  add column if not exists archived_by_user_id uuid references users (id) on delete set null;

comment on column tasks.archived_at is
  'Set when a task is withdrawn from the board. Its comments, activity and '
  'costs are retained, and restoring it puts it back.';

-- Both halves or neither. A task claiming to be archived by somebody but not
-- archived is not a state worth being able to represent.
alter table tasks
  drop constraint if exists tasks_archived_metadata_check;

alter table tasks
  add constraint tasks_archived_metadata_check
  check (
    (archived_at is null and archived_by_user_id is null)
    or (archived_at is not null and archived_by_user_id is not null)
  );

-- The board's read is "the tasks still on it", so that is the index.
create index if not exists tasks_board_active_idx
  on tasks (organization_id, task_column_id, position)
  where archived_at is null;

-- -----------------------------------------------------------------------------
-- app.archive_task
-- -----------------------------------------------------------------------------
create or replace function app.archive_task(p_task_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id uuid;
  v_task    record;
  v_actor   text;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Archiving a task requires an authenticated user.'
      using errcode = '28000';
  end if;

  select t.id, t.organization_id, t.archived_at
    into v_task
  from tasks t
  where t.id = p_task_id;

  if v_task.id is null then
    raise exception 'No such task.' using errcode = 'P0002';
  end if;

  if not app.can_write_org(v_task.organization_id) then
    raise exception 'Your role cannot archive tasks.'
      using errcode = '42501';
  end if;

  if v_task.archived_at is not null then
    -- Already archived. Not an error: two people reaching the same conclusion
    -- is not a conflict, and failing here would make the button feel broken.
    return;
  end if;

  update tasks
  set archived_at = now(),
      archived_by_user_id = v_user_id
  where id = p_task_id;

  select coalesce(u.display_name, u.email::text) into v_actor
  from users u where u.id = v_user_id;

  -- On the task's own trail, so the reason it left the board is visible to
  -- whoever restores it.
  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary
  )
  values (
    v_task.organization_id, p_task_id, v_user_id,
    coalesce(v_actor, 'Unknown'), 'user', 'archived', 'archived this task');

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_task.organization_id, v_user_id, 'task.archived', 'task', p_task_id);
end;
$$;

-- -----------------------------------------------------------------------------
-- app.restore_task
-- -----------------------------------------------------------------------------
create or replace function app.restore_task(p_task_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id uuid;
  v_task    record;
  v_actor   text;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Restoring a task requires an authenticated user.'
      using errcode = '28000';
  end if;

  select t.id, t.organization_id, t.archived_at
    into v_task
  from tasks t
  where t.id = p_task_id;

  if v_task.id is null then
    raise exception 'No such task.' using errcode = 'P0002';
  end if;

  if not app.can_write_org(v_task.organization_id) then
    raise exception 'Your role cannot restore tasks.'
      using errcode = '42501';
  end if;

  if v_task.archived_at is null then
    return;
  end if;

  update tasks
  set archived_at = null,
      archived_by_user_id = null
  where id = p_task_id;

  select coalesce(u.display_name, u.email::text) into v_actor
  from users u where u.id = v_user_id;

  insert into task_activity (
    organization_id, task_id, actor_user_id, actor_display_name,
    actor_kind, activity_type, summary
  )
  values (
    v_task.organization_id, p_task_id, v_user_id,
    coalesce(v_actor, 'Unknown'), 'user', 'restored', 'restored this task');

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_task.organization_id, v_user_id, 'task.restored', 'task', p_task_id);
end;
$$;

-- -----------------------------------------------------------------------------
-- Grants
--
-- The revoke first: a new function in schema app is created with EXECUTE for
-- PUBLIC, and CREATE OR REPLACE keeps whatever ACL an existing one had.
-- -----------------------------------------------------------------------------
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

grant execute on function app.archive_task(uuid) to app_api;
grant execute on function app.restore_task(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Calls both functions rather than checking they exist. 0026 checked for
-- existence and shipped a function that referenced a table by the wrong name;
-- only running it would have caught that, so that is what this does.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org    uuid;
  v_user   uuid;
  v_auth   uuid := gen_random_uuid();
  v_prop   uuid;
  v_column uuid;
  v_task   uuid;
  v_when   timestamptz;
  v_trail  integer;
begin
  insert into organizations (name, timezone)
  values ('verification-0028', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0028@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  insert into properties (organization_id, name, timezone)
  values (v_org, 'verification-0028', 'UTC')
  returning id into v_prop;

  insert into task_columns (organization_id, name, category, position)
  values (v_org, 'Verification', 'new', 1)
  returning id into v_column;

  insert into tasks (
    organization_id, property_id, task_column_id, title, status_category, position)
  values (v_org, v_prop, v_column, 'verification task', 'new', 1000)
  returning id into v_task;

  -- The identity chain is app.auth_user_id (the auth subject) -> users
  -- .auth_user_id -> users.id, so the setting holds the AUTH id.
  perform set_config('app.auth_user_id', v_auth::text, true);

  perform app.archive_task(v_task);

  select archived_at into v_when from tasks where id = v_task;

  if v_when is null then
    raise exception 'Verification 0028: archive_task did not archive the task.';
  end if;

  -- Idempotent: a second call is a no-op rather than an error.
  perform app.archive_task(v_task);

  perform app.restore_task(v_task);

  select archived_at into v_when from tasks where id = v_task;

  if v_when is not null then
    raise exception 'Verification 0028: restore_task did not restore the task.';
  end if;

  -- Both ends recorded on the task's own trail.
  select count(*) into v_trail
  from task_activity
  where task_id = v_task and activity_type in ('archived', 'restored');

  if v_trail < 2 then
    raise exception
      'Verification 0028: expected archive and restore on the activity trail, found %',
      v_trail;
  end if;

  raise exception using errcode = 'P0001', message = 'verification-0028-rollback';
exception
  when sqlstate 'P0001' then
    if sqlerrm <> 'verification-0028-rollback' then
      raise;
    end if;
end
$verify$;

do $cleanup$
begin
  if exists (select 1 from organizations where name = 'verification-0028') then
    raise exception
      'Verification 0028 left scaffolding behind. Remove the organization '
      'named verification-0028 before using this database.';
  end if;
end
$cleanup$;
