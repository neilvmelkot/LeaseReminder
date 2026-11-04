-- =============================================================================
-- 0002_core_tables.sql
-- Users, organizations, properties, units, tenants, leases.
--
-- Two integrity patterns are used throughout and are worth stating up front:
--
--  1. COMPOSITE FOREIGN KEYS CARRYING organization_id.
--     Every child row stores its own organization_id and references its parent
--     by (parent_id, organization_id). A unit therefore cannot be attached to
--     a property in another organization, and a lease cannot borrow a tenant
--     from another organization -- the database refuses it outright, even if
--     an application bug or an RLS mistake let the request through. This is
--     defence in depth behind RLS, not a replacement for it.
--
--  2. SEPARATE STATUS COLUMNS (section 13.E).
--     Lease approval, lease lifecycle, and notification automation are three
--     independent state machines on the leases table. A lease can be approved
--     and active while its automation is paused.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- users
-- Mirrors the identity provider. Profile data lives here; credentials never do.
-- -----------------------------------------------------------------------------
create table users (
  id            uuid primary key default gen_random_uuid(),
  auth_user_id  uuid not null unique,
  email         citext not null,
  display_name  text,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),

  constraint users_email_shape_check
    check (email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint users_display_name_length_check
    check (display_name is null or char_length(display_name) between 1 and 200)
);

comment on table users is
  'Application profile for an identity-provider user. No credentials are stored.';
comment on column users.auth_user_id is
  'The identity provider subject (Supabase auth.users.id). Matched against the JWT sub claim.';

create index users_email_idx on users (email);

create trigger users_touch_updated_at
  before update on users
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- organizations
-- The ownership boundary for all tenant data. Present from the first release
-- even though only one organization exists, so that multi-landlord support
-- needs no schema redesign (section 13.A).
-- -----------------------------------------------------------------------------
create table organizations (
  id          uuid primary key default gen_random_uuid(),
  name        text not null,
  -- Default IANA zone for new properties. Never used for scheduling directly;
  -- scheduling always reads the property or rent-schedule zone.
  timezone    text not null default 'UTC',
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),

  constraint organizations_name_length_check
    check (char_length(name) between 1 and 200)
);

comment on table organizations is
  'Workspace that owns properties, leases, documents, rules, and connected accounts.';

create trigger organizations_touch_updated_at
  before update on organizations
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- Timezone validation
-- A CHECK constraint cannot query pg_timezone_names (it is not immutable), so
-- a trigger enforces that every stored zone is a real IANA zone. An invalid
-- zone would silently corrupt every future send instant.
-- -----------------------------------------------------------------------------
create or replace function app.validate_timezone_column()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
declare
  v_column text := tg_argv[0];
  v_value  text;
begin
  execute format('select ($1).%I::text', v_column) into v_value using new;

  if v_value is null then
    return new;
  end if;

  if not exists (select 1 from pg_timezone_names where name = v_value) then
    raise exception 'Invalid IANA time zone %L in %.%.%',
      v_value, tg_table_schema, tg_table_name, v_column
      using errcode = 'invalid_parameter_value';
  end if;

  return new;
end;
$fn$;

comment on function app.validate_timezone_column() is
  'Trigger: rejects a value that is not a known IANA time zone. Pass the column name as the trigger argument.';

create trigger organizations_validate_timezone
  before insert or update of timezone on organizations
  for each row execute function app.validate_timezone_column('timezone');

-- -----------------------------------------------------------------------------
-- organization_members
-- Roles exist from day one so authorization is never written against the
-- assumption of a single user. Team-management UI is deferred (section 13.F).
-- -----------------------------------------------------------------------------
create table organization_members (
  organization_id  uuid not null references organizations (id) on delete cascade,
  user_id          uuid not null references users (id) on delete cascade,
  role             app.org_role not null default 'read_only',
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),

  primary key (organization_id, user_id)
);

comment on table organization_members is
  'Membership and role of a user within an organization. The basis of all authorization.';

create index organization_members_user_idx on organization_members (user_id);

-- Exactly one owner per organization. Prevents an organization becoming
-- unadministrable by removing or demoting its last owner.
create unique index organization_members_single_owner_idx
  on organization_members (organization_id)
  where role = 'owner';

create trigger organization_members_touch_updated_at
  before update on organization_members
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- Authorization helpers
--
-- SECURITY DEFINER so they can read organization_members without being
-- filtered by the very policies that call them. search_path is pinned, they
-- take no free-form SQL, and EXECUTE is granted narrowly in 0008.
-- -----------------------------------------------------------------------------

create or replace function app.current_user_id()
returns uuid
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select u.id
  from users u
  where u.auth_user_id = app.current_auth_user_id()
$fn$;

comment on function app.current_user_id() is
  'users.id for the caller, or NULL when unauthenticated.';

