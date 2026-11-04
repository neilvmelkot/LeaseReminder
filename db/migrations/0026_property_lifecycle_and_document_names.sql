-- =============================================================================
-- 0026 — Removing a property, and naming a document
--
-- Two unrelated gaps, in one migration because they are both small and both
-- additive.
--
-- WHY "DELETE" IS TWO OPERATIONS
--   A property added by mistake and a property with three years of leases
--   under it are not the same object, and one verb cannot serve both.
--
--   A hard delete of the second would take its leases, rent schedules, sent
--   mail and audit trail with it, which is not something a UI button should
--   be able to do — section 11.7 needs "the user removed this" and "the data
--   is gone" to remain separate facts.
--
--   A soft archive of the first leaves a row the user cannot get rid of,
--   forever, because they mistyped a name once.
--
--   So: app.delete_property refuses unless nothing references the property at
--   all, and app.archive_property handles everything else by hiding it while
--   keeping the history. The refusal names the counts, so the caller can see
--   why and reach for the other verb.
--
-- WHY A DOCUMENT GETS A SECOND NAME
--   original_filename is evidence: it is what was uploaded, and it must stay
--   exactly that. "Scan_20260104_0001.pdf" is also useless to read in a list.
--   display_name is the editable label; original_filename is never touched,
--   so the record of what arrived survives any amount of renaming.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Property lifecycle
-- -----------------------------------------------------------------------------

alter table properties
  add column if not exists archived_at timestamptz,
  add column if not exists archived_by_user_id uuid references users (id) on delete set null;

comment on column properties.archived_at is
  'Set when a property with history is withdrawn from use. Its leases, '
  'schedules and audit trail are retained.';

-- The common read is "the properties still in use", so that is the index.
create index if not exists properties_active_idx
  on properties (organization_id, name)
  where archived_at is null;

-- Both halves of the pair, or neither. A row claiming to be archived by
-- somebody but not archived is not a state worth being able to represent.
alter table properties
  drop constraint if exists properties_archived_metadata_check;

alter table properties
  add constraint properties_archived_metadata_check
  check (
    (archived_at is null and archived_by_user_id is null)
    or (archived_at is not null and archived_by_user_id is not null)
  );

-- -----------------------------------------------------------------------------
-- app.archive_property
-- -----------------------------------------------------------------------------
create or replace function app.archive_property(p_property_id uuid)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
  v_live_leases     integer;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Archiving a property requires an authenticated user.'
      using errcode = '28000';
  end if;

  select p.organization_id into v_organization_id
  from properties p
  where p.id = p_property_id;

  if v_organization_id is null then
    raise exception 'No such property.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can archive a property.'
      using errcode = '42501';
  end if;

  -- A live lease means a tenant is currently being told to pay rent here.
  -- Archiving underneath that would leave reminders going out for a property
  -- the workspace believes is retired, which is worse than refusing.
  select count(*) into v_live_leases
  from leases l
  where l.property_id = p_property_id
    and l.lifecycle_status in ('active', 'upcoming');

  if v_live_leases > 0 then
    raise exception
      'This property has % lease(s) that are active or upcoming. End or '
      'terminate them before archiving it.', v_live_leases
      using errcode = 'P0001';
  end if;

  update properties
  set archived_at = now(),
      archived_by_user_id = v_user_id,
      updated_at = now()
  where id = p_property_id
    and archived_at is null;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'property.archived', 'property', p_property_id);
end;
$$;

comment on function app.archive_property(uuid) is
  'Withdraws a property from use, keeping its leases and history. Refuses '
  'while any lease on it is active or upcoming.';

-- -----------------------------------------------------------------------------
-- app.restore_property
-- -----------------------------------------------------------------------------
create or replace function app.restore_property(p_property_id uuid)
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
    raise exception 'Restoring a property requires an authenticated user.'
      using errcode = '28000';
  end if;

  select p.organization_id into v_organization_id
  from properties p
  where p.id = p_property_id;

  if v_organization_id is null then
    raise exception 'No such property.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can restore a property.'
      using errcode = '42501';
  end if;

  update properties
  set archived_at = null,
      archived_by_user_id = null,
      updated_at = now()
  where id = p_property_id
    and archived_at is not null;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'property.restored', 'property', p_property_id);
end;
$$;

-- -----------------------------------------------------------------------------
-- app.delete_property
--
-- The genuine delete, for a property that has accumulated nothing. The
-- emptiness check is the whole safety argument, so it counts every table that
-- can point at a property rather than trusting the foreign keys to complain:
-- some of them are ON DELETE SET NULL, which would succeed and silently
-- orphan the row.
-- -----------------------------------------------------------------------------
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

  select count(*) into v_units   from units u  where u.property_id = p_property_id;
  select count(*) into v_leases  from leases l where l.property_id = p_property_id;

  select count(*) into v_documents
  from lease_documents d
  join leases l on l.id = d.lease_id
  where l.property_id = p_property_id;

  select count(*) into v_tasks
  from maintenance_tasks t
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

