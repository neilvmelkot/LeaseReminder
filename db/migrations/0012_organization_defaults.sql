-- =============================================================================
-- 0012_organization_defaults.sql
-- Seeds the defaults a new organization needs: recipient groups, a holiday
-- calendar, and the published email templates from section 6.I.
--
-- Written as a function rather than static INSERTs because defaults belong to
-- an organization, and organizations are created at runtime. The API calls
-- this inside the same transaction that creates the organization and its
-- owner membership.
--
-- Templates ship as PUBLISHED version 1. Editing one creates version 2; the
-- immutability trigger in 0005 makes sure an email log always points at the
-- exact text that was sent.
--
-- The bodies contain {{variable}} placeholders only. Substitution happens in
-- application code, which HTML-escapes every value before it reaches the HTML
-- body (section 6.I, 4.D). No template interpolates raw markup.
-- =============================================================================

-- Named for the half of the defaults it owns. 0018 adds the maintenance board
-- and composes both behind app.seed_organization_defaults, so the rent-side
-- defaults and the task-side defaults stay separately reviewable.
create or replace function app.seed_rent_defaults(p_organization_id uuid)
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_style_wrap  text := 'margin:0;padding:24px;background:#F7F8FA;'
                     || 'font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;';
  v_style_card  text := 'max-width:560px;margin:0 auto;background:#FFFFFF;'
                     || 'border:1px solid #E6E9EF;border-radius:8px;padding:28px;';
  v_style_h1    text := 'margin:0 0 16px;font-size:18px;line-height:1.4;'
                     || 'color:#172033;font-weight:600;';
  v_style_p     text := 'margin:0 0 14px;font-size:15px;line-height:1.6;color:#172033;';
  v_style_meta  text := 'margin:20px 0 0;padding-top:16px;border-top:1px solid #E6E9EF;'
                     || 'font-size:13px;line-height:1.6;color:#667085;';
  v_style_amount text := 'margin:0 0 14px;font-size:24px;line-height:1.3;'
                     || 'color:#172033;font-weight:600;';
