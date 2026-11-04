"use client";

import { useCallback, useEffect, useRef, useState, useTransition } from "react";
import { createPortal } from "react-dom";
import { useRouter } from "next/navigation";
import { Check, Plus, UserPlus } from "lucide-react";

import { cx } from "@/components/ui/primitives";

import { assignTaskAction } from "@/app/maintenance/actions";

export interface Assignable {
  /** One of these is set, never both. */
  userId: string | null;
  vendorId: string | null;
  name: string;
  /** Shown under the name: an email, or a vendor's trade. */
  detail: string | null;
}

/**
 * Who a task belongs to.
 *
 * <h3>Why members and vendors are one list</h3>
 *
 * From the point of view of "who is dealing with this", a plumber from outside
 * and a property manager inside are the same kind of answer. The database
 * keeps them apart — a task is assigned to exactly one of a user or a vendor,
 * and the constraint enforces it — but making somebody choose which *kind* of
 * person they want before they can look for a name is a question about the
 * schema, not about the work.
 *
 * So one list, grouped, and the picker sends whichever id applies.
 *
 * <h3>Why the first assignee becomes the lead</h3>
 *
 * Somebody has to be the one who answers for it. The API takes an isLead flag
 * and the first person assigned gets it, because a task whose first assignee
 * is not the lead is a task with no lead at all until somebody notices.
 */
export function AssigneePicker({
  organizationId,
  taskId,
  assigned,
  candidates,
  align = "left",
  onAssigned,
  children,
}: {
  organizationId: string;
  taskId: string;
  /** Who is already on the task, so they can be shown as ticked. */
  assigned: Array<{ userId: string | null; vendorId: string | null }>;
  candidates: Assignable[];
  align?: "left" | "right";
  /**
   * Called after a successful assignment, for a caller holding a fetched copy
   * of the task that router.refresh() cannot reach.
   */
  onAssigned?: () => void;
  /** The trigger. Given no children, a default button is rendered. */
  children?: React.ReactNode;
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const [position, setPosition] = useState<{
    left: number;
    top: number;
    placement: "below" | "above";
  } | null>(null);

  // Measured from the trigger's own geometry, because a portalled menu is
  // positioned in viewport coordinates and cannot inherit anything.
  const measure = useCallback(() => {
    const trigger = triggerRef.current;

    if (!trigger) {
      return;
    }

    const rect = trigger.getBoundingClientRect();
    const menuHeight = 320;
    const menuWidth = 256;
    const gap = 6;

    // Flipped above when there is not room below, so the menu is never half
    // off the bottom of the window.
    const roomBelow = window.innerHeight - rect.bottom;
    const placement =
      roomBelow < menuHeight && rect.top > roomBelow ? "above" : "below";

    // Aligned to the trigger, then pulled back inside the viewport if that
    // would overflow it.
    const preferredLeft =
      align === "right" ? rect.right - menuWidth : rect.left;

    setPosition({
      left: Math.max(8, Math.min(preferredLeft, window.innerWidth - menuWidth - 8)),
      top: placement === "below" ? rect.bottom + gap : rect.top - gap,
      placement,
    });
  }, [align]);

  // Only re-measuring here. The first measurement happens in the click that
  // opens the menu, where the geometry is already settled — measuring in an
  // effect instead would be a second render pass doing the first one's work.
  useEffect(() => {
    if (!isOpen) {
      return;
    }

    window.addEventListener("scroll", measure, true);
    window.addEventListener("resize", measure);

    return () => {
      window.removeEventListener("scroll", measure, true);
      window.removeEventListener("resize", measure);
    };
  }, [isOpen, measure]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    function onPointerDown(event: MouseEvent) {
      const target = event.target as Node;

      // The menu is in a portal, so it is not inside containerRef any more.
      // Without the role check, clicking the menu would close it.
      const insideMenu = (target as HTMLElement).closest?.('[role="menu"]');

      if (!containerRef.current?.contains(target) && !insideMenu) {
        setIsOpen(false);
      }
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setIsOpen(false);
      }
    }

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);

    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [isOpen]);

  const isAssigned = (candidate: Assignable) =>
    assigned.some(
      (existing) =>
        (candidate.userId !== null && existing.userId === candidate.userId)
        || (candidate.vendorId !== null && existing.vendorId === candidate.vendorId),
    );

  function assign(candidate: Assignable) {
    setError(null);

    startTransition(async () => {
      const result = await assignTaskAction(organizationId, taskId, {
        userId: candidate.userId,
        vendorId: candidate.vendorId,
        // The first person on the task answers for it.
        isLead: assigned.length === 0,
      });

      if (!result.ok) {
        setError(result.error ?? "That person could not be assigned.");

        return;
      }

      setIsOpen(false);
      router.refresh();
      onAssigned?.();
    });
  }

  const members = candidates.filter((candidate) => candidate.userId !== null);
  const vendors = candidates.filter((candidate) => candidate.vendorId !== null);

  return (
    <div ref={containerRef} className="relative inline-flex">
      <button
        ref={triggerRef}
        type="button"
        aria-expanded={isOpen}
        aria-haspopup="true"
        // Stopped here, not bubbled: on a card the parent opens the task, and
        // reaching for the assignee control should not do that as well.
        onPointerDown={(event) => event.stopPropagation()}
        onClick={(event) => {
          event.stopPropagation();

          if (isOpen) {
            setIsOpen(false);

            return;
          }

          // Measured before opening, so the menu's first paint is already in
          // the right place.
          measure();
          setIsOpen(true);
        }}
        className={
          children
            ? "cursor-pointer"
            : `inline-flex items-center gap-1 rounded-control border border-dashed
               border-line-strong px-2 py-1 text-[12px] text-ink-secondary
               transition-colors hover:border-accent hover:text-accent`
        }
      >
        {children ?? (
          <>
            <UserPlus className="size-3.5" aria-hidden="true" />
            Assign
          </>
        )}
      </button>

      {isOpen && position
        ? createPortal(
        <div
          role="menu"
          onPointerDown={(event) => event.stopPropagation()}
          onClick={(event) => event.stopPropagation()}
          style={{
            left: position.left,
            top: position.placement === "below" ? position.top : undefined,
            bottom:
              position.placement === "above"
                ? window.innerHeight - position.top
                : undefined,
          }}
          className={cx(
            `fixed z-50 w-64 overflow-hidden rounded-surface border border-line
             bg-surface shadow-floating
             motion-safe:animate-[dialog-in_150ms_cubic-bezier(0.16,1,0.3,1)]`,
          )}
        >
          {error ? (
            <p
              role="alert"
              className="border-b border-line bg-danger/10 px-3 py-2 text-[12px] text-danger-text"
            >
              {error}
            </p>
          ) : null}

          <div className="thin-scrollbar max-h-72 overflow-y-auto p-1">
            {candidates.length === 0 ? (
              <p className="px-2.5 py-3 text-center text-[12.5px] leading-relaxed text-ink-secondary">
                Nobody to assign yet. Invite someone to the workspace, or add a
                vendor.
              </p>
            ) : null}

            {members.length > 0 ? (
              <Group label="Workspace">
                {members.map((candidate) => (
                  <Option
                    key={candidate.userId}
                    candidate={candidate}
                    isAssigned={isAssigned(candidate)}
                    isPending={isPending}
                    onSelect={() => assign(candidate)}
                  />
                ))}
              </Group>
            ) : null}

            {vendors.length > 0 ? (
              <Group label="Vendors">
                {vendors.map((candidate) => (
                  <Option
                    key={candidate.vendorId}
                    candidate={candidate}
                    isAssigned={isAssigned(candidate)}
                    isPending={isPending}
                    onSelect={() => assign(candidate)}
                  />
                ))}
              </Group>
            ) : null}
          </div>
        </div>,
        document.body,
      )
        : null}
    </div>
  );
}