create or replace function app.is_org_member(p_organization_id uuid)
returns boolean
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select exists (
    select 1
    from organization_members m
    join users u on u.id = m.user_id
    where m.organization_id = p_organization_id
      and u.auth_user_id = app.current_auth_user_id()
  )
$fn$;

comment on function app.is_org_member(uuid) is
  'True when the caller belongs to the organization. NULL identity never matches.';

create or replace function app.has_org_role(p_organization_id uuid, p_roles app.org_role[])
returns boolean
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select exists (
    select 1
    from organization_members m
    join users u on u.id = m.user_id
    where m.organization_id = p_organization_id
      and u.auth_user_id = app.current_auth_user_id()
      and m.role = any (p_roles)
  )
$fn$;

comment on function app.has_org_role(uuid, app.org_role[]) is
  'True when the caller holds one of the given roles in the organization.';

-- Write access: everything except the read-only and accounting roles.
create or replace function app.can_write_org(p_organization_id uuid)
returns boolean
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select app.has_org_role(
    p_organization_id,
    array['owner', 'admin', 'property_manager']::app.org_role[]
  )
$fn$;

comment on function app.can_write_org(uuid) is
  'True when the caller may create or modify portfolio data in the organization.';

-- Administrative access: connected accounts, retention, organization settings.
create or replace function app.can_administer_org(p_organization_id uuid)
returns boolean
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select app.has_org_role(
    p_organization_id,
    array['owner', 'admin']::app.org_role[]
  )
$fn$;

comment on function app.can_administer_org(uuid) is
  'True when the caller may change organization settings and connected accounts.';

-- -----------------------------------------------------------------------------
-- properties
-- -----------------------------------------------------------------------------
create table properties (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null references organizations (id) on delete cascade,
  name             text not null,
  property_type    app.property_type not null default 'other',

  address_line1    text,
  address_line2    text,
  locality         text,         -- city / town
  region           text,         -- state / province
  postal_code      text,
  country_code     char(2) not null default 'US',

  -- The zone every date rule for this property is interpreted in. Required:
  -- there is no safe default for "when is rent due" (section 7).
  timezone         text not null,

  notes            text,

  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,

  constraint properties_name_length_check
    check (char_length(name) between 1 and 200),
  constraint properties_country_code_check
    check (country_code ~ '^[A-Z]{2}$'),
  constraint properties_notes_length_check
    check (notes is null or char_length(notes) <= 10000),

  -- Target for composite foreign keys that carry organization_id.
  constraint properties_id_organization_key unique (id, organization_id)
);

comment on table properties is 'A building or address owned within an organization.';
comment on column properties.timezone is
  'IANA zone used to interpret every local date and send time for this property.';

create index properties_organization_idx on properties (organization_id, name);

create trigger properties_touch_updated_at
  before update on properties
  for each row execute function app.touch_updated_at();

create trigger properties_validate_timezone
  before insert or update of timezone on properties
  for each row execute function app.validate_timezone_column('timezone');

-- -----------------------------------------------------------------------------
-- units
-- -----------------------------------------------------------------------------
create table units (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null,
  property_id      uuid not null,
  unit_number      text not null,
  status           app.unit_status not null default 'vacant',
  bedrooms         smallint,
  bathrooms        numeric(3, 1),
  square_feet      integer,
  notes            text,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),

  constraint units_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete cascade,

  constraint units_unit_number_length_check
    check (char_length(unit_number) between 1 and 60),
  constraint units_bedrooms_check
    check (bedrooms is null or bedrooms between 0 and 100),
  constraint units_bathrooms_check
    check (bathrooms is null or bathrooms between 0 and 100),
  constraint units_square_feet_check
    check (square_feet is null or square_feet between 0 and 10000000),

  -- A unit number is unique within its property.
  constraint units_property_number_key unique (property_id, unit_number),
  -- Targets for composite foreign keys.
  constraint units_id_organization_key unique (id, organization_id),
  constraint units_id_property_key unique (id, property_id)
);

comment on table units is
  'A rentable unit within a property. A property may hold many units; a unit may hold many leases over time.';
comment on column units.status is
  'Physical/occupancy state of the unit. Independent of any lease status.';

create index units_property_idx on units (property_id, unit_number);
create index units_organization_idx on units (organization_id);

