"use client";

import { useId, useState } from "react";
import { ChevronRight } from "lucide-react";

import { cx } from "@/components/ui/primitives";

/**
 * A labelled section that opens and closes.
 *
 * <h3>Why this is not a &lt;details&gt; element</h3>
 *
 * It was. A native disclosure cannot be animated in any browser this has to
 * work in: the content has no height to transition from, so it snaps open and
 * the page jumps by however tall the contents happen to be. That jump is what
 * makes an expanding panel feel like a layout bug rather than a response.
 *
 * <h3>How it animates without measuring anything</h3>
 *
 * The body is a one-row grid whose row is <code>0fr</code> closed and
 * <code>1fr</code> open, with the content clipped inside it. Those two are
 * interpolatable, so the browser animates the real height — no
 * <code>ResizeObserver</code>, no hard-coded max-height that silently cuts
 * long content off, and it stays correct when the content reflows at a
 * different width.
 *
 * Closed content is marked <code>inert</code>, so a keyboard user cannot tab
 * into a section they cannot see.
 */
export function Disclosure({
  label,
  children,
  defaultOpen = false,
}: {
  label: string;
  children: React.ReactNode;
  defaultOpen?: boolean;
}) {
  const [isOpen, setIsOpen] = useState(defaultOpen);
  const bodyId = useId();

  return (
    <div className="rounded-control border border-line bg-surface-subtle">
      <button
        type="button"
        aria-expanded={isOpen}
        aria-controls={bodyId}
        onClick={() => setIsOpen((open) => !open)}
        className="flex w-full items-center gap-1.5 rounded-control px-3 py-2
                   text-left text-[12.5px] font-medium transition-colors
                   hover:text-accent"
      >
        <ChevronRight
          className={cx(
            "size-3.5 shrink-0 text-ink-muted transition-transform",
            "motion-safe:duration-200 motion-safe:ease-out",
            isOpen && "rotate-90",
          )}
          aria-hidden="true"
        />
        {label}
      </button>

      <div
        id={bodyId}
        inert={!isOpen}
        className={cx(
          "grid transition-[grid-template-rows]",
          "motion-safe:duration-200 motion-safe:ease-out",
          isOpen ? "grid-rows-[1fr]" : "grid-rows-[0fr]",
        )}
      >
        <div className="overflow-hidden">
          <div className="px-3 pb-2.5">{children}</div>
        </div>
      </div>
    </div>
  );
}
