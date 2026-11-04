-- =============================================================================
-- 0005_notifications_and_email.sql
-- Recipients, notification rules with three-level inheritance, the connected
-- Gmail account, email templates, the durable send queue, and append-only logs.
--
-- Scheduling model
--   rent_occurrences holds LOCAL dates. A notification rule turns one of those
--   local dates plus an offset and a local send time into a single UTC
--   instant, stored on scheduled_notifications.scheduled_at_utc. The intended
--   IANA zone and the local send time are stored alongside it, so a future
--   daylight-saving change can be detected and the instant recomputed
--   (section 7).
--
-- Duplicate prevention
--   Every scheduled notification carries a deterministic idempotency_key with
--   a UNIQUE constraint. Two workers racing, or a regeneration running twice,
--   cannot produce two sends for the same (rule, event, occurrence).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- recipients / recipient_groups
-- -----------------------------------------------------------------------------
create table recipients (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  email           citext not null,
  display_name    text,
  -- Optional link to a tenant, so tenant consent can be honoured when this
  -- recipient is really a tenant rather than a staff mailbox.
  tenant_id       uuid,
  active          boolean not null default true,
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint recipients_tenant_fk
    foreign key (tenant_id, organization_id)
    references tenants (id, organization_id) on delete set null,

  constraint recipients_email_shape_check
    check (email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'
           and char_length(email) <= 320),
  constraint recipients_display_name_length_check
    check (display_name is null or char_length(display_name) between 1 and 200),

  constraint recipients_organization_email_key unique (organization_id, email),
  constraint recipients_id_organization_key unique (id, organization_id)
);

comment on table recipients is
  'A reusable email destination. May be linked to a tenant so consent is respected.';

create index recipients_organization_idx on recipients (organization_id, email);
create index recipients_tenant_idx on recipients (tenant_id);

create trigger recipients_touch_updated_at
  before update on recipients
  for each row execute function app.touch_updated_at();

create table recipient_groups (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  name            text not null,
  -- 'landlord', 'property_manager', 'tenant', 'accounting', 'maintenance', 'custom'
  kind            text not null default 'custom',
  description     text,
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint recipient_groups_name_length_check
    check (char_length(name) between 1 and 120),
  constraint recipient_groups_kind_check
    check (kind in ('landlord', 'property_manager', 'tenant',
                    'accounting', 'maintenance', 'custom')),
  constraint recipient_groups_description_length_check
    check (description is null or char_length(description) <= 1000),

  constraint recipient_groups_organization_name_key unique (organization_id, name),
  constraint recipient_groups_id_organization_key unique (id, organization_id)
);

comment on table recipient_groups is
  'Named, reusable set of recipients, e.g. Landlord or Accounting.';

create trigger recipient_groups_touch_updated_at
  before update on recipient_groups
  for each row execute function app.touch_updated_at();

create table recipient_group_members (
  recipient_group_id uuid not null,
  recipient_id       uuid not null,
  organization_id    uuid not null,
  created_at         timestamptz not null default now(),

  primary key (recipient_group_id, recipient_id),

  constraint recipient_group_members_group_fk
    foreign key (recipient_group_id, organization_id)
    references recipient_groups (id, organization_id) on delete cascade,
  constraint recipient_group_members_recipient_fk
    foreign key (recipient_id, organization_id)
    references recipients (id, organization_id) on delete cascade
);

comment on table recipient_group_members is 'Membership of a recipient in a group.';

create index recipient_group_members_recipient_idx
  on recipient_group_members (recipient_id);
create index recipient_group_members_organization_idx
  on recipient_group_members (organization_id);

