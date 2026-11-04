import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { MailboxPanel } from "@/components/email/mailbox-panel";
import { SendLog } from "@/components/email/send-log";
import { ErrorState, PageHeader, Panel } from "@/components/ui/primitives";
import {
  api,
  ApiError,
  emailApi,
  permissions,
  type EmailAccountState,
  type EmailLogEntry,
} from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Email settings: the connected mailbox and what it has sent.
 *
 * <p>
 * Rendered per request. The connection state is the thing a user comes here to
 * check, and a cached answer to "is my mail going out?" would be worse than no
 * answer.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Email" };

export default async function EmailSettingsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const params = await searchParams;

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let canManage = false;
  let permissionReason: string | null = null;
  let state: EmailAccountState | null = null;
  let logs: EmailLogEntry[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      // Connecting a mailbox commits the organization to sending mail in its
      // name, so it is an administrator action. The API and the database both
      // enforce this; reflecting it here means a property manager sees why
      // rather than meeting a 403.
      const access = permissions(organization.callerRole);

      canManage = access.canAdminister;
      permissionReason = access.reason;

      const [stateResult, logResult] = await Promise.all([
        emailApi.state(organization.id),
        emailApi.logs(organization.id, 200),
      ]);

      state = stateResult;
      logs = logResult;
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
      <PageHeader
        title="Email"
        description="Reminders are sent from one Gmail account that you connect. Nothing
                     is sent from anywhere else."
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId || !state ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before connecting a mailbox."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          <MailboxPanel
            organizationId={organizationId}
            state={state}
            authorizeUrl={emailApi.authorizeUrl(organizationId)}
            canManage={canManage}
            permissionReason={permissionReason}
            connectionResult={readParam(params, "connection")}
            connectionReason={readParam(params, "reason")}
          />

          <SendLog entries={logs} />
        </div>
      )}
    </Shell>
  );
}

function readParam(
  params: Record<string, string | string[] | undefined>,
  key: string,
): string | null {
  const value = params[key];

  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return value ?? null;
}
