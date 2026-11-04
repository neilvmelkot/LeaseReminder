"use client";

import { useEffect, useRef } from "react";
import { X } from "lucide-react";

import { Button, cx } from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";

/**
 * A modal that holds a form.
 *
 * <h3>Mount to open, unmount to close</h3>
 *
 * The caller renders this only while it is open, so every field starts empty
 * by construction rather than by a reset running somewhere. That also means
 * the entry animation plays once per opening, which is what makes it read as
 * the dialog arriving rather than the page twitching.
 *
 * <h3>Focus</h3>
 *
 * Focus moves to the first field on open and returns to whatever had it when
 * the dialog closes. Without the second half, dismissing a dialog drops focus
 * to the document body and a keyboard user starts again from the top of the
 * page. Escape closes, and a click on the backdrop closes — but only a click
 * that both started and ended there, so a drag that overshoots a text
 * selection does not discard what somebody typed.
 *
 * <h3>Closing takes a moment on purpose</h3>
 *
 * Mounting to open means the caller unmounts to close, and an unmount is
 * instant — the dialog animated in and then simply ceased to exist, which
 * reads as a glitch rather than a dismissal. So a dismissal plays the exit
 * animation here first and tells the caller afterwards. The caller's contract
 * does not change: it still just renders this while it is open.
 *
 * Under <code>prefers-reduced-motion</code> it closes immediately, because
 * there is no animation to wait for.
 */
export function FormDialog({
  title,
  description,
  submitLabel,
  isSubmitting,
  onSubmit,
  onCancel,
  children,
  isSubmitDisabled,
}: {
  title: string;
  description?: string;
  submitLabel: string;
  isSubmitting: boolean;
  onSubmit: () => void;
  onCancel: () => void;
  children: React.ReactNode;
  isSubmitDisabled?: boolean;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<HTMLElement | null>(null);
  const pressedOnBackdrop = useRef(false);
  const { isClosing, requestClose } = useExitAnimation(onCancel);

  // Deliberately two effects, and the focus one is deliberately mount-only.
  //
  // These were one effect keyed on the close callback. Callers pass an inline
  // arrow for that, so its identity changes every render, the effect re-ran
  // every render, and the focus call dragged the caret back to the first
  // field on every keystroke. Focus-on-open happens once per opening; the
  // component is mounted per opening, so [] is exactly that.
  useEffect(() => {
    previouslyFocused.current = document.activeElement as HTMLElement | null;

    // The first thing somebody needs to type, not the close button.
    panelRef.current
      ?.querySelector<HTMLElement>("input, select, textarea")
      ?.focus();

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
      role="dialog"
      aria-modal="true"
      aria-label={title}
      onMouseDown={(event) => {
        pressedOnBackdrop.current = event.target === event.currentTarget;
      }}
      onMouseUp={(event) => {
        // Both ends on the backdrop. A selection drag that leaves the panel
        // would otherwise throw away a half-filled form.
        if (pressedOnBackdrop.current && event.target === event.currentTarget) {
          requestClose();
        }

        pressedOnBackdrop.current = false;
      }}
    >
      <div
        ref={panelRef}
        className={cx(
          `max-h-[calc(100dvh-2rem)] w-full max-w-lg overflow-y-auto
           rounded-surface border border-line bg-surface shadow-lg`,
          isClosing
            ? "motion-safe:animate-[dialog-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[dialog-in_180ms_cubic-bezier(0.16,1,0.3,1)]",
        )}
      >
        <form
          onSubmit={(event) => {
            event.preventDefault();
            onSubmit();
          }}
        >
          <header className="flex items-start justify-between gap-3 border-b border-line px-5 py-4">
            <div>
              <h2 className="text-[16px] font-semibold">{title}</h2>
              {description ? (
                <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-secondary">
                  {description}
                </p>
              ) : null}
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

          <div className="space-y-4 px-5 py-4">{children}</div>

          <footer className="flex justify-end gap-2 border-t border-line px-5 py-3">
            <Button
              type="button"
              variant="ghost"
              onClick={requestClose}
              disabled={isSubmitting}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              variant="primary"
              disabled={isSubmitting || isSubmitDisabled || isClosing}
            >
              {isSubmitting ? "Saving…" : submitLabel}
            </Button>
          </footer>
        </form>
      </div>
    </div>
  );
}

/**
 * One labelled field.
 *
 * A real label with htmlFor, not a placeholder: a placeholder disappears the
 * moment the field has content, which leaves a screen reader and a distracted
 * user with an unlabelled box.
 */
export function Field({
  label,
  htmlFor,
  hint,
  error,
  required,
  children,
}: {
  label: string;
  htmlFor: string;
  hint?: string;
  error?: string | null;
  required?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className="block text-[12.5px] font-medium">
        {label}
        {required ? (
          <span className="ml-0.5 text-danger-text" aria-hidden="true">
            *
          </span>
        ) : null}
      </label>

      <div className="mt-1.5">{children}</div>

      {error ? (
        <p role="alert" className="mt-1 text-[12px] text-danger-text">
          {error}
        </p>
      ) : hint ? (
        <p className="mt-1 text-[12px] leading-relaxed text-ink-secondary">{hint}</p>
      ) : null}
    </div>
  );
}

/** The shared input styling, so every form field looks like the others. */
export const fieldClass =
  "w-full rounded-control border border-line-strong bg-surface px-2.5 py-1.5 " +
  "text-[13px] transition-colors focus:border-accent focus:outline-none " +
  "focus:ring-2 focus:ring-accent/20 disabled:bg-surface-subtle " +
  "disabled:text-ink-secondary";
