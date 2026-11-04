-- =============================================================================
-- 0027 — app.delete_property referenced a table that does not exist
--
-- WHAT WAS WRONG
--   The emptiness check in 0026 counted rows in `maintenance_tasks`. The table
--   is called `tasks`. Deleting any property therefore failed with
--   undefined_table, which the API did not expect and returned as a 500.
--
-- WHY 0026 APPLIED CLEANLY ANYWAY
--   PostgreSQL does not parse a plpgsql body at CREATE FUNCTION time. The
--   function was created, the migration's verification block confirmed it
--   existed, and the first thing to discover the typo was a user clicking
--   Delete.
--
--   That is the lesson worth keeping: asserting a function EXISTS proves
--   almost nothing about a plpgsql function. So the verification at the end of
--   this file actually CALLS it, against a real property, inside a
--   transaction it rolls back — which is the only check that would have caught
--   this.
-- =============================================================================

create or replace function app.delete_property(p_property_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
  v_units           integer;
  v_leases          integer;
  v_documents       integer;
  v_tasks           integer;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Deleting a property requires an authenticated user.'
      using errcode = '28000';
  end if;

  select p.organization_id into v_organization_id
  from properties p
  where p.id = p_property_id;

  if v_organization_id is null then
    raise exception 'No such property.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can delete a property.'
      using errcode = '42501';
  end if;

  -- Every table that can point at a property, counted rather than left to the
  -- foreign keys: several are ON DELETE SET NULL, which would succeed and
  -- quietly orphan the row instead of refusing.
  select count(*) into v_units   from units u  where u.property_id = p_property_id;
  select count(*) into v_leases  from leases l where l.property_id = p_property_id;

  select count(*) into v_documents
  from lease_documents d
  join leases l on l.id = d.lease_id
  where l.property_id = p_property_id;

  -- `tasks`, not `maintenance_tasks`. This is the fix.
  select count(*) into v_tasks
  from tasks t
  where t.property_id = p_property_id;

  if v_units + v_leases + v_documents + v_tasks > 0 then
    raise exception
      'This property has % unit(s), % lease(s), % document(s) and % task(s). '
      'Archive it instead, which keeps the history.',
      v_units, v_leases, v_documents, v_tasks
      using errcode = 'P0001';
  end if;

  -- Audited before the row goes, so the log is written while the property
  -- still exists and the entity_id refers to something that was real.
  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'property.deleted', 'property', p_property_id);

  delete from properties where id = p_property_id;
end;
$$;

comment on function app.delete_property(uuid) is
  'Removes a property that nothing references. Refuses otherwise and names '
  'the counts, so the caller can archive instead.';

-- The revoke is not optional: a function created in schema app gets EXECUTE
-- for PUBLIC by default, and CREATE OR REPLACE on an existing function keeps
-- whatever ACL it had. Re-asserted so neither path can widen access.
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

grant execute on function app.delete_property(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- This one RUNS the function rather than checking that it exists, because
-- existence is what 0026 checked and existence is what hid the bug.
--
-- It needs a property to call it against and an authenticated user for the
-- function to accept, so it synthesises both, exercises every branch it can,
-- and rolls the whole lot back. Nothing here survives the migration.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_org      uuid;
  v_user     uuid;
  v_auth     uuid := gen_random_uuid();
  v_property uuid;
  v_message  text;
begin
  -- A throwaway organization, user and property, so the call has something
  -- real to work on without touching anybody's data.
  insert into organizations (name, timezone)
  values ('verification-0027', 'UTC')
  returning id into v_org;

  insert into users (auth_user_id, email)
  values (v_auth, 'verification-0027@example.invalid')
  returning id into v_user;

  insert into organization_members (organization_id, user_id, role)
  values (v_org, v_user, 'owner');

  insert into properties (organization_id, name, timezone)
  values (v_org, 'verification-0027', 'UTC')
  returning id into v_property;

  -- The identity chain is app.auth_user_id (the auth subject, as a session
  -- setting) -> users.auth_user_id -> users.id. So the setting holds the AUTH
  -- id, not the users row id; setting the latter resolves to no identity at
  -- all, which is how the first draft of this block failed.
  perform set_config('app.auth_user_id', v_auth::text, true);

  -- 1. An empty property deletes.
  perform app.delete_property(v_property);

  if exists (select 1 from properties where id = v_property) then
    raise exception 'Verification 0027: delete_property did not delete an empty property.';
  end if;

  -- 2. A property with something attached is refused, and the refusal counts
  --    it. This is the branch that referenced the wrong table.
  insert into properties (organization_id, name, timezone)
  values (v_org, 'verification-0027-occupied', 'UTC')
  returning id into v_property;

  insert into units (organization_id, property_id, unit_number)
  values (v_org, v_property, '1');

  begin
    perform app.delete_property(v_property);
    raise exception 'Verification 0027: delete_property allowed a property with a unit.';
  exception
    when sqlstate 'P0001' then
      v_message := sqlerrm;

      if v_message not like '%1 unit(s)%' then
        raise exception
          'Verification 0027: refusal did not count the unit. Message was: %',
          v_message;
      end if;
  end;

  -- Everything above is scaffolding. Undo it.
  raise exception using errcode = 'P0001', message = 'verification-0027-rollback';
exception
  when sqlstate 'P0001' then
    if sqlerrm <> 'verification-0027-rollback' then
      raise;
    end if;
end
$verify$;

-- The block above raised to abandon its own work, which rolls back everything
-- it inserted. Confirm nothing survived.
do $cleanup$
begin
  if exists (select 1 from organizations where name = 'verification-0027') then
    raise exception
      'Verification 0027 left scaffolding behind. Remove the organization '
      'named verification-0027 before using this database.';
  end if;
end
$cleanup$;
