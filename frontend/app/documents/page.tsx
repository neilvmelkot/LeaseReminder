import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { UploadPanel } from "@/components/documents/upload-panel";
import { DocumentList } from "@/components/documents/document-list";
import {
  ErrorState,
  PageHeader,
  Panel,
  Status,
} from "@/components/ui/primitives";
import { api, ApiError, documentApi, permissions } from "@/lib/api";
import type { LeaseDocumentSummary } from "@/lib/documents";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Lease documents.
 *
 * <p>
 * Rendered per request. These are lease files and their processing state, so
 * there is no correct build-time output and nothing here may sit in a shared
 * cache.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Lease documents" };

export default async function DocumentsPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let canUpload = false;
  let canAdminister = false;
  let permissionReason: string | null = null;
  let documents: LeaseDocumentSummary[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      // One helper answers this for every page and hands back the sentence
      // explaining it, so the wording cannot drift between screens.
      const access = permissions(organization.callerRole);

      canUpload = access.canWrite;
      canAdminister = access.canAdminister;
      permissionReason = access.reason;

      documents = await documentApi.list(organization.id);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const awaitingReview = documents.filter(
    (document) => document.processingStatus === "reviewReady" && !document.leaseId,
  ).length;

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Lease documents"
        description="Upload a lease and its terms are read out for you to check. Nothing
                     takes effect until you approve it."
        meta={
          awaitingReview > 0 ? (
            <Status tone="info">{awaitingReview} awaiting review</Status>
          ) : null
        }
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before uploading lease documents."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          <UploadPanel
            organizationId={organizationId}
            canUpload={canUpload}
            permissionReason={permissionReason}
            // Whether a key is configured is a server-side fact. Reporting it
            // here lets the panel say what will happen before the upload
            // rather than after a worker cycle of silence.
            extractionAvailable={process.env.EXTRACTION_CONFIGURED !== "false"}
          />

          <Panel title="Uploaded">
            <DocumentList
              documents={documents}
              canWrite={canUpload}
              canAdminister={canAdminister}
            />
          </Panel>
        </div>
      )}
    </Shell>
  );
}