function Group({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="mb-1 last:mb-0">
      <p className="px-2.5 py-1 text-[10.5px] font-semibold tracking-wide text-ink-muted uppercase">
        {label}
      </p>
      <ul>{children}</ul>
    </div>
  );
}

function Option({
  candidate,
  isAssigned,
  isPending,
  onSelect,
}: {
  candidate: Assignable;
  isAssigned: boolean;
  isPending: boolean;
  onSelect: () => void;
}) {
  return (
    <li>
      <button
        type="button"
        role="menuitem"
        // Already on the task, so there is nothing to do. Shown ticked rather
        // than hidden, which is what makes the list an answer to "who has
        // this?" as well as a way to change it.
        disabled={isAssigned || isPending}
        onClick={onSelect}
        className={cx(
          "flex w-full items-center gap-2 rounded-control px-2.5 py-1.5 text-left",
          "text-[13px] transition-colors",
          isAssigned
            ? "text-ink-secondary"
            : "hover:bg-surface-subtle disabled:opacity-50",
        )}
      >
        <span className="flex size-3.5 shrink-0 items-center justify-center">
          {isAssigned ? (
            <Check className="size-3.5 text-success" aria-hidden="true" />
          ) : (
            <Plus className="size-3 text-ink-muted" aria-hidden="true" />
          )}
        </span>

        <span className="min-w-0 flex-1">
          <span className="block truncate">{candidate.name}</span>
          {candidate.detail ? (
            <span className="block truncate text-[11px] text-ink-muted">
              {candidate.detail}
            </span>
          ) : null}
        </span>
      </button>
    </li>
  );
}
