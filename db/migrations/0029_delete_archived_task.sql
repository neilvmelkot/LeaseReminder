-- =============================================================================
-- 0029 — Deleting an archived task
--
-- WHAT CHANGED SINCE 0028, AND WHY
--   0028 said there was deliberately no delete: a task carries a comment
--   thread, an activity trail and costs, and destroying that by a misclick
--   would lose the answer to "what happened with that leak".
--
--   That reasoning holds for a task on the board. It does not hold for the
--   archive, which fills up with duplicates, tests, and things logged against
--   the wrong property — records nobody will ever want and which make the
--   archive useless as a place to look for the ones that matter. An archive
--   you cannot empty is a second backlog.
--
--   So delete is added, and it is deliberately narrow.
--
-- THE TWO-STEP RULE
--   A task must be archived before it can be deleted. There is no path from
--   the board to gone, which is what keeps a misclick from destroying a
--   thread: archiving is reversible and visible, and the task has to be sat
--   in the archive before the second, explicitly destructive step is offered.
--
--   This is enforced here, not in the API, so it holds against a direct call
--   as well as against the button.
--
-- WHAT SURVIVES
--   The audit entry, which is written before the row goes and lives in
--   audit_logs — a table with no foreign key to tasks and no cascade, exactly
--   so that it outlives what it describes. Everything hanging off the task
--   (assignees, comments, attachments, recipients, the activity trail) goes
--   with it by ON DELETE CASCADE from 0013.
--
-- WHO MAY
--   Administrators, not the write tier. Archiving is day-to-day tidying and
--   sits at write level; destroying the record is not, and it is the one
--   operation here that cannot be undone.
-- =============================================================================

create or replace function app.delete_task(p_task_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id uuid;
  v_task    record;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Deleting a task requires an authenticated user.'
      using errcode = '28000';
  end if;

  select t.id, t.organization_id, t.archived_at, t.reference_number, t.title
    into v_task
  from tasks t
  where t.id = p_task_id;

  if v_task.id is null then
    raise exception 'No such task.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_task.organization_id) then
    raise exception 'Only an administrator can delete a task.'
      using errcode = '42501';
  end if;

  -- The two-step rule. Deliberately not an idempotent no-op like archive is:
  -- somebody asking to delete a live task has misunderstood what the button
  -- does, and silently succeeding would be the wrong kind of helpful.
  if v_task.archived_at is null then
    raise exception
      'A task must be archived before it can be deleted.'
      using errcode = '42501';
  end if;

  -- Written BEFORE the delete, so that if the delete fails the transaction
  -- takes the audit entry with it and the two can never disagree. The title
  -- and reference number are copied in because the row they came from is
  -- about to stop existing, and an audit entry naming only a uuid answers
  -- nothing six months later.
  insert into audit_logs (
    organization_id, actor_user_id, actor_kind, action, entity_type, entity_id,
    safe_metadata_json
  )
  values (
    v_task.organization_id, v_user_id, 'user', 'task.deleted', 'task', p_task_id,
    jsonb_build_object(
      'reference_number', v_task.reference_number,
      'title', v_task.title
    )
  );

  -- Assignees, comments, attachments, recipients and the activity trail all
  -- carry ON DELETE CASCADE to this row (0013), so this is the whole deletion.
  delete from tasks where id = p_task_id;
end;
$$;

comment on function app.delete_task is
  'Permanently deletes an ARCHIVED task and everything hanging off it. '
  'Administrators only. The audit entry is written first and survives.';

-- -----------------------------------------------------------------------------
-- Privileges
--
-- The revoke first: a new function in schema app is created with EXECUTE for
-- PUBLIC, and CREATE OR REPLACE keeps whatever ACL an existing one had.
-- -----------------------------------------------------------------------------
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

grant execute on function app.delete_task(uuid) to app_api;

-- Re-granted because the blanket revoke above strips every function in the
-- schema, including the ones earlier migrations granted.
grant execute on function app.archive_task(uuid) to app_api;
grant execute on function app.restore_task(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Calls the function rather than checking it exists. 0026 checked for
-- existence and shipped a function that referenced a table by the wrong name;
-- only running it would have caught that, so that is what this does.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org     uuid;
  v_user    uuid;
  v_auth    uuid := gen_random_uuid();
  v_prop    uuid;
  v_column  uuid;
  v_task    uuid;
  v_refused boolean := false;
  v_audits  integer;
begin
  insert into organizations (name, timezone)
  values ('verification-0029', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0029@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  insert into properties (organization_id, name, timezone)
  values (v_org, 'verification-0029', 'UTC')
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

  -- A live task cannot be deleted: the two-step rule is the whole point.
  begin
    perform app.delete_task(v_task);
  exception
    when insufficient_privilege then
      v_refused := true;
  end;

  if not v_refused then
    raise exception
      'Verification 0029: delete_task destroyed a task that was not archived.';
  end if;

  -- Archived, it can be.
  perform app.archive_task(v_task);
  perform app.delete_task(v_task);

  if exists (select 1 from tasks where id = v_task) then
    raise exception 'Verification 0029: delete_task left the task behind.';
  end if;

  -- The comment thread and the trail went with it.
  if exists (select 1 from task_activity where task_id = v_task) then
    raise exception 'Verification 0029: the activity trail outlived the task.';
  end if;

  -- But the audit entry did not, which is the record that has to survive.
  select count(*) into v_audits
  from audit_logs
  where entity_id = v_task and action = 'task.deleted';

  if v_audits <> 1 then
    raise exception
      'Verification 0029: expected one task.deleted audit entry, found %.', v_audits;
  end if;

  raise exception 'Verification 0029 passed, rolling back.';
exception
  when others then
    if sqlerrm = 'Verification 0029 passed, rolling back.' then
      raise notice 'Verification 0029 passed.';
    else
      raise;
    end if;
end;
$verify$;
