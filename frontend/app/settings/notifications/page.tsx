import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { NotificationRuleList } from "@/components/notification-rule-list";
import { PermissionNotice } from "@/components/ui/permission-notice";
import {
  EmptyState,
  ErrorState,
  PageHeader,
  Panel,
} from "@/components/ui/primitives";
import { api, ApiError, maintenanceApi, permissions } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";
import type { TaskNotificationRule } from "@/lib/maintenance";

/**
 * Task notification rules.
 *
 * <p>
 * Every rule is independently switchable, which is what the brief asks for.
 * The rules are grouped by what triggers them, because the two kinds answer
 * different questions: event rules are "tell somebody when this happens",
 * schedule rules are "chase this if nobody does anything".
 * </p>
 *
 * <p>
 * Each row shows who it notifies. Without that, "Overdue" and "Overdue
 * escalation" look like duplicates, when the difference is precisely that the
 * second one goes above the assignee's head.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Notification rules" };

export default async function NotificationSettingsPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let canEdit = false;
  let permissionReason: string | null = null;
  let rules: TaskNotificationRule[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      // A rule change affects what everybody in the workspace receives, so it
      // is an administrator action. The API enforces this; reflecting it here
      // means a read-only member sees why the toggles are disabled instead of
      // clicking one and getting a 403.
      const access = permissions(organization.callerRole);

      canEdit = access.canAdminister;
      permissionReason = access.reason;

      rules = await maintenanceApi.notificationRules(organization.id);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const eventRules = rules.filter((rule) => rule.triggerKind === "event");
  const scheduleRules = rules.filter((rule) => rule.triggerKind === "schedule");

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Task notification rules"
        description="Each rule is switched on or off independently. Turning one off stops
                     the email and the in-app notification together."
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before configuring notification rules."
          />
        </Panel>
      ) : rules.length === 0 ? (
        <Panel>
          <EmptyState
            title="No rules yet"
            description="A new workspace is seeded with a sensible default set. If this is
                         empty, the board has not been set up."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          {!canEdit ? (
            <PermissionNotice
              reason={
                permissionReason
                  ? `${permissionReason} You can see what is configured, but the switches are read-only.`
                  : null
              }
              action="Changing reminder rules"
            />
          ) : null}

          <Panel
            title="When something happens"
            description="Fired immediately by an action on a task."
          >
            <NotificationRuleList
              organizationId={organizationId}
              rules={eventRules}
              canEdit={canEdit}
            />
          </Panel>

          <Panel
            title="When a deadline passes"
            description="Checked hourly against each property's own time zone, so a task is
                         never called overdue early."
          >
            <NotificationRuleList
              organizationId={organizationId}
              rules={scheduleRules}
              canEdit={canEdit}
            />
          </Panel>

          <p className="text-[12.5px] leading-relaxed text-ink-secondary">
            A rule that targets a tenant always honours that tenant&apos;s own
            notification preference, and an internal note is never sent to the tenant
            who reported the task. Nobody is notified about their own action.
          </p>
        </div>
      )}
    </Shell>
  );
}
