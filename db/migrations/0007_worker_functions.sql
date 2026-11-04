-- =============================================================================
-- 0007_worker_functions.sql
-- The complete surface the background worker is allowed to touch.
--
-- The worker role is granted EXECUTE on these functions and (almost) nothing
-- else: no direct UPDATE on the queues, no SELECT on leases, tenants, or
-- templates. A compromised worker therefore cannot read the portfolio or
-- rewrite history. It can only advance work it already holds a claim on.
-- This is the concrete form of "keep worker permissions narrower than API
-- permissions" (section 4.H).
--
-- Every function re-checks `locked_by = p_worker_id`. A worker whose lock has
-- expired and been reclaimed elsewhere cannot overwrite the new holder's
-- result, which closes the last window for a duplicate send.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Job completion
-- -----------------------------------------------------------------------------

create or replace function app.complete_job(
  p_job_id    uuid,
  p_worker_id text
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_updated integer;
begin
  update jobs
  set status             = 'succeeded',
      finished_at        = now(),
      locked_at          = null,
      locked_by          = null,
      lock_expires_at    = null,
      last_error_message = null,
      updated_at         = now()
  where id = p_job_id
    and status = 'claimed'
    and locked_by = p_worker_id;

  get diagnostics v_updated = row_count;
  return v_updated = 1;
end;
$fn$;

comment on function app.complete_job(uuid, text) is
  'Marks a claimed job succeeded. Only the holder of the claim may complete it.';

create or replace function app.fail_job(
  p_job_id              uuid,
  p_worker_id           text,
  p_error_message       text,
  -- NULL asks for the default exponential backoff with jitter.
  p_retry_after_seconds integer default null,
  -- True for errors that can never succeed on retry: an unsupported format,
  -- a failed signature check, a malformed document. These are dead-lettered
  -- immediately rather than retried five times (section 4.H).
  p_permanent           boolean default false
)
returns app.job_status
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_job    jobs;
  v_status app.job_status;
  v_delay  integer;
begin
  select * into v_job
  from jobs
  where id = p_job_id
    and status = 'claimed'
    and locked_by = p_worker_id
  for update;

  if not found then
    return null;
  end if;

  if p_permanent or v_job.attempt_count >= v_job.max_attempts then
    v_status := 'dead_letter';
  else
    v_status := 'failed';
  end if;

  -- 30s, 60s, 120s, 240s ... capped at one hour, plus up to 25 percent random
  -- spread so that retries from a batch do not re-synchronise.
  v_delay := coalesce(
    p_retry_after_seconds,
    least(3600, (30 * power(2, greatest(v_job.attempt_count - 1, 0)))::integer)
  );
  v_delay := v_delay + floor(random() * (v_delay * 0.25))::integer;

  update jobs
  set status = case when v_status = 'dead_letter' then 'dead_letter'
                    else 'queued' end,
      -- A retryable failure goes back on the queue with a later run_after.
      -- Persisting the backoff is what makes it survive a worker restart.
      run_after = case when v_status = 'dead_letter' then run_after
                       else now() + make_interval(secs => v_delay) end,
      finished_at = case when v_status = 'dead_letter' then now() else null end,
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      last_error_message = left(coalesce(p_error_message, 'unspecified error'), 2000),
      updated_at = now()
  where id = p_job_id;

  return v_status;
end;
$fn$;

comment on function app.fail_job(uuid, text, text, integer, boolean) is
  'Records a job failure with exponential backoff and jitter, or dead-letters a permanent error.';

-- -----------------------------------------------------------------------------
-- app.get_notification_context
--
-- Everything needed to render and send one notification, in a single read, for
-- a notification the caller already holds a claim on. Template variable values
-- are assembled here, so the worker receives values rather than table access.
--
-- The variable set matches section 6.I exactly.
-- -----------------------------------------------------------------------------
create or replace function app.get_notification_context(
  p_notification_id uuid,
  p_worker_id       text
)
returns jsonb
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_result jsonb;
begin
  select jsonb_build_object(
    'notification_id',  n.id,
    'organization_id',  n.organization_id,
    'idempotency_key',  n.idempotency_key,
    'event_type',       n.event_type,
    'event_local_date', n.event_local_date,
    'local_timezone',   n.local_timezone,
    'attempt_count',    n.attempt_count,
    'max_attempts',     n.max_attempts,
    'send_separately',  r.send_separately,
    'recipients',       n.recipients_snapshot,

    'sender', jsonb_build_object(
      'email_account_id', a.id,
      'email_address',    a.email_address,
      'display_name',     a.display_name,
      'daily_send_limit', a.daily_send_limit
    ),

    'template', jsonb_build_object(
      'template_id', t.id,
      'subject',     t.subject,
      'html_body',   t.html_body,
      'text_body',   t.text_body,
      'version',     t.version
    ),

    'lease', jsonb_build_object(
      'lease_id',    l.id,
      'reference',   l.reference,
      'property_id', p.id
    ),

    'variables', jsonb_strip_nulls(jsonb_build_object(
      'tenant_name',      primary_tenant.name,
      'landlord_name',    coalesce(a.display_name, o.name),
      'property_name',    p.name,
      'property_address', nullif(concat_ws(', ',
                            nullif(p.address_line1, ''),
                            nullif(p.address_line2, ''),
                            nullif(p.locality, ''),
                            nullif(p.region, ''),
                            nullif(p.postal_code, '')), ''),
      'unit_number',      u.unit_number,
      'rent_amount',      to_char(coalesce(occ.amount, l.rent_amount), 'FM999999990.00'),
      'currency',         coalesce(occ.currency, l.currency),
      'due_date',         to_char(coalesce(occ.due_date, n.event_local_date), 'YYYY-MM-DD'),
      -- Computed against today in the notification own zone, not server local time.
      'days_until_due',   (coalesce(occ.due_date, n.event_local_date)
                             - (now() at time zone n.local_timezone)::date),
      'grace_period_end', to_char(occ.grace_period_end_date, 'YYYY-MM-DD'),
      'late_fee_date',    to_char(occ.late_fee_date, 'YYYY-MM-DD'),
      'lease_start_date', to_char(l.start_date, 'YYYY-MM-DD'),
      'lease_end_date',   to_char(l.end_date, 'YYYY-MM-DD'),
      'sender_name',      coalesce(a.display_name, o.name)
    ))
  )
  into v_result
  from scheduled_notifications n
  join organizations o    on o.id = n.organization_id
  join notification_rules r on r.id = n.notification_rule_id
                           and r.organization_id = n.organization_id
  join leases l           on l.id = n.lease_id
                         and l.organization_id = n.organization_id
  join properties p       on p.id = l.property_id
                         and p.organization_id = n.organization_id
  join email_accounts a   on a.id = n.email_account_id
                         and a.organization_id = n.organization_id
  join email_templates t  on t.id = n.template_id
                         and t.organization_id = n.organization_id
  left join units u       on u.id = l.unit_id
  left join rent_occurrences occ on occ.id = n.rent_occurrence_id
                               and occ.organization_id = n.organization_id
  left join lateral (
    select coalesce(te.preferred_name, te.legal_name) as name
    from lease_tenants lt
    join tenants te on te.id = lt.tenant_id
    where lt.lease_id = l.id
    order by (lt.role = 'primary') desc, te.legal_name
    limit 1
  ) as primary_tenant on true
  where n.id = p_notification_id
    and n.status = 'claimed'
    and n.locked_by = p_worker_id;

  -- NULL when the claim is not held. The worker treats that as "skip", never
  -- as "send anyway".
  return v_result;
end;
$fn$;

comment on function app.get_notification_context(uuid, text) is
  'Full render-and-send projection for a claimed notification, so the worker needs no table access.';

-- -----------------------------------------------------------------------------
-- app.filter_suppressed_recipients
-- Re-checks the suppression list immediately before sending, rather than
-- trusting the snapshot taken when the notification was generated (4.G).
-- -----------------------------------------------------------------------------
create or replace function app.filter_suppressed_recipients(
  p_organization_id uuid,
  p_recipients      jsonb
)
returns jsonb
language sql
stable
security definer
set search_path = pg_catalog, public
as $fn$
  select coalesce(jsonb_agg(r.value), '[]'::jsonb)
  from jsonb_array_elements(p_recipients) as r(value)
  where not exists (
    select 1
    from email_suppressions s
    where s.organization_id = p_organization_id
      and s.email = (r.value ->> 'email')::citext
      and (s.expires_at is null or s.expires_at > now())
  )
$fn$;

comment on function app.filter_suppressed_recipients(uuid, jsonb) is
  'Removes suppressed addresses from a recipient snapshot. Called at send time, not generation time.';

-- -----------------------------------------------------------------------------
-- app.record_notification_result
--
-- Writes the email log and advances the queue row in ONE transaction, after
-- the Gmail call has already returned. The provider call itself happens
-- outside every database lock (section 7).
-- -----------------------------------------------------------------------------
create or replace function app.record_notification_result(
  p_notification_id     uuid,
  p_worker_id           text,
  p_accepted            boolean,
  p_provider_message_id text default null,
  p_error_category      app.email_error_category default 'none',
  p_error_message       text default null,
  p_permanent           boolean default false
)
returns app.scheduled_notification_status
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_notification scheduled_notifications;
  v_status       app.scheduled_notification_status;
  v_property_id  uuid;
  v_sender       citext;
  v_subject      text;
begin
  select * into v_notification
  from scheduled_notifications
  where id = p_notification_id
    and status = 'claimed'
    and locked_by = p_worker_id
  for update;

  if not found then
    -- The claim expired and another worker owns this row now. Writing a
    -- result here could produce a second log entry for one send, so do
    -- nothing and let the current holder record the outcome.
    return null;
  end if;

  select l.property_id into v_property_id
  from leases l where l.id = v_notification.lease_id;

  select a.email_address into v_sender
  from email_accounts a where a.id = v_notification.email_account_id;

  select t.subject into v_subject
  from email_templates t where t.id = v_notification.template_id;

  if p_accepted then
    v_status := 'sent';
  elsif p_permanent or v_notification.attempt_count >= v_notification.max_attempts then
    v_status := 'dead_letter';
  else
    v_status := 'failed';
  end if;

  update scheduled_notifications
  set status  = v_status,
      sent_at = case when v_status = 'sent' then now() else sent_at end,
      next_attempt_at = case
        when v_status = 'failed' then
          -- 60s, 120s, 240s ... capped at six hours, plus jitter.
          now() + make_interval(secs => (
            least(21600, (60 * power(2, greatest(v_notification.attempt_count - 1, 0)))::integer)
            + floor(random() * 30)::integer
          ))
        else null
      end,
      last_error_category = p_error_category,
      last_error_message  = left(p_error_message, 2000),
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      updated_at = now()
  where id = p_notification_id;

  insert into email_logs (
    organization_id, scheduled_notification_id, lease_id, property_id,
    notification_rule_id, email_account_id, template_id,
    provider_message_id, sender_email, recipients_json, subject,
    status, error_category, error_message, attempt_number, provider_accepted_at
  )
  values (
    v_notification.organization_id,
    v_notification.id,
    v_notification.lease_id,
    v_property_id,
    v_notification.notification_rule_id,
    v_notification.email_account_id,
    v_notification.template_id,
    p_provider_message_id,
    coalesce(v_sender, 'unknown@invalid'),
    v_notification.recipients_snapshot,
    coalesce(nullif(v_subject, ''), '(no subject)'),
    case when p_accepted then 'accepted'::app.email_send_status
         else 'failed'::app.email_send_status end,
    case when p_accepted then 'none'::app.email_error_category
         else coalesce(nullif(p_error_category, 'none'),
                       'unknown'::app.email_error_category) end,
    left(p_error_message, 2000),
    v_notification.attempt_count,
    case when p_accepted then now() else null end
  );

  if p_accepted then
    -- A successful send is proof the stored credential still works.
    update email_accounts
    set last_successful_send_at = now(), updated_at = now()
    where id = v_notification.email_account_id;

  elsif p_error_category = 'authorization' then
    -- The credential has stopped working. Surface it for reauthorization
    -- instead of retrying blindly, and leave every lease status untouched:
    -- a Gmail failure is not a lease event (section 13.E).
    update email_accounts
    set connection_status = 'reauthorization_required',
        status_detail = 'Gmail rejected the stored credential during a scheduled send.',
        last_error_at = now(),
        updated_at = now()
    where id = v_notification.email_account_id
      and connection_status = 'connected';
  end if;

  return v_status;
end;
$fn$;

comment on function app.record_notification_result(uuid, text, boolean, text, app.email_error_category, text, boolean) is
  'Writes the email log and advances the queue row in one transaction after the provider call returns.';

-- -----------------------------------------------------------------------------
-- app.pause_rule_on_repeated_failure
-- "Pause an automation if it produces repeated failures or unusual volume"
-- (section 4.G). Called by the worker after recording a failure.
-- -----------------------------------------------------------------------------
create or replace function app.pause_rule_on_repeated_failure(
  p_notification_rule_id uuid,
  p_failure_threshold    integer default 5,
  p_window_minutes       integer default 60
)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_failures integer;
begin
  select count(*) into v_failures
  from email_logs
  where notification_rule_id = p_notification_rule_id
    and status = 'failed'
    and created_at > now() - make_interval(mins => p_window_minutes);

  if v_failures < p_failure_threshold then
    return false;
  end if;

  update notification_rules
  set paused_at = now(),
      paused_reason = format(
        'Paused automatically after %s failed sends in %s minutes.',
        v_failures, p_window_minutes
      ),
      updated_at = now()
  where id = p_notification_rule_id
    and paused_at is null;

  -- Pending sends for a paused rule are cancelled rather than left to fire
  -- once the rule is re-enabled, which would deliver a burst of stale mail.
  update scheduled_notifications
  set status = 'cancelled',
      resolution_reason = 'rule_auto_paused',
      locked_at = null,
      locked_by = null,
      lock_expires_at = null,
      updated_at = now()
  where notification_rule_id = p_notification_rule_id
    and status in ('scheduled', 'failed');

  return true;
end;
$fn$;

comment on function app.pause_rule_on_repeated_failure(uuid, integer, integer) is
  'Auto-pauses a rule after repeated send failures and cancels its pending sends.';
