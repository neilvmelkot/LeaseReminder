-- =============================================================================
-- 0004_rent_schedules.sql
-- Rent recurrence rules, user-defined business-day calendars, exceptions, and
-- the materialised rent occurrences the notification engine hangs off.
--
-- The five dates below are kept strictly separate (section 6.F). Collapsing
-- any two of them is the single most likely way to send a tenant a wrong or
-- legally misleading reminder:
--
--     rent due date          when payment is due
--     grace period end       last day payment is accepted without penalty
--     late fee trigger date  when a late fee actually applies
--     lease expiration       when the term ends
--     renewal notice deadline when notice must be given
--
-- Weekend and holiday shifting is never assumed. It is opt-in per schedule,
-- and holidays come from a calendar the user defines.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- holiday_calendars / holiday_dates
-- The application ships no holiday data. A user who wants business-day
-- shifting defines which dates count as holidays (section 6.F).
-- -----------------------------------------------------------------------------
create table holiday_calendars (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references organizations (id) on delete cascade,
  name            text not null,
  -- Which weekdays are non-working, as ISO numbers (1 = Monday .. 7 = Sunday).
  -- Default is Saturday and Sunday, but it is explicit and editable.
  weekend_days    smallint[] not null default array[6, 7]::smallint[],
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint holiday_calendars_name_length_check
    check (char_length(name) between 1 and 120),
  constraint holiday_calendars_weekend_days_check
    check (
      weekend_days <@ array[1, 2, 3, 4, 5, 6, 7]::smallint[]
      and array_length(weekend_days, 1) between 0 and 6
    ),
  constraint holiday_calendars_organization_name_key
    unique (organization_id, name),
  constraint holiday_calendars_id_organization_key unique (id, organization_id)
);

comment on table holiday_calendars is
  'User-defined working-day calendar. The application ships no holiday data of its own.';
comment on column holiday_calendars.weekend_days is
  'ISO weekday numbers treated as non-working. Explicit rather than assumed.';

create trigger holiday_calendars_touch_updated_at
  before update on holiday_calendars
  for each row execute function app.touch_updated_at();

create table holiday_dates (
  id                  uuid primary key default gen_random_uuid(),
  organization_id     uuid not null,
  holiday_calendar_id uuid not null,
  holiday_date        date not null,
  name                text,

  constraint holiday_dates_calendar_fk
    foreign key (holiday_calendar_id, organization_id)
    references holiday_calendars (id, organization_id) on delete cascade,
  constraint holiday_dates_name_length_check
    check (name is null or char_length(name) between 1 and 120),
  constraint holiday_dates_calendar_date_key
    unique (holiday_calendar_id, holiday_date)
);

comment on table holiday_dates is 'Individual non-working dates within a holiday calendar.';

create index holiday_dates_calendar_idx on holiday_dates (holiday_calendar_id, holiday_date);
create index holiday_dates_organization_idx on holiday_dates (organization_id);