create trigger units_touch_updated_at
  before update on units
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- tenants
-- Personal data. One tenant record is never exposed to another tenant; only
-- organization members can read these rows.
-- -----------------------------------------------------------------------------
create table tenants (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null references organizations (id) on delete cascade,

  legal_name       text not null,
  preferred_name   text,
  primary_email    citext,
  secondary_email  citext,
  phone            text,
  status           app.tenant_status not null default 'active',

  -- Communication consent. Rules that target tenants must honour this.
  notifications_opted_in     boolean not null default true,
  notifications_opt_in_at    timestamptz,
  notifications_opt_out_at   timestamptz,
  notification_preferences   jsonb not null default '{}'::jsonb,

  notes            text,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now(),

  constraint tenants_legal_name_length_check
    check (char_length(legal_name) between 1 and 200),
  constraint tenants_preferred_name_length_check
    check (preferred_name is null or char_length(preferred_name) between 1 and 200),
  constraint tenants_primary_email_shape_check
    check (primary_email is null or
           primary_email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint tenants_secondary_email_shape_check
    check (secondary_email is null or
           secondary_email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint tenants_phone_length_check
    check (phone is null or char_length(phone) between 3 and 40),
  constraint tenants_preferences_is_object_check
    check (jsonb_typeof(notification_preferences) = 'object'),
  constraint tenants_notes_length_check
    check (notes is null or char_length(notes) <= 10000),

  constraint tenants_id_organization_key unique (id, organization_id)
);

comment on table tenants is 'A person leasing a unit. Contains personal data; retention rules apply.';
comment on column tenants.notifications_opted_in is
  'Consent gate. A notification rule targeting tenants must not send when this is false.';

create index tenants_organization_idx on tenants (organization_id, legal_name);
create index tenants_primary_email_idx on tenants (organization_id, primary_email);

create trigger tenants_touch_updated_at
  before update on tenants
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- leases
-- -----------------------------------------------------------------------------
create table leases (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null references organizations (id) on delete cascade,
  property_id      uuid not null,
  unit_id          uuid,

  -- Human-friendly label, e.g. "12 Oak St #3 - 2026 renewal".
  reference         text,

  -- Three independent state machines (section 13.E).
  lifecycle_status  app.lease_lifecycle_status not null default 'draft',
  approval_status   app.lease_approval_status not null default 'pending_review',
  automation_status app.notification_automation_status not null default 'not_configured',

  -- Term ------------------------------------------------------------------
  start_date        date,
  -- NULL means open-ended / month-to-month. Not "unknown": unknown stays NULL
  -- with a review flag on the extraction, never a guessed date.
  end_date          date,

  -- Financial terms -------------------------------------------------------
  rent_amount             numeric(14, 2),
  currency                char(3) not null default 'USD',
  security_deposit_amount numeric(14, 2),
  first_payment_date      date,

  -- Rent recurrence lives in rent_schedules, deliberately NOT duplicated here.

  grace_period_days       smallint,
  late_fee_amount         numeric(14, 2),
  -- When true, late_fee_amount is a percentage of rent rather than a currency amount.
  late_fee_is_percentage  boolean not null default false,
  -- Days after the due date at which the late fee applies. Distinct from the
  -- grace period: a lease may grant 5 grace days but charge from day 3.
  late_fee_after_days     smallint,

  -- Renewal and termination ----------------------------------------------
  renewal_notice_days     smallint,
  renewal_terms           text,
  early_termination_terms text,
  notice_period_days      smallint,

  payment_instructions    text,
  special_clauses         text,
  notes                   text,

  -- Provenance ------------------------------------------------------------
  -- Set in 0003 once lease_documents exists.
  source_document_id      uuid,

  -- Approval audit (section 13.C) ----------------------------------------
  approved_by_user_id     uuid references users (id) on delete set null,
  approved_at             timestamptz,
  rejected_by_user_id     uuid references users (id) on delete set null,
  rejected_at             timestamptz,
  rejection_reason        text,

  -- Pausing automation is independent of approval (section 13.C).
  automation_paused_at    timestamptz,
  automation_paused_reason text,

  created_at              timestamptz not null default now(),
  updated_at              timestamptz not null default now(),
  created_by_user_id      uuid references users (id) on delete set null,

  -- Referential integrity across the organization boundary ----------------
  constraint leases_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete cascade,
  -- A lease may reference no unit (single-family property), but if it does the
  -- unit must belong to the same property.
  constraint leases_unit_fk
    foreign key (unit_id, property_id)
    references units (id, property_id) on delete restrict,

  -- Value constraints -----------------------------------------------------
  constraint leases_reference_length_check
    check (reference is null or char_length(reference) between 1 and 200),
  constraint leases_term_order_check
    check (start_date is null or end_date is null or end_date >= start_date),
  constraint leases_rent_amount_check
    check (rent_amount is null or (rent_amount >= 0 and rent_amount <= 99999999.99)),
  constraint leases_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  constraint leases_security_deposit_check
    check (security_deposit_amount is null or security_deposit_amount >= 0),
  constraint leases_grace_period_check
    check (grace_period_days is null or grace_period_days between 0 and 365),
  constraint leases_late_fee_amount_check
    check (late_fee_amount is null or late_fee_amount >= 0),
  -- A percentage late fee is capped at 100.
  constraint leases_late_fee_percentage_range_check
    check (not late_fee_is_percentage
           or late_fee_amount is null
           or late_fee_amount <= 100),
  constraint leases_late_fee_after_days_check
    check (late_fee_after_days is null or late_fee_after_days between 0 and 365),
  constraint leases_renewal_notice_days_check
    check (renewal_notice_days is null or renewal_notice_days between 0 and 3650),
  constraint leases_notice_period_days_check
    check (notice_period_days is null or notice_period_days between 0 and 3650),

  -- Approval invariants ---------------------------------------------------
  -- Approval metadata and approval state cannot disagree.
  constraint leases_approved_metadata_check
    check (
      (approval_status = 'approved'
         and approved_at is not null and approved_by_user_id is not null)
      or
      (approval_status <> 'approved'
         and approved_at is null and approved_by_user_id is null)
    ),
  constraint leases_rejected_metadata_check
    check (
      (approval_status = 'rejected' and rejected_at is not null)
      or
      (approval_status <> 'rejected' and rejected_at is null)
    ),

  -- An unapproved lease can never reach a lifecycle state that schedules
  -- reminders. This is the database-level expression of "AI extraction alone
  -- must never activate a lease" (section 13.C).
  constraint leases_live_requires_approval_check
    check (
      lifecycle_status not in ('upcoming', 'active', 'expired', 'terminated')
      or approval_status = 'approved'
    ),

  -- A live lease must have the terms needed to schedule anything at all.
  constraint leases_live_requires_terms_check
    check (
      lifecycle_status not in ('upcoming', 'active')
      or (start_date is not null and rent_amount is not null)
    ),

  constraint leases_paused_metadata_check
    check (
      (automation_status = 'paused' and automation_paused_at is not null)
      or (automation_status <> 'paused' and automation_paused_at is null)
    ),

  constraint leases_id_organization_key unique (id, organization_id)
);

comment on table leases is
  'A lease agreement. Approval, lifecycle, and automation are three independent states.';
comment on column leases.approval_status is
  'Human review gate. Reminders are never generated or sent unless this is approved.';
comment on column leases.lifecycle_status is
  'Where the lease sits in its term. Constrained to require approval before going live.';
comment on column leases.automation_status is
  'Notification automation state. Pausing it does not change approval or lifecycle.';
comment on column leases.end_date is
  'NULL means open-ended. An unknown end date stays NULL and is flagged for review, never guessed.';

create index leases_organization_idx on leases (organization_id);
create index leases_property_idx on leases (property_id);
create index leases_unit_idx on leases (unit_id);
create index leases_approval_status_idx on leases (organization_id, approval_status);
create index leases_lifecycle_status_idx on leases (organization_id, lifecycle_status);
-- Supports the "expiring in 30/60/90 days" dashboard queries.
create index leases_end_date_idx on leases (organization_id, end_date)
  where lifecycle_status in ('upcoming', 'active');
-- Supports the dispatcher: only approved, active, non-paused leases may send.
create index leases_sendable_idx on leases (organization_id, id)
  where approval_status = 'approved'
    and automation_status = 'active'
    and lifecycle_status in ('upcoming', 'active');

-- One unit cannot hold two live leases covering the same dates. Back-to-back
-- terms are fine because the ranges are inclusive on both ends.
create index leases_term_gist_idx on leases
  using gist (unit_id, daterange(start_date, end_date, '[]'));

alter table leases
  add constraint leases_no_overlapping_live_terms
  exclude using gist (
    unit_id with =,
    daterange(start_date, end_date, '[]') with &&
  )
  where (
    unit_id is not null
    and start_date is not null
    and approval_status = 'approved'
    and lifecycle_status in ('upcoming', 'active')
  );

create trigger leases_touch_updated_at
  before update on leases
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- lease_tenants
-- A lease may name several tenants; a tenant may hold several leases over time.
-- -----------------------------------------------------------------------------
create table lease_tenants (
  lease_id         uuid not null,
  tenant_id        uuid not null,
  organization_id  uuid not null,
  -- 'primary', 'co_tenant', 'guarantor', 'occupant'
  role             text not null default 'co_tenant',
  created_at       timestamptz not null default now(),

  primary key (lease_id, tenant_id),

  constraint lease_tenants_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint lease_tenants_tenant_fk
    foreign key (tenant_id, organization_id)
    references tenants (id, organization_id) on delete restrict,

  constraint lease_tenants_role_check
    check (role in ('primary', 'co_tenant', 'guarantor', 'occupant'))
);

comment on table lease_tenants is 'Tenants named on a lease, with their role on it.';

create index lease_tenants_tenant_idx on lease_tenants (tenant_id);
create index lease_tenants_organization_idx on lease_tenants (organization_id);

-- At most one primary tenant per lease.
create unique index lease_tenants_single_primary_idx
  on lease_tenants (lease_id)
  where role = 'primary';
