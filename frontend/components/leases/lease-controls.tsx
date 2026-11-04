"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { BellOff, BellRing, Check, TriangleAlert, X } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import type { Lease } from "@/lib/api";

import {
  approveManualLeaseAction,
  rejectLeaseAction,
  setLeaseAutomationAction,
} from "@/app/leases/actions";

type Dialog = "approve" | "reject" | "pause" | null;

/**
 * Approving, rejecting, and switching the reminders off.
 *
 * <h3>Why approval has a dialog rather than a button</h3>
 *
 * Approval is the moment typed-in terms become something this system will act
 * on: it is what permits a rent schedule to be generated and emails to be sent
 * to a tenant about money. A primary button in a toolbar, beside Edit, would
 * make that indistinguishable from renaming a property.
 *
 * The dialog does what a button cannot — it states the consequence before the
 * act, and it records a note alongside the approval, so the audit trail says
 * who decided and on what basis.
 *
 * <h3>Why pausing is offered separately from rejecting</h3>
 *
 * They are not degrees of the same thing. Rejecting says the terms are wrong.
 * Pausing says the terms are right and the sending should stop anyway — a
 * dispute, a payment plan, a tenant who asked to be contacted another way.
 * Collapsing them would force somebody to misrepresent one as the other, in
 * the permanent record.
 */
export function LeaseControls({
  organizationId,
  lease,
  canWrite,
  canAdminister,
}: {
  organizationId: string;
  lease: Lease;
  canWrite: boolean;
  canAdminister: boolean;
}) {
  const router = useRouter();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [reason, setReason] = useState("");

  function open(which: Dialog) {
    setError(null);
    setNote("");
    setReason("");
    setDialog(which);
  }

  function run(action: () => Promise<{ ok: boolean; error?: string }>, fallback: string) {
    setError(null);

    startTransition(async () => {
      const result = await action();

      if (!result.ok) {
        setError(result.error ?? fallback);

        return;
      }

      setDialog(null);
      router.refresh();
    });
  }

  const isPaused = lease.automationStatus === "paused";
  const isPending_ = lease.approvalStatus === "pendingReview";

  return (
    <>
      <div className="flex flex-wrap items-center justify-end gap-2">
        {canWrite && isPending_ ? (
          <>
            <Button variant="primary" size="sm" onClick={() => open("approve")}>
              <Check className="size-3.5" aria-hidden="true" />
              Approve
            </Button>
            <Button variant="ghost" size="sm" onClick={() => open("reject")}>
              <X className="size-3.5" aria-hidden="true" />
              Reject
            </Button>
          </>
        ) : null}

        {/* Only meaningful once the lease is approved: there is nothing to
            pause on a lease that was never going to send anything. */}
        {canAdminister && lease.approvalStatus === "approved" ? (
          isPaused ? (
            <Button
              variant="secondary"
              size="sm"
              disabled={isPending}
              onClick={() =>
                run(
                  () => setLeaseAutomationAction(organizationId, lease.id, false),
                  "The reminders could not be resumed.",
                )
              }
            >
              <BellRing className="size-3.5" aria-hidden="true" />
              {isPending ? "Resuming…" : "Resume reminders"}
            </Button>
          ) : (
            <Button variant="secondary" size="sm" onClick={() => open("pause")}>
              <BellOff className="size-3.5" aria-hidden="true" />
              Pause reminders
            </Button>
          )
        ) : null}
      </div>

      {error && dialog === null ? (
        <p role="alert" className="reveal mt-2 text-right text-[12px] text-danger-text">
          {error}
        </p>
      ) : null}

      {dialog === "approve" ? (
        <FormDialog
          title="Approve these lease terms"
          description="This is what permits reminders to be generated and sent for this lease."
          submitLabel="Approve lease"
          isSubmitting={isPending}
          onSubmit={() =>
            run(
              () =>
                approveManualLeaseAction(organizationId, lease.id, note || null),
              "This lease could not be approved.",
            )
          }
          onCancel={() => setDialog(null)}
        >
          <div className="rounded-control border border-line bg-surface-subtle px-3 py-2.5">
            <p className="text-[13px] font-medium">
              {lease.reference ?? "Untitled lease"}
            </p>
            <p className="mt-0.5 text-[12px] text-ink-secondary">
              {lease.propertyName}
              {lease.unitNumber ? ` · ${lease.unitNumber}` : ""}
            </p>
          </div>

          <Field
            label="Note"
            htmlFor="approve-note"
            hint="Optional, and kept with the approval record."
          >
            <textarea
              id="approve-note"
              value={note}
              onChange={(event) => setNote(event.target.value)}
              rows={2}
              maxLength={2000}
              className={fieldClass}
            />
          </Field>

          <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12px] leading-relaxed text-ink-secondary">
            Approving records that you reviewed these terms, with your name and
            the time. It does not send anything by itself — a rent schedule and
            a connected mailbox are both still needed before a tenant hears
            from this.
          </p>

          {error ? (
            <p
              role="alert"
              className="reveal flex items-start gap-1.5 rounded-control border
                         border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                         text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}
        </FormDialog>
      ) : null}

      {dialog === "reject" ? (
        <FormDialog
          title="Reject these terms"
          description="Use this when the terms are wrong. To keep the lease but stop the sending, pause the reminders instead."
          submitLabel="Reject lease"
          isSubmitting={isPending}
          isSubmitDisabled={reason.trim().length === 0}
          onSubmit={() =>
            run(
              () => rejectLeaseAction(organizationId, lease.id, reason),
              "This lease could not be rejected.",
            )
          }
          onCancel={() => setDialog(null)}
        >
          <Field
            label="Reason"
            htmlFor="reject-reason"
            required
            hint="Recorded with the rejection. Say what is wrong, so the next person does not have to guess."
          >
            <textarea
              id="reject-reason"
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              rows={3}
              maxLength={2000}
              className={fieldClass}
            />
          </Field>

          {error ? (
            <p
              role="alert"
              className="reveal flex items-start gap-1.5 rounded-control border
                         border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                         text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}
        </FormDialog>
      ) : null}

      {dialog === "pause" ? (
        <FormDialog
          title="Pause reminders for this lease"
          description="The lease stays approved and active. Only the sending stops."
          submitLabel="Pause reminders"
          isSubmitting={isPending}
          onSubmit={() =>
            run(
              () =>
                setLeaseAutomationAction(organizationId, lease.id, true, reason),
              "The reminders could not be paused.",
            )
          }
          onCancel={() => setDialog(null)}
        >
          <Field
            label="Why"
            htmlFor="pause-reason"
            hint="Shown on the lease, so anybody looking later knows why it went quiet."
          >
            <input
              id="pause-reason"
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              placeholder="Payment plan agreed until March"
              maxLength={500}
              className={fieldClass}
            />
          </Field>

          <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12px] leading-relaxed text-ink-secondary">
            Reminders already queued for this lease will not be sent while it is
            paused. Resuming does not send the ones that were missed — it
            resumes from the next one due.
          </p>

          {error ? (
            <p
              role="alert"
              className="reveal flex items-start gap-1.5 rounded-control border
                         border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                         text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}
        </FormDialog>
      ) : null}
    </>
  );
}
