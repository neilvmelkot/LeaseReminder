-- =============================================================================
-- 0033 — Changing a member's role
--
-- WHY THIS DID NOT EXIST
--   A role could be set once, by invitation, and never again. Somebody
--   invited as read-only who later took over the maintenance board had to be
--   removed and re-invited, which loses the audit trail of when they joined
--   and breaks any task assigned to them in the meantime.
--
-- WHAT IT REFUSES, AND WHY EACH ONE
--   * Ownership. An owner cannot be created here, for the same reason
--     app.create_invitation refuses it: the single-owner index in 0002 would
--     reject a second one, and transferring ownership is a deliberate act
--     that should look like one rather than a dropdown change.
--
--   * The owner's own role. Demoting the owner would leave the workspace with
--     nobody holding it, and the only route back would be a direct SQL edit.
--
--   * Your own role. An administrator demoting themselves by one misclick can
--     lock a single-administrator workspace out of its own settings. Asking
--     another administrator costs a message; recovering costs a database
--     session.
-- =============================================================================

create or replace function app.change_member_role(
  p_organization_id uuid,
  p_user_id         uuid,
  p_role            text
)
returns void
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_actor_id    uuid;
  v_current     app.org_role;
begin
  v_actor_id := app.current_user_id();

  if v_actor_id is null then
    raise exception 'Changing a role requires an authenticated user.'
      using errcode = '28000';
  end if;

  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can change roles.'
      using errcode = '42501';
  end if;

  if p_role = 'owner' then
    raise exception
      'Ownership cannot be granted here. Transferring a workspace is a separate step.'
      using errcode = '22023';
  end if;

  if p_user_id = v_actor_id then
    raise exception
      'You cannot change your own role. Ask another owner or administrator.'
      using errcode = '42501';
  end if;

  select role into v_current
  from organization_members
  where organization_id = p_organization_id and user_id = p_user_id;

  if v_current is null then
    raise exception 'That person is not a member of this workspace.'
      using errcode = 'no_data_found';
  end if;

  if v_current = 'owner' then
    raise exception
      'The owner''s role cannot be changed. Transfer the workspace first.'
      using errcode = '42501';
  end if;

  -- Nothing to do, and nothing to record. Returning quietly rather than
  -- writing an audit entry saying a role changed from admin to admin.
  if v_current::text = p_role then
    return;
  end if;

  update organization_members
  set role = p_role::app.org_role
  where organization_id = p_organization_id and user_id = p_user_id;

  insert into audit_logs (
    organization_id, actor_user_id, actor_kind, action, entity_type, entity_id,
    safe_metadata_json
  )
  values (
    p_organization_id, v_actor_id, 'user', 'member.role_changed', 'user', p_user_id,
    jsonb_build_object('from', v_current::text, 'to', p_role)
  );
end;
$fn$;

comment on function app.change_member_role is
  'Changes a member''s role. Administrators only. Refuses ownership, the owner, and self.';

revoke all on function app.change_member_role(uuid, uuid, text) from public;
grant execute on function app.change_member_role(uuid, uuid, text) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Exercises the happy path and all three refusals, because each refusal is
-- the thing standing between a dropdown and a locked-out workspace.
--
-- Everything it creates is rolled back by raising at the end of the block.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org     uuid;
  v_owner   uuid;
  v_owner_a uuid := gen_random_uuid();
  v_admin   uuid;
  v_admin_a uuid := gen_random_uuid();
  v_role    text;
  v_refused boolean;
begin
  insert into organizations (name, timezone)
  values ('verification-0033', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_owner_a, 'owner-0033@example.invalid')
  returning id into v_owner;

  insert into users (auth_user_id, email)
  values (v_admin_a, 'member-0033@example.invalid')
  returning id into v_admin;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_owner, 'owner'), (v_org, v_admin, 'read_only');

  perform set_config('app.auth_user_id', v_owner_a::text, true);

  -- The happy path.
  perform app.change_member_role(v_org, v_admin, 'property_manager');

  select role::text into v_role
  from organization_members
  where organization_id = v_org and user_id = v_admin;

  if v_role is distinct from 'property_manager' then
    raise exception 'Verification 0033: expected property_manager, got %.', v_role;
  end if;

  -- Ownership cannot be granted.
  v_refused := false;
  begin
    perform app.change_member_role(v_org, v_admin, 'owner');
  exception when others then v_refused := true;
  end;

  if not v_refused then
    raise exception 'Verification 0033: ownership was granted through a role change.';
  end if;

  -- The owner cannot be demoted.
  v_refused := false;
  begin
    perform app.change_member_role(v_org, v_owner, 'read_only');
  exception when others then v_refused := true;
  end;

  if not v_refused then
    raise exception 'Verification 0033: the owner was demoted.';
  end if;

  -- Nobody may change their own role. Checked as the ADMIN, because as the
  -- owner the previous rule would have caught it anyway and proved nothing.
  perform app.change_member_role(v_org, v_admin, 'admin');
  perform set_config('app.auth_user_id', v_admin_a::text, true);

  v_refused := false;
  begin
    perform app.change_member_role(v_org, v_admin, 'read_only');
  exception when others then v_refused := true;
  end;

  if not v_refused then
    raise exception 'Verification 0033: an administrator changed their own role.';
  end if;

  raise exception 'Verification 0033 passed, rolling back.';
exception
  when others then
    if sqlerrm = 'Verification 0033 passed, rolling back.' then
      raise notice 'Verification 0033 passed.';
    else
      raise;
    end if;
end;
$verify$;
