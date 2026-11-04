"use client";

import { useRef } from "react";
import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import {
  AlertTriangle,
  CalendarClock,
  GitBranch,
  MessageSquare,
  Paperclip,
} from "lucide-react";

import { AvatarStack, cx, Priority, Status } from "@/components/ui/primitives";
import {
  AssigneePicker,
  type Assignable,
} from "@/components/kanban/assignee-picker";
import {
  categoryLabel,
  dueDateLabel,
  statusLabel,
  type TaskCard,
} from "@/lib/maintenance";

/**
 * One card on the board.
 *
 * <h3>What is on a card, and what is deliberately not</h3>
 *
 * A board is read by scanning, so a card shows only what distinguishes it from
 * its neighbours: what it is, where, how urgent, when it is due, and who has
 * it. The description, the cost, the activity timeline, and the full comment
 * thread are all on the detail view.
 *
 * The temptation is to add one more field "because it is useful". Four cards
 * of six lines each are read at a glance; four cards of twelve lines are read
 * one at a time, which defeats the purpose of a board.
 *
 * <h3>Overdue comes from the server</h3>
 *
 * `isOverdue` and `daysUntilDue` are computed server-side in the property's own
 * time zone. The browser could not do it correctly: it knows only the viewer's
 * zone, and a task on a Los Angeles property is not overdue because it has
 * gone midnight in London.
 */
export function KanbanCard({
  card,
  onOpen,
  isDragOverlay = false,
  organizationId,
  assignable = [],
}: {
  card: TaskCard;
  onOpen: (taskId: string) => void;
  isDragOverlay?: boolean;
  /** Absent on the drag overlay, which is not interactive. */
  organizationId?: string;
  assignable?: Assignable[];
}) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: card.id,
    data: { type: "task", card },
  });

  const due = dueDateLabel(card);

  // Where the pointer went down, so a click can be told from the end of a
  // drag. Null between interactions.
  const pressOrigin = useRef<{ x: number; y: number } | null>(null);

  function handlePointerDown(event: React.PointerEvent<HTMLElement>) {
    pressOrigin.current = { x: event.clientX, y: event.clientY };

    // Composed rather than replaced: dnd-kit's own pointerdown is what starts
    // a drag, and dropping it would leave the card immovable.
    listeners?.onPointerDown?.(event);
  }

  function handleClick(event: React.MouseEvent<HTMLElement>) {
    const origin = pressOrigin.current;

    pressOrigin.current = null;

    // Matches the sensor's activation distance. Anything further was a drag,
    // and a drag is not a request to open anything.
    if (origin) {
      const travelled = Math.hypot(
        event.clientX - origin.x,
        event.clientY - origin.y,
      );

      if (travelled > 6) {
        return;
      }
    }

    onOpen(card.id);
  }

  const style = {
    transform: CSS.Translate.toString(transform),
    transition,
  };

  // The card left behind while its copy follows the cursor. Rendered as an
  // outline rather than hidden, so the column does not collapse and reflow
  // under the pointer mid-drag.
  if (isDragging && !isDragOverlay) {
    return (
      <li
        ref={setNodeRef}
        style={style}
        className="drop-placeholder h-[88px] list-none"
        aria-hidden="true"
      />
    );
  }

  return (
    <li
      ref={isDragOverlay ? undefined : setNodeRef}
      style={isDragOverlay ? undefined : style}
      className={cx("list-none", isDragOverlay && "card-dragging")}
    >
      <article
        {...(isDragOverlay ? {} : attributes)}
        {...(isDragOverlay ? {} : listeners)}
        {...(isDragOverlay
          ? {}
          : { onPointerDown: handlePointerDown, onClick: handleClick })}
        className={cx(
          "group relative rounded-control border bg-surface px-3 py-2.5 text-left",
          "shadow-raised transition-[border-color,box-shadow] duration-100",
          // Not selectable. Dragging a card across a board otherwise paints a
          // blue selection through its text, and a double-click selects the
          // title instead of opening the task.
          "select-none",
          isDragOverlay ? "cursor-grabbing" : "cursor-grab",
          !isDragOverlay && "hover:border-line-strong hover:shadow-floating",
          // An overdue card gets a left edge rather than a red background: a
          // filled card competes with the priority chip and makes the text
          // harder to read.
          card.isOverdue
            ? "border-line border-l-[3px] border-l-danger"
            : card.priority === "urgent"
              ? "border-line border-l-[3px] border-l-priority-urgent"
              : "border-line",
        )}
      >
        <div className="relative">
          <header className="flex items-start justify-between gap-2">
            {/* Focusable so a keyboard user can reach the task. Enter fires
                a click, which bubbles to the handler on the card — so this
                deliberately has no onClick of its own. */}
            <button type="button" className="min-w-0 flex-1 text-left">
              <span className="flex items-baseline gap-1.5">
                <span className="tabular text-[11px] font-semibold text-ink-muted">
                  #{card.referenceNumber}
                </span>
                {card.hasSubtasks ? (
                  <GitBranch
                    className="size-3 shrink-0 text-ink-muted"
                    aria-label="Has subtasks"
                  />
                ) : null}
              </span>
              <h3 className="mt-0.5 line-clamp-2 text-[13.5px] font-medium leading-snug">
                {card.title}
              </h3>
            </button>

            <Priority priority={card.priority} />
          </header>

          <p className="mt-1.5 truncate text-[12px] text-ink-secondary">
            {card.propertyName}
            {card.unitNumber ? (
              <span className="text-ink-muted"> · Unit {card.unitNumber}</span>
            ) : null}
          </p>

          <footer className="mt-2.5 flex items-center justify-between gap-2">
            <div className="flex min-w-0 items-center gap-2">
              {due ? (
                <span
                  className={cx(
                    "inline-flex items-center gap-1 whitespace-nowrap text-[11.5px] font-medium",
                    due.tone === "danger"
                      ? "text-danger-text"
                      : due.tone === "warning"
                        ? "text-warning-text"
                        : "text-ink-secondary",
                  )}
                >
                  {due.tone === "danger" ? (
                    <AlertTriangle className="size-3" aria-hidden="true" />
                  ) : (
                    <CalendarClock className="size-3" aria-hidden="true" />
                  )}
                  {due.text}
                </span>
              ) : (
                <span className="truncate text-[11.5px] text-ink-muted">
                  {categoryLabel(card.category)}
                </span>
              )}

              {card.commentCount > 0 ? (
                <span className="inline-flex items-center gap-0.5 text-[11.5px] text-ink-muted">
                  <MessageSquare className="size-3" aria-hidden="true" />
                  <span className="tabular">{card.commentCount}</span>
                  <span className="sr-only">comments</span>
                </span>
              ) : null}

              {card.attachmentCount > 0 ? (
                <span className="inline-flex items-center gap-0.5 text-[11.5px] text-ink-muted">
                  <Paperclip className="size-3" aria-hidden="true" />
                  <span className="tabular">{card.attachmentCount}</span>
                  <span className="sr-only">attachments</span>
                </span>
              ) : null}
            </div>

            {/* Unassigned is a meaningful state on a board, so AvatarStack
                renders a labelled placeholder rather than nothing.

                It is also the control for changing who has the task. The
                picker stops its own pointer and click events, so reaching for
                it does not open the card underneath. */}
            {organizationId && !isDragOverlay ? (
              <AssigneePicker
                organizationId={organizationId}
                taskId={card.id}
                // Matched by name, which is all a card carries. A false match
                // only disables an assignment that the database treats as a
                // no-op anyway, so the failure mode is harmless — and the
                // drawer, which has the ids, is exact.
                assigned={assignable
                  .filter((candidate) =>
                    card.assigneeNames.includes(candidate.name))
                  .map((candidate) => ({
                    userId: candidate.userId,
                    vendorId: candidate.vendorId,
                  }))}
                candidates={assignable}
                align="right"
              >
                <AvatarStack names={card.assigneeNames} max={2} />
              </AssigneePicker>
            ) : (
              <AvatarStack names={card.assigneeNames} max={2} />
            )}
          </footer>
        </div>
      </article>
    </li>
  );
}

