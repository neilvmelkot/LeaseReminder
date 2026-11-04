-- =============================================================================
-- 0025_invitations.sql
--
-- Joining a workspace by invitation only.
--
-- Until now anybody who could reach the sign-in page could create an account.
-- That is wrong for this product: a workspace holds lease terms, tenant
-- contact details, and a mailbox that sends in the owner's name, and the set
-- of people who may see that is decided by its owner, not by whoever finds the
-- URL.
--
-- Two design points worth stating, because both are easy to get wrong.
--
-- THE TOKEN IS NOT STORED. Only its SHA-256 digest is. A database read, a
-- backup, or a support engineer looking at a row therefore yields nothing that
-- can be redeemed. This is the same reasoning that keeps the Gmail refresh
-- token encrypted: a credential at rest should not be a working credential.
--
-- AN INVITATION NAMES A PERSON. Acceptance requires the signed-in address to
-- match the invited one, so a forwarded link cannot be redeemed by somebody
-- else. The token alone is not sufficient authority.
-- =============================================================================

create table organization_invitations (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  -- citext, so an invitation to Alice@example.com is redeemed by
  -- alice@example.com. Case is not identity in an email address.
  email           citext not null,
  role            app.org_role not null default 'read_only',

  -- SHA-256 of the token. The token itself is returned once, to the inviter,
  -- and never persisted.
  token_digest    bytea not null,

  invited_by_user_id uuid references users (id) on delete set null,
  created_at      timestamptz not null default now(),
  expires_at      timestamptz not null,

  accepted_at     timestamptz,
  accepted_by_user_id uuid references users (id) on delete set null,

  revoked_at      timestamptz,
  revoked_by_user_id uuid references users (id) on delete set null,

  constraint organization_invitations_email_shape_check
    check (email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint organization_invitations_digest_length_check
    check (octet_length(token_digest) = 32),
  constraint organization_invitations_expiry_after_creation_check
    check (expires_at > created_at),

  -- An invitation cannot be both accepted and revoked.
  constraint organization_invitations_terminal_state_check
    check (accepted_at is null or revoked_at is null),
  -- Acceptance records who accepted it.
  constraint organization_invitations_accepted_metadata_check
    check ((accepted_at is null) = (accepted_by_user_id is null)),

  -- An owner may not invite somebody to a role above their own authority to
  -- grant, but that is a rule about the caller rather than the row. What the
  -- row enforces is that an invitation always carries a real role.
  constraint organization_invitations_id_organization_key unique (id, organization_id)
);

comment on table organization_invitations is
  'Pending invitations. Only the SHA-256 digest of the token is stored, so a database '
  'read yields nothing redeemable.';

-- One live invitation per address per organization. Re-inviting somebody
-- supersedes rather than accumulating, which is what makes "revoke" meaningful.
create unique index organization_invitations_one_live_per_email
  on organization_invitations (organization_id, email)
  where accepted_at is null and revoked_at is null;

-- The redemption lookup, and the only index that matters for it.
create unique index organization_invitations_token_digest_key
  on organization_invitations (token_digest);

create index organization_invitations_organization_idx
  on organization_invitations (organization_id, created_at desc);

-- -----------------------------------------------------------------------------
-- app.bootstrap_needed
--
-- Whether this deployment has no users at all.
--
-- The first person has nobody to invite them, so a brand-new deployment
-- permits exactly one self-registration. After that the answer is false
-- forever and the only way in is an invitation.
--
-- Deliberately callable without authentication: the sign-in page has to ask
-- before anybody has signed in. It discloses one bit — whether this system has
-- been set up — which is not a secret worth protecting and is obvious from the
-- screen anyway.
-- -----------------------------------------------------------------------------
create or replace function app.bootstrap_needed()
returns boolean
language sql
stable
security definer
set search_path = app, public
as $fn$
  select not exists (select 1 from users)
$fn$;

comment on function app.bootstrap_needed is
  'True only while no user exists. Permits the very first self-registration.';

-- -----------------------------------------------------------------------------
-- app.create_invitation
--
-- Returns the invitation id. The TOKEN is generated by the caller and only its
-- digest arrives here, so this function never sees a redeemable credential
-- either.
-- -----------------------------------------------------------------------------
create or replace function app.create_invitation(
  p_organization_id uuid,
  p_email           text,
  p_role            text,
  p_token_digest    bytea,
  p_expires_in_days integer default 7
)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id       uuid;
  v_invitation_id uuid;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Inviting somebody requires an authenticated user.'
      using errcode = '28000';
  end if;

  -- Administrators only. Adding a person to a workspace grants them sight of
  -- every lease and tenant in it.
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can invite people.'
      using errcode = '42501';
  end if;

  -- Nobody may hand out ownership by invitation. An owner transfer is a
  -- deliberate, separate act, and the single-owner index in 0002 would refuse
  -- a second one anyway -- better to say so clearly than to fail on a
  -- constraint the inviter cannot see.
  if p_role = 'owner' then
    raise exception 'An owner cannot be created by invitation.'
      using errcode = '22023';
  end if;

  if p_expires_in_days is null or p_expires_in_days < 1 or p_expires_in_days > 30 then
    raise exception 'An invitation must expire between 1 and 30 days from now.'
      using errcode = '22023';
  end if;

  -- Somebody already in the workspace does not need an invitation, and
  -- sending one would imply otherwise.
  if exists (
    select 1 from organization_members m
    join users u on u.id = m.user_id
    where m.organization_id = p_organization_id and u.email = p_email::citext
  ) then
    raise exception 'That person is already a member of this workspace.'
      using errcode = '23505';
  end if;

  -- Supersede any outstanding invitation to the same address, so the partial
  -- unique index holds and the newest link is the only live one.
  update organization_invitations
  set revoked_at = now(), revoked_by_user_id = v_user_id
  where organization_id = p_organization_id
    and email = p_email::citext
    and accepted_at is null
    and revoked_at is null;

  insert into organization_invitations (
    organization_id, email, role, token_digest, invited_by_user_id, expires_at
  )
  values (
    p_organization_id, p_email::citext, p_role::app.org_role, p_token_digest,
    v_user_id, now() + make_interval(days => p_expires_in_days)
  )
  returning id into v_invitation_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    p_organization_id, v_user_id, 'invitation.created',
    'organization_invitation', v_invitation_id);

  return v_invitation_id;
