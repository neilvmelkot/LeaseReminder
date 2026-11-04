"use client";

import { useEffect, useRef } from "react";
import { X, type LucideIcon } from "lucide-react";

import { cx } from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";

/**
 * A modal that asks which of two or three routes somebody wants.
 *
 * <h3>Why this is not a FormDialog with radio buttons</h3>
 *
 * A fork in a workflow is not a field. Radios plus a Continue button make
 * somebody choose, then confirm the choice, for no gain — and the choice is
 * usually obvious the moment the options are described. Each option here is a
 * single button that commits, so picking one is one action.
 *
 * It also lets each option carry a sentence explaining what will happen,
 * which is the actual job: "upload" and "type it in" are not self-evidently
 * different until you know that one of them reads the document for you.
 *
 * Behaviour matches FormDialog deliberately — escape closes, the backdrop
 * closes only on a press that both starts and ends there, focus returns
 * where it came from, and closing plays its exit animation first.
 */
export function ChoiceDialog({
  title,
  description,
  options,
  onCancel,
}: {
  title: string;
  description?: string;
  options: Array<{
    key: string;
    label: string;
    description: string;
    icon: LucideIcon;
    onSelect: () => void;
    /** Marks the option most people want. Rendered with the accent. */
    recommended?: boolean;
  }>;
  onCancel: () => void;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const previouslyFocused = useRef<HTMLElement | null>(null);
  const pressedOnBackdrop = useRef(false);
  const { isClosing, requestClose } = useExitAnimation(onCancel);

  // Mount-only, like FormDialog's: the component is mounted per opening, so
  // this runs once per opening, and keying it on a callback the caller
  // declares inline would re-run it on every render.
  useEffect(() => {
    previouslyFocused.current = document.activeElement as HTMLElement | null;

    panelRef.current?.querySelector<HTMLElement>("button[data-option]")?.focus();

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

        <div className="space-y-2.5 px-5 py-4">
          {options.map(({ key, label, description: optionDescription, icon: Icon, onSelect, recommended }) => (
            <button
              key={key}
              type="button"
              data-option
              onClick={onSelect}
              className={cx(
                "group flex w-full items-start gap-3 rounded-surface border p-3.5 text-left",
                "transition-[border-color,background-color,box-shadow] duration-100",
                "hover:border-accent hover:bg-accent-tint/40 hover:shadow-raised",
                recommended ? "border-line-strong" : "border-line",
              )}
            >
              <span
                className={cx(
                  "grid size-9 shrink-0 place-items-center rounded-control",
                  "bg-surface-sunken text-ink-secondary transition-colors",
                  "group-hover:bg-accent group-hover:text-white",
                )}
                aria-hidden="true"
              >
                <Icon className="size-[18px]" />
              </span>

              <span className="min-w-0">
                <span className="flex items-center gap-2">
                  <span className="text-[13.5px] font-semibold">{label}</span>
                  {recommended ? (
                    <span
                      className="rounded-full bg-accent-tint px-1.5 py-[1px] text-[10px]
                                 font-semibold uppercase tracking-wide text-accent"
                    >
                      Fastest
                    </span>
                  ) : null}
                </span>
                <span className="mt-0.5 block text-[12.5px] leading-relaxed text-ink-secondary">
                  {optionDescription}
                </span>
              </span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