-- -----------------------------------------------------------------------------
-- rent_schedules
-- The recurrence definition for a lease. Deliberately the single home for the
-- due rule: it is not duplicated onto leases, so the two can never disagree.
-- -----------------------------------------------------------------------------
create table rent_schedules (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  lease_id        uuid not null,

  frequency       app.rent_frequency not null,
  due_day_rule    app.due_day_rule not null,

  -- Required only for due_day_rule = 'specific_day'. 1..31.
  due_day_of_month smallint,
  -- Required only for frequency = 'custom' or due_day_rule = 'custom_sequence'.
  custom_dates    date[],
  -- Anchor for weekly / biweekly / every_30_days. The first occurrence.
  anchor_date     date,

  -- Window the schedule covers. end_date NULL means "for the lease term".
  start_date      date not null,
  end_date        date,

  -- What to do when the due day does not exist in a short month, e.g. the
  -- 31st in February (section 6.F).
  short_month_fallback app.short_month_fallback not null default 'last_day_of_month',

  -- Weekend/holiday handling. 'none' by default: nothing shifts unless asked.
  business_day_shift   app.business_day_shift not null default 'none',
  holiday_calendar_id  uuid,

  -- The zone the local due dates are interpreted in. Defaults to the
  -- property zone at creation time, but stored here so a later property edit
  -- cannot silently move every past and future due date.
  timezone        text not null,

  -- Overrides the lease rent when a schedule charges a different amount
  -- (for example a prorated first month). NULL means "use the lease rent".
  amount          numeric(14, 2),
  currency        char(3),

  active          boolean not null default true,
  -- Bumped on every regeneration so occurrences can be matched to the rule
  -- version that produced them.
  generation_version integer not null default 1,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint rent_schedules_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint rent_schedules_holiday_calendar_fk
    foreign key (holiday_calendar_id, organization_id)
    references holiday_calendars (id, organization_id) on delete restrict,

  constraint rent_schedules_term_order_check
    check (end_date is null or end_date >= start_date),
  constraint rent_schedules_amount_check
    check (amount is null or (amount >= 0 and amount <= 99999999.99)),
  constraint rent_schedules_currency_check
    check (currency is null or currency ~ '^[A-Z]{3}$'),

  -- due_day_of_month is required for 'specific_day' and meaningless otherwise.
  constraint rent_schedules_due_day_required_check
    check (
      (due_day_rule = 'specific_day' and due_day_of_month between 1 and 31)
      or (due_day_rule <> 'specific_day' and due_day_of_month is null)
    ),

  -- A custom sequence needs dates; the other rules must not carry them.
  constraint rent_schedules_custom_dates_required_check
    check (
      (
        (frequency = 'custom' or due_day_rule = 'custom_sequence')
        and custom_dates is not null
        and array_length(custom_dates, 1) between 1 and 600
      )
      or (
        frequency <> 'custom' and due_day_rule <> 'custom_sequence'
        and custom_dates is null
      )
    ),

  -- Interval-based frequencies need an anchor to count from.
  constraint rent_schedules_anchor_required_check
    check (
      frequency not in ('weekly', 'biweekly', 'every_30_days')
      or anchor_date is not null
    ),

  -- A one-time payment has exactly one date and no recurrence semantics.
  constraint rent_schedules_one_time_shape_check
    check (
      frequency <> 'one_time'
      or (due_day_rule = 'custom_sequence' and array_length(custom_dates, 1) = 1)
    ),

  -- Monthly day rules only mean something for monthly recurrence.
  constraint rent_schedules_monthly_rule_pairing_check
    check (
      due_day_rule not in ('first_of_month', 'last_of_month', 'specific_day', 'first_business_day')
      or frequency = 'monthly'
    ),

  -- Shifting by business day is incoherent without a calendar defining them.
  constraint rent_schedules_shift_requires_calendar_check
    check (business_day_shift = 'none' or holiday_calendar_id is not null),
  -- Likewise for the first-business-day rule.
  constraint rent_schedules_business_day_rule_requires_calendar_check
    check (due_day_rule <> 'first_business_day' or holiday_calendar_id is not null),

  constraint rent_schedules_id_organization_key unique (id, organization_id)
);

comment on table rent_schedules is
  'Rent recurrence for a lease. The only home for the due rule; never duplicated onto leases.';
comment on column rent_schedules.timezone is
  'Zone the local due dates are interpreted in. Snapshotted so editing the property cannot move past due dates.';
comment on column rent_schedules.short_month_fallback is
  'Behaviour when the configured day does not exist in a month, e.g. the 31st in February.';
comment on column rent_schedules.business_day_shift is
  'Opt-in weekend/holiday shifting. Defaults to none: nothing shifts unless the user asks.';

create index rent_schedules_lease_idx on rent_schedules (lease_id) where active;
create index rent_schedules_organization_idx on rent_schedules (organization_id);

-- At most one active schedule per lease in the first release. Lifted later if
-- split rent (for example parking billed separately) is supported.
create unique index rent_schedules_one_active_per_lease_idx
  on rent_schedules (lease_id)
  where active;

create trigger rent_schedules_touch_updated_at
  before update on rent_schedules
  for each row execute function app.touch_updated_at();

create trigger rent_schedules_validate_timezone
  before insert or update of timezone on rent_schedules
  for each row execute function app.validate_timezone_column('timezone');