/** The card rendered under the cursor during a drag. */
export function KanbanCardOverlay({ card }: { card: TaskCard }) {
  return (
    <ul className="w-[272px]">
      <KanbanCard card={card} onOpen={() => undefined} isDragOverlay />
    </ul>
  );
}

/**
 * A compact row for the list view.
 *
 * <p>
 * Offered alongside the board because a board is the wrong tool for some
 * questions. "What is overdue across every property, sorted by how late it
 * is?" is a list; dragging has nothing to do with it.
 * </p>
 */
export function TaskRow({
  card,
  onOpen,
}: {
  card: TaskCard;
  onOpen: (taskId: string) => void;
}) {
  const due = dueDateLabel(card);

  return (
    <tr className="transition-colors hover:bg-surface-subtle">
      <td className="border-b border-line px-4 py-2.5">
        <button
          type="button"
          onClick={() => onOpen(card.id)}
          className="text-left hover:text-accent"
        >
          <span className="tabular mr-1.5 text-[11.5px] font-semibold text-ink-muted">
            #{card.referenceNumber}
          </span>
          <span className="text-[13.5px] font-medium">{card.title}</span>
        </button>
      </td>

      <td className="border-b border-line px-4 py-2.5 text-[13px] text-ink-secondary">
        {card.propertyName}
        {card.unitNumber ? ` · ${card.unitNumber}` : ""}
      </td>

      <td className="border-b border-line px-4 py-2.5">
        <Priority priority={card.priority} alwaysShow />
      </td>

      <td className="border-b border-line px-4 py-2.5 text-[13px]">
        {due ? (
          <span
            className={
              due.tone === "danger"
                ? "font-medium text-danger-text"
                : due.tone === "warning"
                  ? "text-warning-text"
                  : "text-ink-secondary"
            }
          >
            {due.text}
          </span>
        ) : (
          <span className="text-ink-muted">No due date</span>
        )}
      </td>

      <td className="border-b border-line px-4 py-2.5">
        <AvatarStack names={card.assigneeNames} max={3} />
      </td>

      <td className="border-b border-line px-4 py-2.5">
        <Status tone={card.isOverdue ? "danger" : "neutral"}>
          {statusLabel(card.statusCategory)}
        </Status>
      </td>
    </tr>
  );
}