end;
$fn$;

-- -----------------------------------------------------------------------------
-- app.describe_invitation
--
-- What the invite page shows BEFORE anybody signs in: which workspace, which
-- role, and which address it was sent to.
--
-- Unauthenticated by necessity. It discloses only what the holder of the token
-- already had to be told to be invited, and an expired or spent token
-- discloses nothing at all.
-- -----------------------------------------------------------------------------
create or replace function app.describe_invitation(p_token_digest bytea)
returns table (
  organization_name text,
  email             text,
  role              text,
  expires_at        timestamptz,
  is_valid          boolean
)
language sql
stable
security definer
set search_path = app, public
as $fn$
  select
    o.name,
    i.email::text,
    i.role::text,
    i.expires_at,
    (i.accepted_at is null and i.revoked_at is null and i.expires_at > now())
  from organization_invitations i
  join organizations o on o.id = i.organization_id
  where i.token_digest = p_token_digest
$fn$;

-- -----------------------------------------------------------------------------
-- app.accept_invitation
--
-- Redeems a token for the signed-in user.
--
-- The address must match. A token is a secret, but a secret that has been
-- emailed can be forwarded, and an invitation names a person rather than
-- whoever happens to be holding the link.
-- -----------------------------------------------------------------------------
create or replace function app.accept_invitation(p_token_digest bytea)
returns uuid
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id    uuid;
  v_user_email citext;
  v_invitation organization_invitations%rowtype;
begin
  v_user_id := app.current_user_id();

  if v_user_id is null then
    raise exception 'Accepting an invitation requires an authenticated user.'
      using errcode = '28000';
  end if;

  select u.email into v_user_email from users u where u.id = v_user_id;

  -- Locked, so two simultaneous redemptions cannot both succeed.
  select * into v_invitation
  from organization_invitations i
  where i.token_digest = p_token_digest
  for update;

  -- One message for every failure mode. Distinguishing "no such invitation"
  -- from "already accepted" would let somebody probe which tokens exist.
  if v_invitation.id is null
     or v_invitation.accepted_at is not null
     or v_invitation.revoked_at is not null
     or v_invitation.expires_at <= now()
  then
    raise exception 'That invitation is not valid. Ask for a new one.'
      using errcode = '22023';
  end if;

  if v_invitation.email <> v_user_email then
    raise exception 'That invitation was sent to a different address.'
      using errcode = '42501';
  end if;

  insert into organization_members (organization_id, user_id, role)
  values (v_invitation.organization_id, v_user_id, v_invitation.role)
  on conflict (organization_id, user_id) do nothing;

  update organization_invitations
  set accepted_at = now(), accepted_by_user_id = v_user_id
  where id = v_invitation.id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    v_invitation.organization_id, v_user_id, 'invitation.accepted',
    'organization_invitation', v_invitation.id);

  return v_invitation.organization_id;