-- -----------------------------------------------------------------------------
-- rent_schedule_exceptions
-- Per-month overrides: skip a month, move a date, or change one amount
-- (section 6.F, "Exceptions for a particular month").
-- -----------------------------------------------------------------------------
create table rent_schedule_exceptions (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null,
  rent_schedule_id uuid not null,

  -- The date the rule would otherwise have produced.
  original_due_date date not null,
  action           text not null,
  -- Required for 'move'.
  replacement_date date,
  -- Required for 'override_amount'.
  amount           numeric(14, 2),
  note             text,

  created_at       timestamptz not null default now(),
  created_by_user_id uuid references users (id) on delete set null,

  constraint rent_schedule_exceptions_schedule_fk
    foreign key (rent_schedule_id, organization_id)
    references rent_schedules (id, organization_id) on delete cascade,

  constraint rent_schedule_exceptions_action_check
    check (action in ('skip', 'move', 'override_amount')),
  constraint rent_schedule_exceptions_move_shape_check
    check (action <> 'move' or replacement_date is not null),
  constraint rent_schedule_exceptions_amount_shape_check
    check (
      (action = 'override_amount' and amount is not null and amount >= 0)
      or (action <> 'override_amount' and amount is null)
    ),
  constraint rent_schedule_exceptions_note_length_check
    check (note is null or char_length(note) <= 1000),

  constraint rent_schedule_exceptions_schedule_date_key
    unique (rent_schedule_id, original_due_date)
);

comment on table rent_schedule_exceptions is
  'Single-occurrence overrides: skip a month, move a due date, or change one amount.';

create index rent_schedule_exceptions_organization_idx
  on rent_schedule_exceptions (organization_id);

-- -----------------------------------------------------------------------------
-- rent_escalations
-- Scheduled rent increases. Kept as their own rows rather than edited into the
-- lease, so history and the reason for each increase survive.
-- -----------------------------------------------------------------------------
create table rent_escalations (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  lease_id        uuid not null,

  effective_date  date not null,
  -- Exactly one of the two is set.
  new_amount      numeric(14, 2),
  increase_percentage numeric(6, 3),

  note            text,
  -- Which extracted field proposed this, when it came from a document.
  source_field_id uuid references lease_extracted_fields (id) on delete set null,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint rent_escalations_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,

  constraint rent_escalations_exactly_one_form_check
    check ((new_amount is null) <> (increase_percentage is null)),
  constraint rent_escalations_new_amount_check
    check (new_amount is null or (new_amount >= 0 and new_amount <= 99999999.99)),
  constraint rent_escalations_percentage_check
    check (increase_percentage is null or increase_percentage between -100 and 1000),
  constraint rent_escalations_note_length_check
    check (note is null or char_length(note) <= 2000),

  constraint rent_escalations_lease_date_key unique (lease_id, effective_date)
);

comment on table rent_escalations is
  'Scheduled rent increases, as either an absolute new amount or a percentage.';

create index rent_escalations_lease_idx on rent_escalations (lease_id, effective_date);
create index rent_escalations_organization_idx on rent_escalations (organization_id);

create trigger rent_escalations_touch_updated_at
  before update on rent_escalations
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- additional_charges
-- Recurring and one-time charges beyond base rent: parking, pets, utilities.
-- -----------------------------------------------------------------------------
create table additional_charges (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  lease_id        uuid not null,

  name            text not null,
  amount          numeric(14, 2) not null,
  currency        char(3) not null default 'USD',
  -- 'monthly' charges follow the rent schedule; 'one_time' uses charge_date.
  frequency       app.rent_frequency not null default 'monthly',
  charge_date     date,
  refundable      boolean not null default false,
  note            text,

  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),

  constraint additional_charges_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,

  constraint additional_charges_name_length_check
    check (char_length(name) between 1 and 120),
  constraint additional_charges_amount_check
    check (amount >= 0 and amount <= 99999999.99),
  constraint additional_charges_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  constraint additional_charges_one_time_date_check
    check (frequency <> 'one_time' or charge_date is not null),
  constraint additional_charges_note_length_check
    check (note is null or char_length(note) <= 2000)
);