begin
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Not permitted to seed defaults for this organization'
      using errcode = 'insufficient_privilege';
  end if;

  -- ---------------------------------------------------------------------------
  -- Recipient groups (section 6.H)
  -- ---------------------------------------------------------------------------
  insert into recipient_groups (organization_id, name, kind, description)
  values
    (p_organization_id, 'Landlord', 'landlord',
     'The property owner. Default recipient for rent and expiry reminders.'),
    (p_organization_id, 'Property manager', 'property_manager',
     'Day-to-day manager for the property.'),
    (p_organization_id, 'Tenant', 'tenant',
     'Tenants named on the lease. Honours each tenant consent setting.'),
    (p_organization_id, 'Accounting', 'accounting',
     'Bookkeeping. Typically added to overdue-rent rules.'),
    (p_organization_id, 'Maintenance', 'maintenance',
     'Maintenance contacts for property events.')
  on conflict (organization_id, name) do nothing;

  -- ---------------------------------------------------------------------------
  -- A starting working-day calendar.
  --
  -- It deliberately contains NO holiday dates. The application ships no
  -- holiday data, and business-day shifting stays off until a user both
  -- enables it and fills this in (section 6.F).
  -- ---------------------------------------------------------------------------
  insert into holiday_calendars (organization_id, name, weekend_days)
  values (p_organization_id, 'Default (Sat/Sun weekend)', array[6, 7]::smallint[])
  on conflict (organization_id, name) do nothing;

  -- ---------------------------------------------------------------------------
  -- Email templates
  -- ---------------------------------------------------------------------------

  -- 1. Upcoming rent -----------------------------------------------------------
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'upcoming_rent', 'Upcoming rent', 'rent_due', 1, 'published',
    'Rent for {{property_name}}{{unit_number}} is due on {{due_date}}',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Rent due in {{days_until_due}} days</h1>'
      || '<p style="' || v_style_amount || '">{{currency}} {{rent_amount}}</p>'
      || '<p style="' || v_style_p || '">Hello {{tenant_name}},</p>'
      || '<p style="' || v_style_p || '">This is a reminder that rent for '
      || '<strong>{{property_name}}{{unit_number}}</strong> is due on '
      || '<strong>{{due_date}}</strong>.</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Rent due in {{days_until_due}} days\n\n'
      || E'Amount: {{currency}} {{rent_amount}}\n'
      || E'Due date: {{due_date}}\n'
      || E'Property: {{property_name}}{{unit_number}}\n'
      || E'{{property_address}}\n\n'
      || E'Hello {{tenant_name}},\n\n'
      || E'This is a reminder that rent is due on {{due_date}}.\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['tenant_name', 'property_name', 'unit_number', 'property_address',
          'rent_amount', 'currency', 'due_date', 'days_until_due',
          'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;

  -- 2. Rent due today ----------------------------------------------------------
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'rent_due_today', 'Rent due today', 'rent_due', 1, 'published',
    'Rent for {{property_name}}{{unit_number}} is due today',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Rent is due today</h1>'
      || '<p style="' || v_style_amount || '">{{currency}} {{rent_amount}}</p>'
      || '<p style="' || v_style_p || '">Hello {{tenant_name}},</p>'
      || '<p style="' || v_style_p || '">Rent for '
      || '<strong>{{property_name}}{{unit_number}}</strong> is due today, '
      || '<strong>{{due_date}}</strong>.</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Rent is due today\n\n'
      || E'Amount: {{currency}} {{rent_amount}}\n'
      || E'Due date: {{due_date}}\n'
      || E'Property: {{property_name}}{{unit_number}}\n'
      || E'{{property_address}}\n\n'
      || E'Hello {{tenant_name}},\n\n'
      || E'Rent is due today.\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['tenant_name', 'property_name', 'unit_number', 'property_address',
          'rent_amount', 'currency', 'due_date', 'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;

  -- 3. Overdue rent ------------------------------------------------------------
  -- States the facts recorded on the lease. It does not assert that a fee has
  -- been charged, because the application does not know that it has.
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'overdue_rent', 'Overdue rent', 'late_fee_trigger', 1, 'published',
    'Rent for {{property_name}}{{unit_number}} was due on {{due_date}}',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Rent has not been recorded as paid</h1>'
      || '<p style="' || v_style_amount || '">{{currency}} {{rent_amount}}</p>'
      || '<p style="' || v_style_p || '">Hello {{tenant_name}},</p>'
      || '<p style="' || v_style_p || '">Our records show that rent for '
      || '<strong>{{property_name}}{{unit_number}}</strong>, due on '
      || '<strong>{{due_date}}</strong>, has not yet been recorded as received. '
      || 'If you have already paid, please disregard this message.</p>'
      || '<p style="' || v_style_p || '">Grace period ends: {{grace_period_end}}</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Rent has not been recorded as paid\n\n'
      || E'Amount: {{currency}} {{rent_amount}}\n'
      || E'Due date: {{due_date}}\n'
      || E'Grace period ends: {{grace_period_end}}\n'
      || E'Property: {{property_name}}{{unit_number}}\n\n'
      || E'Hello {{tenant_name}},\n\n'
      || E'Our records show rent due on {{due_date}} has not yet been recorded as\n'
      || E'received. If you have already paid, please disregard this message.\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['tenant_name', 'property_name', 'unit_number', 'property_address',
          'rent_amount', 'currency', 'due_date', 'grace_period_end',
          'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;

  -- 4. Lease expiration --------------------------------------------------------
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'lease_expiration', 'Lease expiration',
    'lease_expiration', 1, 'published',
    'Lease for {{property_name}}{{unit_number}} ends on {{lease_end_date}}',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Lease ends on {{lease_end_date}}</h1>'
      || '<p style="' || v_style_p || '">The lease for '
      || '<strong>{{property_name}}{{unit_number}}</strong> runs from '
      || '{{lease_start_date}} to <strong>{{lease_end_date}}</strong>.</p>'
      || '<p style="' || v_style_p || '">Tenant: {{tenant_name}}</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Lease ends on {{lease_end_date}}\n\n'
      || E'Property: {{property_name}}{{unit_number}}\n'
      || E'{{property_address}}\n'
      || E'Term: {{lease_start_date}} to {{lease_end_date}}\n'
      || E'Tenant: {{tenant_name}}\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['property_name', 'unit_number', 'property_address', 'tenant_name',
          'lease_start_date', 'lease_end_date', 'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;

  -- 5. Renewal notice deadline -------------------------------------------------
  -- A distinct template, because a renewal notice deadline is not the lease
  -- end date and conflating the two misleads the reader (section 6.F).
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'renewal_notice_deadline', 'Renewal notice deadline',
    'renewal_notice_deadline', 1, 'published',
    'Renewal notice for {{property_name}}{{unit_number}} is due by {{due_date}}',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Renewal notice due by {{due_date}}</h1>'
      || '<p style="' || v_style_p || '">The lease for '
      || '<strong>{{property_name}}{{unit_number}}</strong> ends on '
      || '{{lease_end_date}}. Under its terms, notice of renewal or '
      || 'non-renewal is due by <strong>{{due_date}}</strong>.</p>'
      || '<p style="' || v_style_p || '">Tenant: {{tenant_name}}</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Renewal notice due by {{due_date}}\n\n'
      || E'Property: {{property_name}}{{unit_number}}\n'
      || E'Lease end date: {{lease_end_date}}\n'
      || E'Notice due by: {{due_date}}\n'
      || E'Tenant: {{tenant_name}}\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['property_name', 'unit_number', 'property_address', 'tenant_name',
          'lease_end_date', 'due_date', 'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;

  -- 6. Custom event ------------------------------------------------------------
  insert into email_templates (
    organization_id, template_key, name, event_type, version, status,
    subject, html_body, text_body, referenced_variables, published_at
  )
  values (
    p_organization_id, 'custom_event', 'Custom event', 'custom', 1, 'published',
    'Reminder for {{property_name}}{{unit_number}} on {{due_date}}',
    '<div style="' || v_style_wrap || '"><div style="' || v_style_card || '">'
      || '<h1 style="' || v_style_h1 || '">Reminder for {{due_date}}</h1>'
      || '<p style="' || v_style_p || '">This is a reminder about '
      || '<strong>{{property_name}}{{unit_number}}</strong>.</p>'
      || '<p style="' || v_style_meta || '">{{property_address}}<br>'
      || 'Sent by {{sender_name}} on behalf of {{landlord_name}}.</p>'
      || '</div></div>',
    E'Reminder for {{due_date}}\n\n'
      || E'Property: {{property_name}}{{unit_number}}\n'
      || E'{{property_address}}\n\n'
      || E'Sent by {{sender_name}} on behalf of {{landlord_name}}.',
    array['property_name', 'unit_number', 'property_address', 'due_date',
          'sender_name', 'landlord_name'],
    now()
  )
  on conflict (organization_id, template_key, version) do nothing;
end;
$fn$;

comment on function app.seed_rent_defaults(uuid) is
  'Seeds recipient groups, a working-day calendar, and the six published email templates.';

grant execute on function app.seed_rent_defaults(uuid) to app_api;

-- -----------------------------------------------------------------------------
-- The entry point the API calls.
--
-- A wrapper from the start, so that later phases can add their own defaults
-- without the API needing to know how many seeding steps exist. 0018 replaces
-- this definition to include the maintenance board.
-- -----------------------------------------------------------------------------
create or replace function app.seed_organization_defaults(p_organization_id uuid)
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
begin
  if not app.can_administer_org(p_organization_id) then
    raise exception 'Not permitted to seed defaults for this organization'
      using errcode = 'insufficient_privilege';
  end if;

  perform app.seed_rent_defaults(p_organization_id);
end;
$fn$;

comment on function app.seed_organization_defaults(uuid) is
  'Everything a new organization needs. Composes the per-area seeding functions.';

grant execute on function app.seed_organization_defaults(uuid) to app_api;
