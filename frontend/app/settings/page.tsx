import Link from "next/link";
import { redirect } from "next/navigation";
import { Bell, ChevronRight, FileText, Mail, ShieldCheck } from "lucide-react";

import { Shell } from "@/components/shell";
import { PeoplePanel } from "@/components/settings/people-panel";
import { WorkspaceSettings } from "@/components/settings/workspace-settings";
import { AppearancePanel } from "@/components/settings/appearance-panel";
import { ErrorState, PageHeader, Panel, Status } from "@/components/ui/primitives";
import {
  api,
  ApiError,
  emailApi,
  membershipApi,
  permissions,
  type EmailAccountState,
  type Invitation,
  type Member,
} from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Settings.
 *
 * <h3>Why this page shows state rather than just links</h3>
 *
 * A settings index that is only a menu makes you open each page to find out
 * whether anything is wrong. The two things most likely to be wrong here are
 * silent — a mailbox that needs reconnecting sends nothing, and a workspace
 * with no mailbox at all sends nothing — so both are reported on the card
 * rather than behind it.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Settings" };

export default async function SettingsPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName = "";
  let organizationTimeZone = "UTC";
  let access = permissions(null);
  let currentUserId: string | null = null;
  let emailState: EmailAccountState | null = null;
  let members: Member[] = [];
  let invitations: Invitation[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    currentUserId = currentUser.userId;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      organizationTimeZone = organization.timeZone;
      access = permissions(organization.callerRole);

      // Each allowed to fail on its own. A settings index that will not
      // render because one panel could not load is worse than a panel that
      // says so.
      const [emailResult, memberResult, invitationResult] = await Promise.all([
        emailApi.state(organization.id).catch(() => null),
        membershipApi.members(organization.id).catch(() => []),
        membershipApi.invitations(organization.id).catch(() => []),
      ]);

      emailState = emailResult;
      members = memberResult;
      invitations = invitationResult;
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const mailbox = emailState?.account ?? null;
  const isConnected = mailbox?.connectionStatus === "connected";
  const needsReconnect = mailbox?.connectionStatus === "reauthorizationRequired";

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName || null}>
      <PageHeader
        title="Settings"
        description="How this workspace sends mail, what it says, and who can see it."
      />

      {/* Outside the workspace check below, and first, because appearance is
          a preference belonging to this browser rather than to the workspace.
          It works before a workspace exists, it is not shared with anybody
          else in one, and nothing about it needs a role. */}
      <Panel
        className="mb-5"
        title="Appearance"
        description="Applies to this browser only. Nobody else in the workspace sees the change."
        padded
      >
        <AppearancePanel />
      </Panel>

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace from the dashboard before changing settings."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          <WorkspaceSettings
            organizationId={organizationId}
            name={organizationName}
            timeZone={organizationTimeZone}
            canEdit={access.canAdminister}
            permissionReason={access.reason}
          />

          <div className="grid gap-3 sm:grid-cols-2">
            <SettingCard
              href="/settings/email"
              icon={<Mail className="size-4" aria-hidden="true" />}
              title="Email"
              description="The one Gmail account every reminder is sent from, and the
                           record of what went out."
              status={
                isConnected ? (
                  <Status tone="success">Connected</Status>
                ) : needsReconnect ? (
                  <Status tone="danger">Needs reconnecting</Status>
                ) : (
                  <Status tone="warning">Not connected</Status>
                )
              }
              footnote={
                isConnected
                  ? mailbox?.emailAddress
                  : "Nothing is sent until a mailbox is connected."
              }
            />

            <SettingCard
              href="/settings/templates"
              icon={<FileText className="size-4" aria-hidden="true" />}
              title="Email templates"
              description="The wording a tenant actually reads. Edited as a draft, then
                           published."
            />

            <SettingCard
              href="/settings/reminders"
              icon={<Bell className="size-4" aria-hidden="true" />}
              title="Reminder rules"
              description="When a rent reminder goes out, and who receives it. Nothing
                           is sent until a rule exists."
            />

            <SettingCard
              href="/settings/notifications"
              icon={<Bell className="size-4" aria-hidden="true" />}
              title="Maintenance notifications"
              description="Which maintenance events notify whom, and when a deadline
                           escalates."
            />

            <SettingCard
              href="/documents"
              icon={<ShieldCheck className="size-4" aria-hidden="true" />}
              title="Lease documents"
              description="Upload a lease, review what was read from it, and approve the
                           terms."
            />
          </div>

          <PeoplePanel
            organizationId={organizationId}
            members={members}
            invitations={invitations}
            currentUserId={currentUserId}
            canAdminister={access.canAdminister}
            permissionReason={access.reason}
          />
        </div>
      )}
    </Shell>
  );
}

function SettingCard({
  href,
  icon,
  title,
  description,
  status,
  footnote,
}: {
  href: string;
  icon: React.ReactNode;
  title: string;
  description: string;
  status?: React.ReactNode;
  footnote?: string | null;
}) {
  return (
    <Link
      href={href}
      className="surface group flex flex-col gap-1.5 p-4 transition-colors
                 motion-safe:duration-150 hover:border-accent hover:bg-surface-subtle"
    >
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-2 text-[14px] font-semibold">
          <span className="text-ink-muted">{icon}</span>
          {title}
        </span>
        {status ?? (
          <ChevronRight
            className="size-4 text-ink-muted transition-transform
                       motion-safe:duration-150 group-hover:translate-x-0.5"
            aria-hidden="true"
          />
        )}
      </div>

      <p className="text-[12.5px] leading-relaxed text-ink-secondary">
        {description}
      </p>

      {footnote ? (
        <p className="truncate text-[12px] text-ink-muted">{footnote}</p>
      ) : null}
    </Link>
  );
}