comment on table additional_charges is
  'Recurring or one-time charges beyond base rent, such as parking or pet fees.';

create index additional_charges_lease_idx on additional_charges (lease_id);
create index additional_charges_organization_idx on additional_charges (organization_id);

create trigger additional_charges_touch_updated_at
  before update on additional_charges
  for each row execute function app.touch_updated_at();

-- -----------------------------------------------------------------------------
-- rent_occurrences
-- The expanded schedule: one row per rent payment the lease calls for.
--
-- Materialised rather than computed on demand because it is the stable anchor
-- for notification scheduling, the calendar views, and the schedule preview a
-- user approves. Regenerating replaces future rows and leaves past ones alone.
--
-- All four dates here are LOCAL dates in the schedule time zone. The UTC send
-- instant is computed per notification rule, never stored on the occurrence --
-- one occurrence can feed reminders at several different local times.
-- -----------------------------------------------------------------------------
create table rent_occurrences (
  id               uuid primary key default gen_random_uuid(),
  organization_id  uuid not null,
  lease_id         uuid not null,
  rent_schedule_id uuid not null,

  -- 1-based position in the generated sequence.
  sequence_number  integer not null,

  -- The date the rule produced, before any shifting or exception. Kept so the
  -- UI can explain "the 31st fell on a Sunday, so this moved".
  nominal_due_date date not null,
  -- The date actually in force.
  due_date         date not null,
  -- Last day payment is accepted without penalty. NULL when no grace period.
  grace_period_end_date date,
  -- When a late fee actually applies. Independent of the grace period.
  late_fee_date    date,

  amount           numeric(14, 2) not null,
  currency         char(3) not null,

  -- Why due_date differs from nominal_due_date, if it does.
  adjustment_reason text,
  -- Matches rent_schedules.generation_version at the time of generation.
  generation_version integer not null,

  created_at       timestamptz not null default now(),

  constraint rent_occurrences_lease_fk
    foreign key (lease_id, organization_id)
    references leases (id, organization_id) on delete cascade,
  constraint rent_occurrences_schedule_fk
    foreign key (rent_schedule_id, organization_id)
    references rent_schedules (id, organization_id) on delete cascade,

  constraint rent_occurrences_sequence_check
    check (sequence_number >= 1),
  constraint rent_occurrences_amount_check
    check (amount >= 0 and amount <= 99999999.99),
  constraint rent_occurrences_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  -- A grace period cannot end before the payment is due.
  constraint rent_occurrences_grace_after_due_check
    check (grace_period_end_date is null or grace_period_end_date >= due_date),
  -- A late fee cannot apply before the payment is due.
  constraint rent_occurrences_late_fee_after_due_check
    check (late_fee_date is null or late_fee_date >= due_date),
  constraint rent_occurrences_adjustment_reason_check
    check (
      adjustment_reason is null
      or adjustment_reason in (
        'short_month_fallback', 'weekend_shift', 'holiday_shift',
        'user_exception', 'prorated'
      )
    ),
  -- A differing due date must say why.
  constraint rent_occurrences_adjustment_explained_check
    check (due_date = nominal_due_date or adjustment_reason is not null),

  constraint rent_occurrences_schedule_sequence_key
    unique (rent_schedule_id, sequence_number),
  constraint rent_occurrences_id_organization_key unique (id, organization_id)
);

comment on table rent_occurrences is
  'Expanded rent schedule, one row per payment. The anchor for notification scheduling and calendars.';
comment on column rent_occurrences.nominal_due_date is
  'Date the recurrence rule produced before shifting or exceptions. Kept so the UI can explain adjustments.';
comment on column rent_occurrences.due_date is
  'The effective local due date. Distinct from grace_period_end_date and late_fee_date.';
comment on column rent_occurrences.late_fee_date is
  'When a late fee applies. Deliberately separate from the grace period end.';

create index rent_occurrences_lease_due_idx on rent_occurrences (lease_id, due_date);
create index rent_occurrences_organization_due_idx
  on rent_occurrences (organization_id, due_date);
create index rent_occurrences_schedule_idx on rent_occurrences (rent_schedule_id, due_date);
