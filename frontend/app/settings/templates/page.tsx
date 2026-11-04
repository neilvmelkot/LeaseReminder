import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { TemplateEditor } from "@/components/email/template-editor";
import { ErrorState, PageHeader, Panel } from "@/components/ui/primitives";
import {
  api,
  ApiError,
  permissions,
  templateApi,
  type EmailTemplateList,
} from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * The wording of what tenants receive.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Email templates" };

export default async function TemplatesPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let canEdit = false;
  let permissionReason: string | null = null;
  let data: EmailTemplateList | null = null;
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      // A template is the wording every tenant receives in the
      // organization's name, so editing it is an administrator action. The
      // database enforces this independently.
      const access = permissions(organization.callerRole);

      canEdit = access.canAdminister;
      permissionReason = access.reason;

      data = await templateApi.list(organization.id);
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
        title="Email templates"
        description="What a tenant actually reads. A published version cannot be edited,
                     so changes are made as a draft and published when you are ready."
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId || !data ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before editing templates."
          />
        </Panel>
      ) : (
        <TemplateEditor
          organizationId={organizationId}
          data={data}
          canEdit={canEdit}
          permissionReason={permissionReason}
        />
      )}
    </Shell>
  );
}
