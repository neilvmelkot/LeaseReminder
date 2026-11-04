"use client";

import { useEffect, useRef, useState } from "react";
import { ShieldCheck, TriangleAlert } from "lucide-react";

import { Button, cx } from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";

/**
 * The approval confirmation.
 *
 * <h3>Why this is a dialog and not a button</h3>
 *
 * Section 5: "Do not make approval look like an ordinary save action."
 * Approval is the moment a machine's reading of a lease becomes something this
 * system will act on — it is what lets reminders be scheduled against these
 * terms and emails sent to a tenant about money. A primary button in a
 * toolbar, next to Save and Cancel, would make that indistinguishable from
 * renaming a property.
 *
 * So the dialog does three things a button cannot:
 *
 *   1. It states what is being approved, in numbers, so the reviewer sees the
 *      scale of what they are signing off.
 *   2. It names the consequence in plain words, including the one that is
 *      easy to assume wrongly: approving does NOT start sending anything.
 *   3. It requires the reviewer to type the word. A deliberate act of typing
 *      is not something a hand does by accident on the way to somewhere else.
 *
 * <h3>Why it also says what approval does not do</h3>
 *
 * Approval and automation are separate state machines (section 13.E), and the
 * natural assumption is that approving a lease starts it running. Someone who
 * believes that and is wrong will either expect reminders that never come, or
 * approve cautiously to avoid emails that were never going to be sent. Saying
 * so here is cheaper than either.
 */
export function ApprovalDialog({
  fieldCount,
  correctedCount,
  propertyLabel,
  isSubmitting,
  error,
  onConfirm,
  onCancel,
}: {
  fieldCount: number;
  correctedCount: number;
  propertyLabel: string;
  isSubmitting: boolean;
  error: string | null;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  const { isClosing, requestClose } = useExitAnimation(onCancel);
  const [typed, setTyped] = useState("");
  const [reason, setReason] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const previouslyFocused = useRef<HTMLElement | null>(null);

  // No state is reset here, because there is none to reset: the parent mounts
  // this component when the dialog opens and unmounts it when it closes, so
  // the typed confirmation and the note start empty every time by
  // construction. Clearing them in an effect instead would be a cascading
  // render doing the job that unmounting already does.
  // Deliberately two effects, and the focus one is deliberately mount-only.
  //
  // These were one effect keyed on the close callback. Callers pass an inline
  // arrow for that, so its identity changes every render, the effect re-ran
  // every render, and the focus call dragged the caret back to the first
  // field on every keystroke. Focus-on-open happens once per opening; the
  // component is mounted per opening, so [] is exactly that.
  useEffect(() => {
    // Remember what had focus so it can be given back on close. Without this,
    // dismissing the dialog drops focus to the document body and a keyboard
    // user has to tab from the top of the page again.
    previouslyFocused.current = document.activeElement as HTMLElement | null;
    inputRef.current?.focus();

    const restoreTo = previouslyFocused.current;

    return () => {
      restoreTo?.focus();
    };
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        requestClose();
      }
    };

    document.addEventListener("keydown", onKeyDown);

    return () => document.removeEventListener("keydown", onKeyDown);
  }, [requestClose]);

  const confirmed = typed.trim().toLowerCase() === "approve";

  return (
    <div
      className={cx(
        "fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4",
        isClosing
          ? "motion-safe:animate-[fade-out_150ms_ease-in_forwards]"
          : "motion-safe:animate-[fade-in_150ms_ease-out]",
      )}
      role="dialog"
      aria-modal="true"
      aria-labelledby="approval-title"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          requestClose();
        }
      }}
    >
      <div
        className={cx(
          "w-full max-w-lg rounded-surface border border-line bg-surface shadow-lg",
          isClosing
            ? "motion-safe:animate-[dialog-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[dialog-in_180ms_cubic-bezier(0.16,1,0.3,1)]",
        )}
      >
        <div className="flex items-start gap-3 border-b border-line px-5 py-4">
          <ShieldCheck className="mt-0.5 size-5 shrink-0 text-accent" aria-hidden="true" />
          <div>
            <h2 id="approval-title" className="text-[16px] font-semibold">
              Approve these lease terms
            </h2>
            <p className="mt-0.5 text-[13px] text-ink-secondary">{propertyLabel}</p>
          </div>
        </div>

        <div className="space-y-3 px-5 py-4">
          <p className="text-[13.5px] leading-relaxed">
            You are confirming that{" "}
            <span className="font-semibold">{fieldCount} lease term{fieldCount === 1 ? "" : "s"}</span>{" "}
            read from this document match the document itself
            {correctedCount > 0 ? (
              <>
                , including the{" "}
                <span className="font-semibold">
                  {correctedCount} you corrected
                </span>
              </>
            ) : null}
            .
          </p>

          <div className="rounded-control border border-line bg-surface-subtle px-3 py-2.5">
            <p className="text-[13px] font-medium">What this does</p>
            <ul className="mt-1 space-y-1 text-[12.5px] leading-relaxed text-ink-secondary">
              <li>
                The lease can be made active, and rent reminders can be scheduled
                against these terms.
              </li>
              <li>
                Your name and the time are recorded against the decision
                permanently. It cannot be edited afterwards.
              </li>
            </ul>

            <p className="mt-2 text-[13px] font-medium">What this does not do</p>
            <ul className="mt-1 space-y-1 text-[12.5px] leading-relaxed text-ink-secondary">
              <li>
                No email is sent to anyone. Reminders stay switched off until you
                turn them on separately.
              </li>
            </ul>
          </div>

          <label className="block">
            <span className="mb-1 block text-[12.5px] font-medium">
              Note (optional)
            </span>
            <input
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              placeholder="Checked against the signed copy"
              maxLength={2000}
              className="w-full rounded-control border border-line-strong bg-surface px-2.5
                         py-1.5 text-[13px] focus:border-accent focus:outline-none
                         focus:ring-2 focus:ring-accent/20"
            />
          </label>

          <label className="block">
            <span className="mb-1 block text-[12.5px] font-medium">
              Type <span className="font-mono font-semibold">approve</span> to confirm
            </span>
            <input
              ref={inputRef}
              value={typed}
              onChange={(event) => setTyped(event.target.value)}
              autoComplete="off"
              spellCheck={false}
              className="w-full rounded-control border border-line-strong bg-surface px-2.5
                         py-1.5 font-mono text-[13px] focus:border-accent
                         focus:outline-none focus:ring-2 focus:ring-accent/20"
            />
          </label>

          {error ? (
            <p
              role="alert"
              className="flex items-start gap-1.5 rounded-control border border-danger/30
                         bg-danger/10 px-2.5 py-2 text-[12.5px] text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}
        </div>

        <div className="flex justify-end gap-2 border-t border-line px-5 py-3">
          <Button variant="ghost" onClick={requestClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button
            variant="primary"
            disabled={!confirmed || isSubmitting}
            onClick={() => onConfirm(reason)}
          >
            {isSubmitting ? "Approving…" : "Approve these terms"}
          </Button>
        </div>
      </div>
    </div>
  );
}
