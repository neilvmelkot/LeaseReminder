"use client";

import { useMemo, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { CircleAlert, ShieldAlert, TriangleAlert } from "lucide-react";

import { ApprovalDialog } from "@/components/documents/approval-dialog";
import { DocumentViewer } from "@/components/documents/document-viewer";
import { FieldRow } from "@/components/documents/field-row";
import { Button, EmptyState, Status } from "@/components/ui/primitives";
import { PermissionNotice } from "@/components/ui/permission-notice";
import {
  groupFields,
  issueLabel,
  missingRequired,
  needsDecision,
  type DocumentDetail,
  type ExtractedField,
} from "@/lib/documents";

import { approveLeaseAction, reviewFieldAction } from "@/app/documents/actions";

/**
 * The review screen.
 *
 * <h3>Layout</h3>
 *
 * Section 5: the document on the left with page navigation, the extracted
 * fields and their confidence on the right. The two are linked — selecting a
 * field navigates the viewer to the page it cites — because the only way to
 * review an extraction honestly is to read the clause it came from, and
 * anything that makes that harder makes approving without reading easier.
 *
 * <h3>What governs the approval button</h3>
 *
 * It is disabled until every field has been decided and every blocking issue
 * resolved. That rule is enforced by the database, in
 * <code>app.approve_lease_from_extraction</code>; it is repeated here so the
 * screen can say what is outstanding rather than letting someone click and
 * receive a refusal. The database stays the authority, and a disagreement
 * between the two shows up as a refused approval rather than a wrong one.
 */
export function ReviewWorkspace({
  detail,
  organizationId,
  canReview,
  canApprove,
  permissionReason,
  leaseId,
  propertyLabel,
}: {
  detail: DocumentDetail;
  organizationId: string;
  canReview: boolean;
  canApprove: boolean;
  /** From `permissions(role).reason`. Names the caller's own role. */
  permissionReason: string | null;
  /** Null when no lease record exists yet for this document. */
  leaseId: string | null;
  propertyLabel: string;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [selected, setSelected] = useState<ExtractedField | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isDialogOpen, setIsDialogOpen] = useState(false);
  const [isApproving, setIsApproving] = useState(false);
  const [approvalError, setApprovalError] = useState<string | null>(null);
  const [showOnlyOutstanding, setShowOnlyOutstanding] = useState(true);

  const { fields, catalogue, issues, extraction, document } = detail;

  const outstanding = useMemo(
    () => fields.filter((field) => needsDecision(field)),
    [fields],
  );

  const blocking = useMemo(
    () => issues.filter((issue) => issue.severity === "blocking" && !issue.isResolved),
    [issues],
  );

  const missing = useMemo(
    () => missingRequired(fields, catalogue),
    [fields, catalogue],
  );

  const visible = showOnlyOutstanding && outstanding.length > 0 ? outstanding : fields;
  const grouped = useMemo(() => groupFields(visible, catalogue), [visible, catalogue]);

  const correctedCount = fields.filter((f) => f.reviewStatus === "corrected").length;
  const readyToApprove =
    outstanding.length === 0 && blocking.length === 0 && leaseId !== null;

  function decide(
    field: ExtractedField,
    decision: "accepted" | "corrected" | "rejected",
    correctedValue?: unknown,
  ) {
    setError(null);

    startTransition(async () => {
      const result = await reviewFieldAction(field.id, decision, correctedValue);

      if (!result.ok) {
        setError(result.error ?? "That decision could not be recorded.");

        return;
      }

      router.refresh();
    });
  }

  function approve(reason: string) {
    if (!leaseId) {
      return;
    }

    setApprovalError(null);
    setIsApproving(true);

    startTransition(async () => {
      const result = await approveLeaseAction(organizationId, leaseId, {
        extractionId: extraction?.id ?? null,
        note: reason.trim() === "" ? null : reason.trim(),
      });

      setIsApproving(false);

      if (!result.ok) {
        setApprovalError(result.error ?? "This lease could not be approved.");

        return;
      }

      setIsDialogOpen(false);
      router.refresh();
    });
  }

  if (!extraction) {
    return (
      <div className="surface p-6">
        <EmptyState
          title={
            document.processingStatus === "failed"
              ? "This document could not be read"
              : "This document has not been read yet"
          }
          description={
            document.processingError ??
            "It is queued. The terms will appear here once it has been read; you can " +
              "also enter them by hand."
          }
        />
      </div>
    );
  }

  return (
    <>
      <div className="grid gap-4 lg:h-[calc(100dvh-12rem)] lg:grid-cols-[minmax(0,1fr)_minmax(0,28rem)]">
        {/* Left: the document */}
        <section className="surface min-h-[28rem] overflow-hidden lg:h-full">
          <DocumentViewer
            documentId={document.id}
            filename={document.originalFilename}
            mimeType={document.detectedMimeType}
            pageCount={document.pageCount}
            highlightedPage={selected?.sourcePage ?? null}
          />
        </section>

        {/* Right: what was read from it */}
        <section className="surface flex min-h-0 flex-col overflow-hidden lg:h-full">
          <header className="border-b border-line px-4 py-3">
            <div className="flex items-center justify-between gap-2">
              <h2 className="text-[15px] font-semibold">Lease terms</h2>
              <span className="text-[12px] text-ink-secondary">
                Read by {extraction.model ?? extraction.provider}
              </span>
            </div>

            <p className="mt-1 text-[12.5px] leading-relaxed text-ink-secondary">
              Every value below is a suggestion with the passage it came from.
              Check each against the document before approving.
            </p>

            {outstanding.length > 0 ? (
              <div className="mt-2 flex items-center justify-between gap-2">
                <Status tone="warning">
                  {outstanding.length} still to decide
                </Status>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => setShowOnlyOutstanding((previous) => !previous)}
                >
                  {showOnlyOutstanding ? "Show all terms" : "Show only outstanding"}
                </Button>
              </div>
            ) : (
              <Status tone="success">Every term has been decided</Status>
            )}
          </header>

          <div className="thin-scrollbar min-h-0 flex-1 overflow-y-auto">
            {/* Blocking issues first. These stop approval outright, so
                burying them below thirty fields would waste the reviewer's
                time before telling them it was wasted. */}
            {blocking.length > 0 ? (
              <div className="border-b border-line bg-danger/5 px-4 py-3">
                <h3 className="flex items-center gap-1.5 text-[13px] font-semibold text-danger-text">
                  <ShieldAlert className="size-4" aria-hidden="true" />
                  Must be resolved before approval
                </h3>
                <ul className="mt-1.5 space-y-1.5">
                  {blocking.map((issue) => (
                    <li key={issue.id} className="text-[12.5px] leading-relaxed">
                      <span className="font-medium">{issueLabel(issue.issueCode)}.</span>{" "}
                      <span className="text-ink-secondary">{issue.message}</span>
                      {issue.sourcePages.length > 0 ? (
                        <span className="tabular text-ink-muted">
                          {" "}
                          (page{issue.sourcePages.length === 1 ? "" : "s"}{" "}
                          {issue.sourcePages.join(", ")})
                        </span>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}

            {/* Required terms the document did not give. Not a model error to
                review, but a gap somebody has to fill, so it is a separate
                heading rather than thirty rows down the list. */}
            {missing.length > 0 ? (
              <div className="border-b border-line bg-warning/5 px-4 py-3">
                <h3 className="flex items-center gap-1.5 text-[13px] font-semibold text-warning-text">
                  <CircleAlert className="size-4" aria-hidden="true" />
                  Not found in the document
                </h3>
                <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-secondary">
                  Reminders cannot be scheduled without these. Add them to the lease
                  by hand.
                </p>
                <ul className="mt-1.5 flex flex-wrap gap-1">
                  {missing.map((entry) => (
                    <li
                      key={entry.name}
                      className="rounded-control border border-warning/40 bg-warning/10
                                 px-1.5 py-0.5 text-[11.5px] text-warning-text"
                    >
                      {entry.label}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}

            {error ? (
              <p
                role="alert"
                className="flex items-start gap-1.5 border-b border-line bg-danger/10
                           px-4 py-2 text-[12.5px] text-danger-text"
              >
                <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
                {error}
              </p>
            ) : null}

            {grouped.map((group) => (
              <div key={group.group}>
                <h3
                  className="sticky top-0 z-10 border-y border-line bg-surface-subtle px-4
                             py-1.5 text-[11.5px] font-semibold uppercase tracking-wide
                             text-ink-secondary"
                >
                  {group.group}
                </h3>
                <ul>
                  {group.fields.map(({ field, meta }) => (
                    <FieldRow
                      key={field.id}
                      field={field}
                      meta={meta}
                      isSelected={selected?.id === field.id}
                      canEdit={canReview && !isPending}
                      onSelect={setSelected}
                      onDecide={decide}
                    />
                  ))}
                </ul>
              </div>
            ))}
          </div>

          {/* The approval. Deliberately in its own bar, separated from the
              per-field controls, because it is a different kind of act. */}
          <footer className="border-t border-line px-4 py-3">
            {!canApprove ? (
              <PermissionNotice
                reason={
                  canReview && permissionReason
                    ? `${permissionReason} You can still review and correct the terms above.`
                    : permissionReason
                }
                action="Approving a lease"
              />
            ) : !leaseId ? (
              <p className="text-[12.5px] leading-relaxed text-ink-secondary">
                Create the lease record for this property before approving these
                terms.
              </p>
            ) : (
              <>
                <Button
                  variant="primary"
                  className="w-full"
                  disabled={!readyToApprove || isPending}
                  onClick={() => setIsDialogOpen(true)}
                >
                  Approve these lease terms
                </Button>

                {!readyToApprove ? (
                  <p className="mt-1.5 text-center text-[12px] text-ink-secondary">
                    {blocking.length > 0
                      ? `Resolve ${blocking.length} blocking issue${blocking.length === 1 ? "" : "s"} first.`
                      : `${outstanding.length} term${outstanding.length === 1 ? "" : "s"} still need a decision.`}
                  </p>
                ) : null}
              </>
            )}
          </footer>
        </section>
      </div>

      {/* Mounted only while open, so the typed confirmation and the note
          start empty every time without an effect clearing them. */}
      {isDialogOpen ? (
        <ApprovalDialog
          fieldCount={fields.length}
          correctedCount={correctedCount}
          propertyLabel={propertyLabel}
          isSubmitting={isApproving}
          error={approvalError}
          onConfirm={approve}
          onCancel={() => setIsDialogOpen(false)}
        />
      ) : null}
    </>
  );
}