-- -----------------------------------------------------------------------------
-- email_suppressions
-- Opt-out and bounce suppression. Checked immediately before every send, not
-- only at scheduling time.
-- -----------------------------------------------------------------------------
create table email_suppressions (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  email           citext not null,
  reason          text not null,
  note            text,
  created_at      timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,
  -- NULL means permanent.
  expires_at      timestamptz,

  constraint email_suppressions_email_shape_check
    check (email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint email_suppressions_reason_check
    check (reason in ('user_opt_out', 'hard_bounce', 'complaint',
                      'manual', 'invalid_address')),
  constraint email_suppressions_note_length_check
    check (note is null or char_length(note) <= 1000),

  constraint email_suppressions_organization_email_key unique (organization_id, email)
);

comment on table email_suppressions is
  'Addresses that must not be emailed. Re-checked at send time, not only when scheduling.';

create index email_suppressions_lookup_idx on email_suppressions (organization_id, email);

-- -----------------------------------------------------------------------------
-- email_accounts
-- The connected Gmail sender. The first release supports exactly one per
-- organization (section 13.B), enforced by a partial unique index that is
-- trivial to drop when multiple senders are added.
--
-- The refresh token is stored as AES-256-GCM ciphertext. The key lives in the
-- environment, never in this database, and the key version is recorded so a
-- rotation can re-encrypt row by row.
-- -----------------------------------------------------------------------------
create table email_accounts (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  provider        text not null default 'gmail',
  email_address   citext not null,
  display_name    text,

  -- Authenticated encryption, stored as separate parts so nothing has to be
  -- re-parsed out of a blob.
  encrypted_refresh_token bytea,
  token_nonce             bytea,
  token_auth_tag          bytea,
  token_key_version       integer,

  -- Scopes actually granted, so the UI can tell the user what is missing.
  granted_scopes  text[] not null default '{}',

  connection_status app.email_connection_status not null default 'not_connected',
  status_detail     text,

  connected_at      timestamptz,
  last_successful_send_at timestamptz,
  last_error_at     timestamptz,
  disconnected_at   timestamptz,

  -- Abuse guard (section 4.G). Enforced by the dispatcher.
  daily_send_limit  integer not null default 500,

  is_default      boolean not null default true,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint email_accounts_provider_check
    check (provider in ('gmail')),
  constraint email_accounts_email_shape_check
    check (email_address ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint email_accounts_display_name_length_check
    check (display_name is null or char_length(display_name) between 1 and 200),
  constraint email_accounts_daily_send_limit_check
    check (daily_send_limit between 0 and 100000),
  constraint email_accounts_status_detail_length_check
    check (status_detail is null or char_length(status_detail) <= 1000),

  -- A connected account must carry a complete, versioned ciphertext. This is
  -- what makes "connected but unusable" impossible to represent.
  constraint email_accounts_connected_has_token_check
    check (
      connection_status <> 'connected'
      or (encrypted_refresh_token is not null
          and token_nonce is not null
          and token_auth_tag is not null
          and token_key_version is not null
          and connected_at is not null)
    ),
  -- Disconnecting or revoking must destroy the stored credential outright.
  constraint email_accounts_revoked_has_no_token_check
    check (
      connection_status not in ('revoked', 'not_connected')
      or (encrypted_refresh_token is null
          and token_nonce is null
          and token_auth_tag is null)
    ),
  -- AES-GCM standard sizes.
  constraint email_accounts_nonce_length_check
    check (token_nonce is null or octet_length(token_nonce) = 12),
  constraint email_accounts_auth_tag_length_check
    check (token_auth_tag is null or octet_length(token_auth_tag) = 16),

  constraint email_accounts_organization_email_key unique (organization_id, email_address),
  constraint email_accounts_id_organization_key unique (id, organization_id)
);

comment on table email_accounts is
  'Connected Gmail sender. Refresh token stored as AES-256-GCM ciphertext; the key is never in this database.';
comment on column email_accounts.token_key_version is
  'Which encryption key version produced the ciphertext. Enables planned key rotation.';
comment on column email_accounts.connection_status is
  'Independent of lease status. A revoked connection pauses sending without changing any lease.';

-- First release: one connected sender per organization. Drop this index to
-- support multiple senders; nothing else in the schema assumes a single one.
create unique index email_accounts_one_per_organization_idx
  on email_accounts (organization_id);

create index email_accounts_status_idx on email_accounts (connection_status);

create trigger email_accounts_touch_updated_at
  before update on email_accounts
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- email_templates
-- Versioned. A published version is immutable; editing creates a new version,
-- so an email log always points at the exact text that was sent.
-- -----------------------------------------------------------------------------
create table email_templates (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,

  -- Stable identity across versions.
  template_key    text not null,
  name            text not null,
  -- Which event this template is written for. NULL means general purpose.
  event_type      app.notification_event_type,

  version         integer not null default 1,
  status          app.email_template_status not null default 'draft',

  subject         text not null,
  html_body       text not null,
  text_body       text not null,

  -- Variables the body actually references, extracted and stored at save time
  -- so the editor can warn about unknown or missing ones.
  referenced_variables text[] not null default '{}',

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,
  published_at    timestamptz,

  constraint email_templates_template_key_check
    check (template_key ~ '^[a-z][a-z0-9_]*$' and char_length(template_key) <= 80),
  constraint email_templates_name_length_check
    check (char_length(name) between 1 and 160),
  constraint email_templates_version_check
    check (version >= 1),
  constraint email_templates_subject_length_check
    check (char_length(subject) between 1 and 500),
  constraint email_templates_html_body_length_check
    check (char_length(html_body) between 1 and 200000),
  constraint email_templates_text_body_length_check
    check (char_length(text_body) between 1 and 200000),
  constraint email_templates_published_metadata_check
    check ((status = 'published') = (published_at is not null)),

  constraint email_templates_key_version_key
    unique (organization_id, template_key, version),
  constraint email_templates_id_organization_key unique (id, organization_id)
);

comment on table email_templates is
  'Versioned email template. A published version is immutable so logs reference exact sent text.';
comment on column email_templates.referenced_variables is
  'Variables the body uses, captured at save time to validate against the available set.';

-- Only one published version per template key.
create unique index email_templates_one_published_idx
  on email_templates (organization_id, template_key)
  where status = 'published';

create index email_templates_organization_idx on email_templates (organization_id, name);

create trigger email_templates_touch_updated_at
  before update on email_templates
  for each row execute function app.touch_updated_at();

-- A published version must never change. Editing produces a new version.
create or replace function app.protect_published_template()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  if old.status = 'published' and new.status = 'published' then
    if new.subject is distinct from old.subject
       or new.html_body is distinct from old.html_body
       or new.text_body is distinct from old.text_body then
      raise exception
        'Published template %L version % is immutable. Create a new version instead.',
        old.template_key, old.version
        using errcode = 'integrity_constraint_violation';
    end if;
  end if;
  return new;
end;
$fn$;

comment on function app.protect_published_template() is
  'Rejects content edits to a published template version, forcing a new version.';

create trigger email_templates_protect_published
  before update on email_templates
  for each row execute function app.protect_published_template();

-- -----------------------------------------------------------------------------
-- notification_rules
-- Three levels in one table (section 6.G):
--   global    property_id IS NULL and lease_id IS NULL
--   property  property_id IS NOT NULL
--   lease     lease_id IS NOT NULL
--
-- `scope` is generated from those columns so it can never drift out of step
-- with them, and inheritance_mode records how this rule relates to an
-- ancestor so the UI can show inherited / overridden / added / disabled.
-- -----------------------------------------------------------------------------
create table notification_rules (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  property_id     uuid,
  lease_id        uuid,

  scope           text not null generated always as (
    case
      when lease_id is not null then 'lease'
      when property_id is not null then 'property'
      else 'global'
    end
  ) stored,

  -- Inheritance ------------------------------------------------------------
  inheritance_mode app.rule_inheritance_mode not null default 'additional',
  -- The ancestor rule this one overrides or disables.
  overrides_rule_id uuid references notification_rules (id) on delete cascade,

  -- Trigger ----------------------------------------------------------------
  event_type      app.notification_event_type not null,
  trigger_type    app.notification_trigger_type not null,
  offset_value    integer not null default 0,
  offset_unit     app.offset_unit not null default 'days',

  -- Local wall-clock send time, interpreted in `timezone`.
  send_local_time time not null default '09:00',
  -- NULL means "inherit the property or rent-schedule zone at generation time".
  timezone        text,

  -- Quiet hours: if the computed local time falls inside the window, the send
  -- is deferred to quiet_hours_end (or skipped, per defer_outside_window).
  quiet_hours_start time,
  quiet_hours_end   time,
  -- When true, a send outside the window is moved; when false, it is skipped.
  defer_outside_window boolean not null default true,

  -- Delivery ---------------------------------------------------------------
  email_account_id uuid,
  template_id      uuid,
  -- Protects recipient privacy by default: separate messages per recipient.
  send_separately  boolean not null default false,

  -- Guard rails (section 4.G) ---------------------------------------------
  -- Hard ceiling on sends this rule may ever produce. NULL means unlimited.
  max_total_sends  integer,
  -- Ceiling per lease per calendar day.
  max_sends_per_day integer not null default 10,

  priority        smallint not null default 100,
  description     text,
  enabled         boolean not null default true,

  -- Set when the rule is auto-paused after repeated failures or odd volume.
  paused_at       timestamptz,
  paused_reason   text,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,

  constraint notification_rules_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete cascade,
  constraint notification_rules_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint notification_rules_email_account_fk
    foreign key (email_account_id, organization_id)
    references email_accounts (id, organization_id) on delete restrict,
  constraint notification_rules_template_fk
    foreign key (template_id, organization_id)
    references email_templates (id, organization_id) on delete restrict,

  -- A lease-scoped rule must not also name a property directly; the property
  -- is reached through the lease. This keeps `scope` unambiguous.
  constraint notification_rules_scope_shape_check
    check (lease_id is null or property_id is null),

  -- 'on' means no offset; 'before'/'after' require a positive one.
  constraint notification_rules_offset_shape_check
    check (
      (trigger_type = 'on' and offset_value = 0)
      or (trigger_type in ('before', 'after') and offset_value > 0)
    ),
  -- Bounds per unit, so a typo cannot schedule something years away.
  constraint notification_rules_offset_bounds_check
    check (
      (offset_unit = 'minutes' and offset_value <= 10080)
      or (offset_unit = 'hours' and offset_value <= 8760)
      or (offset_unit = 'days' and offset_value <= 3650)
    ),

  -- Quiet hours are set as a pair or not at all.
  constraint notification_rules_quiet_hours_pair_check
    check ((quiet_hours_start is null) = (quiet_hours_end is null)),

  constraint notification_rules_max_total_sends_check
    check (max_total_sends is null or max_total_sends between 1 and 100000),
  constraint notification_rules_max_sends_per_day_check
    check (max_sends_per_day between 1 and 100),
  constraint notification_rules_priority_check
    check (priority between 0 and 1000),
  constraint notification_rules_description_length_check
    check (description is null or char_length(description) <= 1000),

  -- Overriding or disabling requires naming the ancestor; the other modes
  -- must not name one.
  constraint notification_rules_override_target_check
    check (
      (inheritance_mode in ('overridden', 'disabled') and overrides_rule_id is not null)
      or (inheritance_mode in ('inherited', 'additional') and overrides_rule_id is null)
    ),
  -- A rule cannot override itself.
  constraint notification_rules_no_self_override_check
    check (overrides_rule_id is null or overrides_rule_id <> id),

  constraint notification_rules_paused_metadata_check
    check ((paused_at is null) = (paused_reason is null)),

  constraint notification_rules_id_organization_key unique (id, organization_id)
);

comment on table notification_rules is
  'Notification rule at global, property, or lease level. Inheritance is explicit and visible.';
comment on column notification_rules.scope is
  'Generated from property_id/lease_id so it can never disagree with them.';
comment on column notification_rules.timezone is
  'NULL means inherit the property or rent-schedule zone when the send instant is computed.';
comment on column notification_rules.send_separately is
  'When true, one message per recipient, so recipients never see each other addresses.';
comment on column notification_rules.max_total_sends is
  'Hard ceiling against runaway automation. NULL means unlimited.';

create index notification_rules_organization_idx
  on notification_rules (organization_id, event_type);
create index notification_rules_property_idx on notification_rules (property_id)
  where property_id is not null;
create index notification_rules_lease_idx on notification_rules (lease_id)
  where lease_id is not null;
create index notification_rules_overrides_idx on notification_rules (overrides_rule_id)
  where overrides_rule_id is not null;
create index notification_rules_global_idx on notification_rules (organization_id, event_type)
  where property_id is null and lease_id is null;

create trigger notification_rules_touch_updated_at
  before update on notification_rules
  for each row execute function app.touch_updated_at();

create trigger notification_rules_validate_timezone
  before insert or update of timezone on notification_rules
  for each row execute function app.validate_timezone_column('timezone');

-- An override must target a rule for the same event at a broader scope, and
-- within the same organization. Expressed as a trigger because a CHECK cannot
-- read another row.
create or replace function app.validate_rule_override()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
declare
  v_parent notification_rules;
begin
  if new.overrides_rule_id is null then
    return new;
  end if;

  select * into v_parent
  from notification_rules
  where id = new.overrides_rule_id;

  if not found then
    raise exception 'Overridden rule % does not exist', new.overrides_rule_id
      using errcode = 'foreign_key_violation';
  end if;

  if v_parent.organization_id <> new.organization_id then
    raise exception 'A rule cannot override a rule in another organization'
      using errcode = 'insufficient_privilege';
  end if;

  if v_parent.event_type <> new.event_type then
    raise exception
      'Rule override must target the same event type (% overriding %)',
      new.event_type, v_parent.event_type
      using errcode = 'integrity_constraint_violation';
  end if;

  -- Only narrower scopes may override broader ones: lease > property > global.
  if not (
    (new.scope = 'lease' and v_parent.scope in ('property', 'global'))
    or (new.scope = 'property' and v_parent.scope = 'global')
  ) then
    raise exception
      'Scope %L cannot override scope %L; overrides must narrow, never widen',
      new.scope, v_parent.scope
      using errcode = 'integrity_constraint_violation';
  end if;

  -- A property-scoped override must belong to the same property as its target
  -- when the target is property-scoped.
  if v_parent.scope = 'property'
     and new.scope = 'property'
     and v_parent.property_id <> new.property_id then
    raise exception 'A property rule cannot override another property rule'
      using errcode = 'integrity_constraint_violation';
  end if;

  return new;
end;
$fn$;

comment on function app.validate_rule_override() is
  'Ensures a rule override targets the same event at a strictly broader scope in the same organization.';

create trigger notification_rules_validate_override
  after insert or update of overrides_rule_id, event_type, property_id, lease_id
  on notification_rules
  for each row execute function app.validate_rule_override();

-- -----------------------------------------------------------------------------
-- notification_rule_recipients
-- A rule targets groups, individual recipients, or both, each as to/cc/bcc.
-- -----------------------------------------------------------------------------
create table notification_rule_recipients (
  id                   uuid primary key default gen_random_uuid(),
  organization_id      uuid not null,
  notification_rule_id uuid not null,

  -- Exactly one of these is set.
  recipient_id         uuid,
  recipient_group_id   uuid,

  recipient_kind       app.recipient_kind not null default 'to',
  created_at           timestamptz not null default now(),

  constraint notification_rule_recipients_rule_fk
    foreign key (notification_rule_id, organization_id)
    references notification_rules (id, organization_id) on delete cascade,
  constraint notification_rule_recipients_recipient_fk
    foreign key (recipient_id, organization_id)
    references recipients (id, organization_id) on delete cascade,
  constraint notification_rule_recipients_group_fk
    foreign key (recipient_group_id, organization_id)
    references recipient_groups (id, organization_id) on delete cascade,

  constraint notification_rule_recipients_exactly_one_target_check
    check ((recipient_id is null) <> (recipient_group_id is null))
);

comment on table notification_rule_recipients is
  'Targets of a rule: individual recipients or groups, each as to, cc, or bcc.';

create unique index notification_rule_recipients_unique_recipient_idx
  on notification_rule_recipients (notification_rule_id, recipient_id, recipient_kind)
  where recipient_id is not null;
create unique index notification_rule_recipients_unique_group_idx
  on notification_rule_recipients (notification_rule_id, recipient_group_id, recipient_kind)
  where recipient_group_id is not null;
create index notification_rule_recipients_organization_idx
  on notification_rule_recipients (organization_id);

-- -----------------------------------------------------------------------------
-- scheduled_notifications
-- The durable send queue. PostgreSQL is the source of truth; no in-memory
-- timer is authoritative (section 7).
-- -----------------------------------------------------------------------------
create table scheduled_notifications (
  id                   uuid primary key default gen_random_uuid(),
  organization_id      uuid not null references organizations (id) on delete cascade,
  notification_rule_id uuid not null,
  lease_id             uuid not null,
  -- Set for rent-related events; NULL for lease-level events such as expiry.
  rent_occurrence_id   uuid,

  event_type           app.notification_event_type not null,
  -- The local date of the underlying event, for display and grouping.
  event_local_date     date not null,

  -- The instant to send. The only value the dispatcher orders by.
  scheduled_at_utc     timestamptz not null,
  -- The intent behind that instant, kept so it can be recomputed if the zone
  -- rules change or the local time is edited.
  local_timezone       text not null,
  local_send_time      time not null,
  -- True when the local time did not exist or was ambiguous at a DST
  -- transition, so the UI can explain which instant was chosen.
  dst_adjusted         boolean not null default false,

  -- Deterministic. The UNIQUE constraint below is what actually prevents
  -- duplicate reminders.
  idempotency_key      text not null,

  status               app.scheduled_notification_status not null default 'scheduled',
  attempt_count        integer not null default 0,
  max_attempts         integer not null default 5,
  next_attempt_at      timestamptz,

  -- Atomic claim fields. A claim expires so an abandoned job is reclaimed.
  locked_at            timestamptz,
  locked_by            text,
  lock_expires_at      timestamptz,

  sent_at              timestamptz,
  last_error_category  app.email_error_category not null default 'none',
  -- Operator-safe. Never a provider payload, never recipient content.
  last_error_message   text,

  -- Resolved at generation time and re-validated at send time, so later edits
  -- to a group do not silently rewrite a pending send.
  email_account_id     uuid,
  template_id          uuid,
  recipients_snapshot  jsonb not null default '[]'::jsonb,

  -- Why a send was skipped or cancelled.
  resolution_reason    text,

  created_at           timestamptz not null default now(),
  updated_at           timestamptz not null default now(),

  constraint scheduled_notifications_rule_fk
    foreign key (notification_rule_id, organization_id)
    references notification_rules (id, organization_id) on delete cascade,
  constraint scheduled_notifications_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint scheduled_notifications_occurrence_fk
    foreign key (rent_occurrence_id, organization_id)
    references rent_occurrences (id, organization_id) on delete cascade,
  constraint scheduled_notifications_email_account_fk
    foreign key (email_account_id, organization_id)
    references email_accounts (id, organization_id) on delete restrict,
  constraint scheduled_notifications_template_fk
    foreign key (template_id, organization_id)
    references email_templates (id, organization_id) on delete restrict,

  constraint scheduled_notifications_idempotency_key_shape_check
    check (char_length(idempotency_key) between 16 and 200),
  constraint scheduled_notifications_attempt_count_check
    check (attempt_count >= 0 and attempt_count <= max_attempts + 1),
  constraint scheduled_notifications_max_attempts_check
    check (max_attempts between 1 and 20),
  constraint scheduled_notifications_recipients_is_array_check
    check (jsonb_typeof(recipients_snapshot) = 'array'),
  constraint scheduled_notifications_sent_metadata_check
    check ((status = 'sent') = (sent_at is not null)),
  -- A claim is complete or absent, never half-set.
  constraint scheduled_notifications_lock_shape_check
    check (
      (locked_at is null and locked_by is null and lock_expires_at is null)
      or (locked_at is not null and locked_by is not null and lock_expires_at is not null)
    ),
  constraint scheduled_notifications_claimed_has_lock_check
    check (status <> 'claimed' or locked_at is not null),
  constraint scheduled_notifications_error_message_length_check
    check (last_error_message is null or char_length(last_error_message) <= 2000),

  -- THE duplicate-send guard. Scoped to the organization so two organizations
  -- can never collide, and unique within it so a replayed generation is a
  -- no-op rather than a second email.
  constraint scheduled_notifications_idempotency_key_unique
    unique (organization_id, idempotency_key),
  constraint scheduled_notifications_id_organization_key unique (id, organization_id)
);

comment on table scheduled_notifications is
  'Durable send queue. PostgreSQL is the source of truth; no in-memory timer is authoritative.';
comment on column scheduled_notifications.idempotency_key is
  'Deterministic key over (rule, event, occurrence). Its UNIQUE constraint prevents duplicate sends.';
comment on column scheduled_notifications.scheduled_at_utc is
  'The send instant in UTC. local_timezone and local_send_time record the intent behind it.';
comment on column scheduled_notifications.dst_adjusted is
  'True when the intended local time was ambiguous or nonexistent at a DST transition.';
comment on column scheduled_notifications.recipients_snapshot is
  'Recipients resolved at generation time. Re-validated against suppressions before sending.';

-- The dispatcher hot path: due, unclaimed work in send order.
create index scheduled_notifications_due_idx
  on scheduled_notifications (scheduled_at_utc)
  where status = 'scheduled';
-- Retry queue.
create index scheduled_notifications_retry_idx
  on scheduled_notifications (next_attempt_at)
  where status = 'failed';
-- Reclaiming jobs abandoned by a restarted or killed worker.
create index scheduled_notifications_expired_locks_idx
  on scheduled_notifications (lock_expires_at)
  where status = 'claimed';

create index scheduled_notifications_lease_idx
  on scheduled_notifications (lease_id, scheduled_at_utc desc);
create index scheduled_notifications_organization_calendar_idx
  on scheduled_notifications (organization_id, event_local_date);
create index scheduled_notifications_rule_idx
  on scheduled_notifications (notification_rule_id);
-- Per-rule, per-day volume checks.
create index scheduled_notifications_volume_idx
  on scheduled_notifications (organization_id, notification_rule_id, sent_at)
  where status = 'sent';

create trigger scheduled_notifications_touch_updated_at
  before update on scheduled_notifications
  for each row execute function app.touch_updated_at();

-- A sent notification is history. Nothing may move it back into a sendable
-- state, which is a second, independent barrier against double sending.
create or replace function app.protect_sent_notification()
returns trigger
language plpgsql
set search_path = pg_catalog, public
as $fn$
begin
  if old.status = 'sent' and new.status <> 'sent' then
    raise exception
      'Scheduled notification % has already been sent and cannot return to status %L',
      old.id, new.status
      using errcode = 'integrity_constraint_violation';
  end if;
  return new;
end;
$fn$;

comment on function app.protect_sent_notification() is
  'Prevents a sent notification from being returned to a sendable status.';

create trigger scheduled_notifications_protect_sent
  before update on scheduled_notifications
  for each row execute function app.protect_sent_notification();

-- -----------------------------------------------------------------------------
-- email_logs
-- Append-only delivery record. One row per send attempt.
--
-- A row here means the provider ACCEPTED the request. It is not evidence the
-- message reached an inbox (section 6.J) and the column names say so.
-- -----------------------------------------------------------------------------
create table email_logs (
  id                          uuid primary key default gen_random_uuid(),
  organization_id             uuid not null references organizations (id) on delete cascade,
  scheduled_notification_id   uuid,
  lease_id                    uuid,
  property_id                 uuid,
  notification_rule_id        uuid,
  email_account_id            uuid,
  template_id                 uuid,

  -- Provider identifier, when one was returned.
  provider_message_id         text,
  sender_email                citext not null,
  -- [{"email":"...","kind":"to"}, ...] -- addresses only, never message bodies.
  recipients_json             jsonb not null,
  subject                     text not null,

  status                      app.email_send_status not null,
  error_category              app.email_error_category not null default 'none',
  -- Operator-safe summary. Never a raw provider payload.
  error_message               text,

  attempt_number              integer not null default 1,
  -- Set only when the provider accepted the request.
  provider_accepted_at        timestamptz,

  created_at                  timestamptz not null default now(),

  constraint email_logs_notification_fk
    foreign key (scheduled_notification_id, organization_id)
    references scheduled_notifications (id, organization_id) on delete set null,
  constraint email_logs_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete set null,
  constraint email_logs_property_fk
    foreign key (property_id, organization_id)
    references properties (id, organization_id) on delete set null,

  constraint email_logs_sender_shape_check
    check (sender_email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint email_logs_recipients_is_array_check
    check (jsonb_typeof(recipients_json) = 'array'),
  constraint email_logs_subject_length_check
    check (char_length(subject) between 1 and 500),
  constraint email_logs_attempt_number_check
    check (attempt_number between 1 and 100),
  constraint email_logs_error_message_length_check
    check (error_message is null or char_length(error_message) <= 2000),
  -- Accepted and failed are mutually exclusive facts.
  constraint email_logs_accepted_metadata_check
    check ((status = 'accepted') = (provider_accepted_at is not null)),
  constraint email_logs_failed_has_category_check
    check (status <> 'failed' or error_category <> 'none')
);

comment on table email_logs is
  'Append-only send record. An accepted row means the provider took the request, not that it was delivered.';
comment on column email_logs.provider_accepted_at is
  'When the provider accepted the send request. Deliberately not named delivered_at.';
comment on column email_logs.recipients_json is
  'Recipient addresses and kinds only. Message bodies are never stored here.';

create index email_logs_organization_idx on email_logs (organization_id, created_at desc);
create index email_logs_lease_idx on email_logs (lease_id, created_at desc);
create index email_logs_notification_idx on email_logs (scheduled_notification_id);
create index email_logs_failures_idx on email_logs (organization_id, created_at desc)
  where status = 'failed';
create index email_logs_provider_message_idx on email_logs (provider_message_id)
  where provider_message_id is not null;

create trigger email_logs_append_only
  before update or delete on email_logs
  for each row execute function app.deny_mutation();
