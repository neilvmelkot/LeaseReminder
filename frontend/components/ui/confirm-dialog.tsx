"use client";

import { useEffect, useRef } from "react";
import { TriangleAlert, X } from "lucide-react";

import { Button, cx } from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";

/**
 * A modal that asks before doing something that cannot be undone from the
 * same screen.
 *
 * <h3>When to reach for this rather than an inline confirmation</h3>
 *
 * The board's archive and the role picker confirm in place, because on a list
 * of similar rows a modal asking "are you sure?" has moved the question away
 * from the row it is about. That reasoning stops holding when the action is
 * not reversible from the same screen: removing somebody from a workspace
 * cannot be undone with a second click, it needs a fresh invitation they have
 * to accept again.
 *
 * So the rule is reversibility, not severity. Reversible and repetitive
 * confirms in place; irreversible interrupts, and names exactly what it is
 * about so the wrong row cannot be confirmed by muscle memory.
 *
 * <h3>Focus</h3>
 *
 * The CANCEL button takes focus, not the confirm. A dialog that opens with
 * the destructive action focused turns a stray Enter into the thing the
 * dialog exists to prevent.
 */
export function ConfirmDialog({
  title,
  body,
  confirmLabel,
  isWorking = false,
  tone = "danger",
  onConfirm,
  onCancel,
}: {
  title: string;
  body: React.ReactNode;
  confirmLabel: string;
  isWorking?: boolean;
  /** "danger" for destruction; "primary" for a merely significant change. */
  tone?: "danger" | "primary";
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<HTMLElement | null>(null);
  const pressedOnBackdrop = useRef(false);
  const { isClosing, requestClose } = useExitAnimation(onCancel);

  // Mount-only, like the other dialogs: this is mounted per opening, so []
  // is exactly "once per opening". Keying it on a callback the caller
  // declares inline would re-run it on every render.
  useEffect(() => {
    previouslyFocused.current = document.activeElement as HTMLElement | null;

    panelRef.current?.querySelector<HTMLElement>("[data-cancel]")?.focus();

    const restoreTo = previouslyFocused.current;

    return () => {
      restoreTo?.focus();
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        requestClose();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    return () => document.removeEventListener("keydown", onKeyDown);
  }, [requestClose]);

  return (
    <div
      className={cx(
        "fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4",
        isClosing
          ? "motion-safe:animate-[fade-out_150ms_ease-in_forwards]"
          : "motion-safe:animate-[fade-in_150ms_ease-out]",
      )}
      role="alertdialog"
      aria-modal="true"
      aria-label={title}
      onMouseDown={(event) => {
        pressedOnBackdrop.current = event.target === event.currentTarget;
      }}
      onMouseUp={(event) => {
        if (pressedOnBackdrop.current && event.target === event.currentTarget) {
          requestClose();
        }

        pressedOnBackdrop.current = false;
      }}
    >
      <div
        ref={panelRef}
        className={cx(
          `w-full max-w-md overflow-hidden rounded-surface border border-line
           bg-surface shadow-lg`,
          isClosing
            ? "motion-safe:animate-[dialog-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[dialog-in_180ms_cubic-bezier(0.16,1,0.3,1)]",
        )}
      >
        <header className="flex items-start justify-between gap-3 px-5 pb-2 pt-4">
          <div className="flex items-start gap-2.5">
            {tone === "danger" ? (
              <span
                className="mt-0.5 grid size-7 shrink-0 place-items-center rounded-full
                           bg-danger-tint text-danger-text"
                aria-hidden="true"
              >
                <TriangleAlert className="size-4" />
              </span>
            ) : null}
            <h2 className="text-[15.5px] font-semibold leading-snug">{title}</h2>
          </div>

          <button
            type="button"
            onClick={requestClose}
            aria-label="Close"
            className="rounded-control p-1 text-ink-muted transition-colors
                       hover:bg-surface-subtle hover:text-ink"
          >
            <X className="size-4" aria-hidden="true" />
          </button>
        </header>

        <div className="px-5 pb-4 pl-[4.1rem] text-[13px] leading-relaxed text-ink-secondary">
          {body}
        </div>

        <footer className="flex justify-end gap-2 border-t border-line px-5 py-3">
          <Button
            type="button"
            variant="ghost"
            data-cancel
            onClick={requestClose}
            disabled={isWorking}
          >
            Cancel
          </Button>
          <Button
            type="button"
            variant={tone}
            onClick={onConfirm}
            disabled={isWorking || isClosing}
          >
            {isWorking ? "Working…" : confirmLabel}
          </Button>
        </footer>
      </div>
    </div>
  );
}
