import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ChevronLeft } from "lucide-react";

import { Shell } from "@/components/shell";
import { ReviewWorkspace } from "@/components/documents/review-workspace";
import { ErrorState, Panel, Status } from "@/components/ui/primitives";
import { api, ApiError, documentApi, permissions } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";
import { documentLabel, type DocumentDetail } from "@/lib/documents";

/**
 * The review screen for one lease document.
 *
 * <p>
 * Section 5 describes this layout: the document on the left with page
 * navigation, the extracted fields and their confidence on the right. The two
 * are linked, so selecting a term shows the page it was read from.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Review lease terms" };

export default async function DocumentReviewPage({
  params,
}: {
  params: Promise<{ documentId: string }>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const { documentId } = await params;

  let organizationName: string | null = null;
  let canReview = false;
  let canApprove = false;
  let permissionReason: string | null = null;
  let detail: DocumentDetail | null = null;
  let leaseId: string | null = null;
  let propertyLabel = "This lease";
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationName = organization.name;

      const access = permissions(organization.callerRole);

      canReview = access.canWrite;
      permissionReason = access.reason;

      // Approval is narrower than review on purpose. A lease going live
      // commits the organization to sending mail in its name. The database
      // enforces this independently; reflecting it here means a property
      // manager sees why the button is absent rather than meeting a 403.
      canApprove = access.canAdminister;

      detail = await documentApi.detail(documentId);
      leaseId = detail.document.leaseId;

      // A lease record has to exist before terms can be approved onto it.
      // Where the document is not yet attached to one, fall back to the
      // single lease awaiting review, which is the usual case for a
      // freshly-uploaded document.
      if (!leaseId) {
        const leases = await api.leases(organization.id);
        const pending = leases.filter((lease) => lease.approvalStatus === "pendingReview");

        if (pending.length === 1 && pending[0]) {
          leaseId = pending[0].id;
          propertyLabel = pending[0].propertyName ?? propertyLabel;
        }
      } else {
        const leases = await api.leases(organization.id);
        const match = leases.find((lease) => lease.id === leaseId);

        if (match) {
          propertyLabel = match.propertyName ?? propertyLabel;
        }
      }
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    if (error instanceof ApiError && error.status === 404) {
      notFound();
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/documents"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          All documents
        </Link>

        {detail ? (
          <div className="mt-1 flex flex-wrap items-center gap-2">
            <h1>{documentLabel(detail.document)}</h1>
            {detail.document.leaseId ? (
              <Status tone="success">Approved</Status>
            ) : detail.extraction ? (
              <Status tone="info">Awaiting review</Status>
            ) : null}
          </div>
        ) : null}
      </div>

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !detail ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before reviewing lease documents."
          />
        </Panel>
      ) : (
        <ReviewWorkspace
          detail={detail}
          organizationId={detail.document.organizationId}
          canReview={canReview}
          canApprove={canApprove}
          permissionReason={permissionReason}
          leaseId={leaseId}
          propertyLabel={propertyLabel}
        />
      )}
    </Shell>
  );
}
