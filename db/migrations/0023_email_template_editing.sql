-- =============================================================================
-- 0023_email_template_editing.sql
--
-- Editing the wording of what gets sent.
--
-- The hard constraint was set in 0005: a PUBLISHED template version is
-- immutable, enforced by a trigger, so that an email log entry always refers
-- to text that still says what it said when it was sent. A log that points at
-- wording somebody has since edited is not evidence of anything.
--
-- That makes "edit a template" mean something specific here:
--
--   * editing works on a DRAFT, which is freely mutable;
--   * publishing promotes that draft and archives whatever was published
--     before, in one transaction;
--   * the previously published version is kept, not deleted, because old log
--     entries still point at it.
--
-- There is exactly one published version per (organization, template_key),
-- enforced by a partial unique index. The swap below has to respect that,
-- which is why it archives before it promotes.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- app.save_email_template_draft
--
-- Creates or updates the draft for a template key. Never touches a published
-- version: the trigger would refuse, and refusing is correct.
-- -----------------------------------------------------------------------------
create or replace function app.save_email_template_draft(
  p_organization_id uuid,
  p_template_key    text,
  p_name            text,
  p_subject         text,
  p_html_body       text,
  p_text_body       text,
  p_referenced_variables text[] default '{}'
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id  uuid;
  v_draft_id uuid;
  v_next     integer;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Editing a template requires an authenticated user.'
      using errcode = '28000';
  end if;

  -- Administrators only. A template is the wording every tenant receives in
  -- the organization's name.
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can edit email templates.'
      using errcode = '42501';
  end if;

  select t.id into v_draft_id
  from email_templates t
  where t.organization_id = p_organization_id
    and t.template_key = p_template_key
    and t.status = 'draft'
  limit 1;

  if v_draft_id is not null then
    update email_templates
    set name = p_name,
        subject = p_subject,
        html_body = p_html_body,
        text_body = p_text_body,
        referenced_variables = coalesce(p_referenced_variables, '{}'),
        updated_at = now()
    where id = v_draft_id;

    return v_draft_id;
  end if;

  -- A new draft always sits above every existing version, published or
  -- archived, so version numbers only ever go up and a log entry's version
  -- never becomes ambiguous.
  select coalesce(max(t.version), 0) + 1 into v_next
  from email_templates t
  where t.organization_id = p_organization_id
    and t.template_key = p_template_key;

  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, created_by_user_id
  )
  select
    p_organization_id, p_template_key, p_name,
    -- Inherited from whatever is published, so a draft keeps describing the
    -- same event without the editor having to restate it.
    (select t.event_type from email_templates t
     where t.organization_id = p_organization_id
       and t.template_key = p_template_key
     order by t.version desc limit 1),
    v_next, 'draft',
    p_subject, p_html_body, p_text_body,
    coalesce(p_referenced_variables, '{}'), v_user_id
  returning id into v_draft_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    p_organization_id, v_user_id, 'email_template.draft_saved',
    'email_template', v_draft_id);

  return v_draft_id;
end;
$fn$;

comment on function app.save_email_template_draft is
  'Creates or updates the draft version of a template. Never modifies a published one.';

-- -----------------------------------------------------------------------------
-- app.publish_email_template
--
-- Promotes a draft and archives the version it replaces, in one transaction.
--
-- Order matters: the partial unique index permits exactly one published
-- version per key, so the outgoing one has to be archived before the incoming
-- one is promoted. Doing it the other way round fails on the index.
-- -----------------------------------------------------------------------------
create or replace function app.publish_email_template(p_template_id uuid)
returns boolean
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
  v_template_key    text;
  v_status          app.email_template_status;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Publishing a template requires an authenticated user.'
      using errcode = '28000';
  end if;

  select t.organization_id, t.template_key, t.status
    into v_organization_id, v_template_key, v_status
  from email_templates t
  where t.id = p_template_id;

  if v_organization_id is null then
    raise exception 'No such template.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can publish email templates.'
      using errcode = '42501';
  end if;

  if v_status <> 'draft' then
    raise exception 'Only a draft can be published; this version is already %.', v_status
      using errcode = '22023';
  end if;

  -- Archived, not deleted. Email log entries reference the version that was
  -- actually sent, and removing it would leave them pointing at nothing.
  update email_templates
  set status = 'archived', updated_at = now()
  where organization_id = v_organization_id
    and template_key = v_template_key
    and status = 'published';

  update email_templates
  set status = 'published', published_at = now(), updated_at = now()
  where id = p_template_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    v_organization_id, v_user_id, 'email_template.published',
    'email_template', p_template_id);

  return true;
end;
$fn$;

comment on function app.publish_email_template is
  'Promotes a draft and archives the version it replaces. Old versions are kept so '
  'email logs keep referring to the text that was actually sent.';

-- -----------------------------------------------------------------------------
-- app.discard_email_template_draft
-- -----------------------------------------------------------------------------
create or replace function app.discard_email_template_draft(p_template_id uuid)
returns boolean
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_organization_id uuid;
  v_status          app.email_template_status;
begin
  select t.organization_id, t.status into v_organization_id, v_status
  from email_templates t
  where t.id = p_template_id;

  if v_organization_id is null then
    raise exception 'No such template.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can discard a draft.'
      using errcode = '42501';
  end if;

  -- A draft has never been sent, so nothing refers to it and deleting it is
  -- safe. A published or archived version is a different matter entirely.
  if v_status <> 'draft' then
    raise exception 'Only a draft can be discarded.' using errcode = '22023';
  end if;

  delete from email_templates where id = p_template_id;

  return true;
end;
$fn$;

-- =============================================================================
-- Grants
-- =============================================================================

grant execute on function app.save_email_template_draft(
  uuid, text, text, text, text, text, text[]) to app_api;
grant execute on function app.publish_email_template(uuid) to app_api;
grant execute on function app.discard_email_template_draft(uuid) to app_api;

-- PUBLIC execute is implicit rather than a stored grant, so it has to be
-- revoked explicitly every time functions are added.
revoke all on all functions in schema app from public;

-- =============================================================================
-- Verification
-- =============================================================================

do $$
declare
  v_count integer;
begin
  -- The worker renders templates but must never edit them: wording is a
  -- human decision, and a background process that could rewrite it is a
  -- background process that could change what tenants are told.
  if has_function_privilege(
       'app_worker',
       'app.save_email_template_draft(uuid, text, text, text, text, text, text[])',
       'execute')
  then
    raise exception 'app_worker can edit email templates; it must not.';
  end if;

  if has_function_privilege('app_worker', 'app.publish_email_template(uuid)', 'execute') then
    raise exception 'app_worker can publish email templates; it must not.';
  end if;

  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Email template editing: grants verified.';
end;
$$;