end;
$fn$;

-- -----------------------------------------------------------------------------
-- app.revoke_invitation
-- -----------------------------------------------------------------------------
create or replace function app.revoke_invitation(p_invitation_id uuid)
returns boolean
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_user_id         uuid;
  v_organization_id uuid;
begin
  v_user_id := app.current_user_id();

  select i.organization_id into v_organization_id
  from organization_invitations i
  where i.id = p_invitation_id;

  if v_organization_id is null then
    raise exception 'No such invitation.' using errcode = 'P0002';
  end if;

  if not app.can_administer_org(v_organization_id) then
    raise exception 'Only an owner or administrator can revoke an invitation.'
      using errcode = '42501';
  end if;

  update organization_invitations
  set revoked_at = now(), revoked_by_user_id = v_user_id
  where id = p_invitation_id and accepted_at is null and revoked_at is null;

  return found;
end;
$fn$;

-- -----------------------------------------------------------------------------
-- app.remove_member
--
-- The counterpart to inviting. An owner cannot be removed: the single-owner
-- index in 0002 means removing one would leave a workspace nobody administers.
-- -----------------------------------------------------------------------------
create or replace function app.remove_member(
  p_organization_id uuid,
  p_user_id         uuid
)
returns boolean
language plpgsql
security definer
set search_path = app, public
as $fn$
#variable_conflict use_column
declare
  v_actor_id uuid;
  v_role     app.org_role;
begin
  v_actor_id := app.current_user_id();

  if not app.can_administer_org(p_organization_id) then
    raise exception 'Only an owner or administrator can remove a member.'
      using errcode = '42501';
  end if;

  select m.role into v_role
  from organization_members m
  where m.organization_id = p_organization_id and m.user_id = p_user_id;

  if v_role is null then
    raise exception 'That person is not a member of this workspace.'
      using errcode = 'P0002';
  end if;

  if v_role = 'owner' then
    raise exception 'The owner cannot be removed from their own workspace.'
      using errcode = '42501';
  end if;

  if p_user_id = v_actor_id then
    raise exception 'You cannot remove yourself.' using errcode = '22023';
  end if;

  delete from organization_members
  where organization_id = p_organization_id and user_id = p_user_id;

  insert into audit_logs (organization_id, actor_user_id, action, entity_type, entity_id)
  values (
    p_organization_id, v_actor_id, 'member.removed', 'user', p_user_id);

  return true;
end;
$fn$;

-- =============================================================================
-- Row Level Security
-- =============================================================================

alter table organization_invitations enable row level security;
alter table organization_invitations force row level security;

-- Members may see their workspace's invitations. Administrators act on them
-- through the functions above, which re-check rights because they are
-- SECURITY DEFINER and bypass this policy.
create policy organization_invitations_member_select on organization_invitations
  for select using (app.is_org_member(organization_id));

-- =============================================================================
-- Grants
-- =============================================================================

grant select on organization_invitations to app_api;

-- Explicit, because ALTER DEFAULT PRIVILEGES for TABLES does apply to tables
-- created later and would otherwise hand the API role write access.
revoke insert, update, delete, truncate on organization_invitations from app_api;

grant execute on function app.bootstrap_needed() to app_api;
grant execute on function app.create_invitation(uuid, text, text, bytea, integer) to app_api;
grant execute on function app.describe_invitation(bytea) to app_api;
grant execute on function app.accept_invitation(bytea) to app_api;
grant execute on function app.revoke_invitation(uuid) to app_api;
grant execute on function app.remove_member(uuid, uuid) to app_api;

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
  select count(*) into v_count
  from information_schema.role_table_grants
  where grantee in ('app_api', 'app_worker')
    and table_name = 'organization_invitations'
    and privilege_type in ('INSERT', 'UPDATE', 'DELETE', 'TRUNCATE');

  if v_count > 0 then
    raise exception
      'An application role can write organization_invitations directly; it must not.';
  end if;

  -- The worker has no business with membership at all.
  if has_function_privilege('app_worker', 'app.accept_invitation(bytea)', 'execute') then
    raise exception 'app_worker can accept invitations; it must not.';
  end if;

  select count(*) into v_count
  from pg_proc p
  join pg_namespace n on n.oid = p.pronamespace
  where n.nspname = 'app'
    and has_function_privilege('public', p.oid, 'execute');

  if v_count > 0 then
    raise exception '% function(s) in app are executable by PUBLIC.', v_count;
  end if;

  raise notice 'Invitations: grants and policies verified.';
end;
$$;