-- -----------------------------------------------------------------------------
-- Document names
-- -----------------------------------------------------------------------------

alter table lease_documents
  add column if not exists display_name text;

comment on column lease_documents.display_name is
  'User-given label. original_filename is evidence of what was uploaded and '
  'is never modified.';

alter table lease_documents
  drop constraint if exists lease_documents_display_name_length_check;

alter table lease_documents
  add constraint lease_documents_display_name_length_check
  check (display_name is null or char_length(display_name) between 1 and 300);

-- -----------------------------------------------------------------------------
-- app.rename_lease_document
-- -----------------------------------------------------------------------------
create or replace function app.rename_lease_document(
  p_document_id  uuid,
  p_display_name text)
returns void
language plpgsql
security definer
set search_path = app, public
as $$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
  v_name            text;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Renaming a document requires an authenticated user.'
      using errcode = '28000';
  end if;

  select d.organization_id into v_organization_id
  from lease_documents d
  where d.id = p_document_id and d.deleted_at is null;

  if v_organization_id is null then
    raise exception 'No such document.' using errcode = 'P0002';
  end if;

  -- Renaming is a label change, not a destructive act, so it sits at write
  -- level rather than administrator level — the same people who may upload a
  -- document may name it.
  if not app.can_write_org(v_organization_id) then
    raise exception 'Your role cannot rename documents.'
      using errcode = '42501';
  end if;

  -- An empty name clears the label and falls back to the filename, rather
  -- than storing a blank that renders as a nameless row.
  v_name := nullif(btrim(coalesce(p_display_name, '')), '');

  if v_name is not null and char_length(v_name) > 300 then
    raise exception 'A document name may be at most 300 characters.'
      using errcode = '22001';
  end if;

  update lease_documents
  set display_name = v_name, updated_at = now()
  where id = p_document_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (v_organization_id, v_user_id, 'document.renamed', 'lease_document', p_document_id);
end;
$$;

-- -----------------------------------------------------------------------------
-- Grants
--
-- The API only. None of this is the worker's business: it reads documents to
-- extract from them and never relabels or removes anything.
--
-- The revoke comes first and is not optional. A new function in schema app is
-- created with EXECUTE granted to PUBLIC — that is PostgreSQL's built-in
-- default for functions, and there is no default-privileges rule here that
-- removes it. Without this line app.delete_property would be callable by
-- app_worker, which is how the verification block at the end of this file
-- caught its own first draft.
-- -----------------------------------------------------------------------------
revoke all on all functions in schema app from public;
revoke all on all routines in schema app from public;

grant execute on function app.archive_property(uuid) to app_api;
grant execute on function app.restore_property(uuid) to app_api;
grant execute on function app.delete_property(uuid) to app_api;
grant execute on function app.rename_lease_document(uuid, text) to app_api;

-- -----------------------------------------------------------------------------
-- Verification
--
-- Asserts the shape this migration claims to have created, so a partially
-- applied or drifted database fails here rather than at the first request.
-- -----------------------------------------------------------------------------
do $verify$
declare
  v_missing text;
begin
  select string_agg(expected, ', ')
    into v_missing
  from (
    values
      ('properties.archived_at'),
      ('properties.archived_by_user_id'),
      ('lease_documents.display_name')
  ) as required (expected)
  where not exists (
    select 1
    from information_schema.columns c
    -- Tables are in public; only the functions live in app. Getting this
    -- wrong is how the first run of this migration failed.
    where c.table_schema = 'public'
      and c.table_name = split_part(required.expected, '.', 1)
      and c.column_name = split_part(required.expected, '.', 2)
  );

  if v_missing is not null then
    raise exception 'Migration 0026 did not create: %', v_missing;
  end if;

  select string_agg(expected, ', ')
    into v_missing
  from (
    values
      ('archive_property'),
      ('restore_property'),
      ('delete_property'),
      ('rename_lease_document')
  ) as required (expected)
  where not exists (
    select 1
    from pg_proc p
    join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'app' and p.proname = required.expected
  );

  if v_missing is not null then
    raise exception 'Migration 0026 did not create function(s): %', v_missing;
  end if;

  -- The grants are the security boundary, not a convenience, so they are
  -- asserted too: a function the API cannot execute is a feature that fails
  -- at runtime with a permission error.
  if not has_function_privilege(
       'app_api', 'app.delete_property(uuid)', 'execute') then
    raise exception 'app_api cannot execute app.delete_property';
  end if;

  if has_function_privilege(
       'app_worker', 'app.delete_property(uuid)', 'execute') then
    raise exception 'app_worker must not be able to execute app.delete_property';
  end if;
end
$verify$;
