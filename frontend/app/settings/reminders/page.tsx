import Link from "next/link";
import { redirect } from "next/navigation";
import { ChevronLeft } from "lucide-react";

import { Shell } from "@/components/shell";
import { ReminderRuleWorkspace } from "@/components/reminders/reminder-rule-workspace";
import { ErrorState, PageHeader, Panel } from "@/components/ui/primitives";
import {
  api,
  ApiError,
  permissions,
  reminderRuleApi,
  type Property,
  type Recipient,
  type RecipientGroup,
  type ReminderRule,
  type Tenant,
} from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Rent reminder rules, and the people they are addressed to.
 *
 * <h3>Why this is its own page rather than part of Notification rules</h3>
 *
 * The existing Notification rules page is the MAINTENANCE board's — it
 * decides who hears about a task. These rules decide who hears about rent.
 * They share a vocabulary and nothing else: different tables, different
 * triggers, different audiences, and a tenant who should never see a
 * maintenance escalation.
 *
 * Putting them together would mean one screen where half the controls do
 * nothing for half the rules.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Reminder rules" };

export default async function ReminderRulesPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let rules: ReminderRule[] = [];
  let recipients: Recipient[] = [];
  let groups: RecipientGroup[] = [];
  let properties: Property[] = [];
  let tenants: Tenant[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      // All at once. Each is small, and the rule editor needs every one of
      // them to open — fetching them in sequence would be five times the
      // latency for a screen that is mostly waiting.
      const [ruleResult, recipientResult, groupResult, propertyResult, tenantResult] =
        await Promise.all([
          reminderRuleApi.rules(organization.id),
          reminderRuleApi.recipients(organization.id),
          reminderRuleApi.groups(organization.id),
          api.properties(organization.id),
          api.tenants(organization.id),
        ]);

      rules = ruleResult;
      recipients = recipientResult;
      groups = groupResult;
      properties = propertyResult;
      tenants = tenantResult;
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/settings"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          Settings
        </Link>
      </div>

      <PageHeader
        title="Reminder rules"
        description="A rent reminder needs four things at once: an approved lease, a rent
                     schedule, a rule saying when to send, and a connected mailbox.
                     This page is the third."
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before setting up reminders."
          />
        </Panel>
      ) : (
        <ReminderRuleWorkspace
          organizationId={organizationId}
          rules={rules}
          recipients={recipients}
          groups={groups}
          properties={properties}
          tenants={tenants}
          canAdminister={access.canAdminister}
          permissionReason={access.reason}
        />
      )}
    </Shell>
  );
}
