"use client";

import { useCallback, useMemo, useState, useTransition } from "react";
import {
  DndContext,
  DragOverlay,
  KeyboardSensor,
  PointerSensor,
  closestCorners,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from "@dnd-kit/core";
import { SortableContext, sortableKeyboardCoordinates, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { AlertTriangle, ChevronDown, ChevronRight } from "lucide-react";

import { KanbanCard, KanbanCardOverlay } from "@/components/kanban/task-card";
import type { Assignable } from "@/components/kanban/assignee-picker";
import { cx, EmptyState } from "@/components/ui/primitives";
import { moveTaskAction } from "@/app/maintenance/actions";
import type { Board, BoardColumn, TaskCard } from "@/lib/maintenance";

/**
 * The Kanban board.
 *
 * <h3>Optimistic moves, and why the server still decides</h3>
 *
 * A drag has to look instant, so the card moves in local state the moment it
 * is dropped and the server call runs behind it. If the call fails, the state
 * is rolled back and the reason is shown.
 *
 * What is NOT sent is a position. The client sends the two neighbouring card
 * ids and the server recomputes the fractional position from current truth.
 * That difference matters when two people are working the same board: both
 * would compute the same midpoint from the same stale view and one would
 * silently land in the wrong place. Neighbours are stable identifiers, so the
 * two drags merge instead.
 *
 * <h3>Keyboard</h3>
 *
 * dnd-kit's keyboard sensor is wired up, so a card can be picked up with space
 * and moved with the arrow keys. A board that can only be operated by dragging
 * is unusable for anybody who does not use a mouse, and "it is a board, it has
 * to be dragged" is not a reason.
 */
export function KanbanBoard({
  board,
  organizationId,
  onOpenTask,
  assignable,
}: {
  board: Board;
  organizationId: string;
  onOpenTask: (taskId: string) => void;
  /** Everyone a task can be given to, for the avatar picker on each card. */
  assignable: Assignable[];
}) {
  // The authoritative local copy. Seeded from the server render and then owned
  // by this component for the lifetime of the page.
  const [columns, setColumns] = useState<BoardColumn[]>(board.columns);
  const [activeCard, setActiveCard] = useState<TaskCard | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(
    () => new Set(board.columns.filter((c) => c.collapsedByDefault).map((c) => c.id)),
  );
  const [, startTransition] = useTransition();

  // The last server payload this component adopted. Compared by reference:
  // every server render produces a new array, so a change of identity is
  // exactly "there is newer data".
  //
  // Adopted during render rather than in an effect. This is derived state —
  // "the local copy should follow the server's, except mid-drag" — and
  // deriving it here means the correct columns are rendered on the first pass
  // instead of the second.
  const [adopted, setAdopted] = useState(board.columns);

  if (board.columns !== adopted && activeCard === null) {
    // Mid-drag the local copy is further ahead than the server's, and
    // overwriting it would snatch the card back from under the cursor. The
    // drop's own refresh brings the next payload.
    setAdopted(board.columns);
    setColumns(board.columns);
  }

  const sensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        // 6px before a drag starts, so a click on the card's title opens the
        // task instead of beginning a one-pixel drag. Without this, every
        // attempt to open a card is a failed drag.
        distance: 6,
      },
    }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
    }),
  );

  const cardsById = useMemo(() => {
    const map = new Map<string, TaskCard>();

    for (const column of columns) {
      for (const card of column.tasks) {
        map.set(card.id, card);
      }
    }

    return map;
  }, [columns]);

  const handleDragStart = useCallback(
    (event: DragStartEvent) => {
      setError(null);
      setActiveCard(cardsById.get(String(event.active.id)) ?? null);
    },
    [cardsById],
  );

  const handleDragEnd = useCallback(
    (event: DragEndEvent) => {
      const { active, over } = event;

      setActiveCard(null);

      if (!over) {
        return;
      }

      const taskId = String(active.id);
      const card = cardsById.get(taskId);

      if (!card) {
        return;
      }

      // `over` is either a card (dropped onto a position in a column) or a
      // column (dropped into empty space below the cards).
      const overId = String(over.id);
      const overCard = cardsById.get(overId);

      const targetColumnId = overCard?.taskColumnId ?? overId;
      const targetColumn = columns.find((column) => column.id === targetColumnId);

      if (!targetColumn) {
        return;
      }

      // Work out the neighbours in the TARGET column, excluding the card being
      // moved so that a within-column drag does not treat the card as its own
      // neighbour.
      const siblings = targetColumn.tasks.filter((sibling) => sibling.id !== taskId);

      const dropIndex = overCard
        ? siblings.findIndex((sibling) => sibling.id === overCard.id)
        : siblings.length;

      const insertAt = dropIndex === -1 ? siblings.length : dropIndex;

      const previousTaskId = insertAt > 0 ? (siblings[insertAt - 1]?.id ?? null) : null;
      const nextTaskId = siblings[insertAt]?.id ?? null;

      // Nothing changed: same column, same neighbours. Returning early keeps a
      // click-that-moved-slightly from raising a status change event.
      if (
        card.taskColumnId === targetColumnId &&
        previousTaskId === findPreviousSibling(targetColumn.tasks, taskId) &&
        nextTaskId === findNextSibling(targetColumn.tasks, taskId)
      ) {
        return;
      }

      const snapshot = columns;

      // Optimistic: move it now, reconcile later.
      setColumns((current) =>
        moveCardLocally(current, taskId, targetColumnId, insertAt),
      );

      startTransition(async () => {
        const result = await moveTaskAction(
          organizationId,
          taskId,
          targetColumnId,
          previousTaskId,
          nextTaskId,
        );

        if (!result.ok) {
          // Roll back to exactly what was on screen before the drag. Leaving
          // the card where it was dropped would show a state the server has
          // rejected, and the next refresh would silently undo it.
          setColumns(snapshot);
          setError(result.error ?? "The task could not be moved.");
        }
      });
    },
    [cardsById, columns, organizationId],
  );

  const toggleCollapsed = useCallback((columnId: string) => {
    setCollapsed((current) => {
      const next = new Set(current);

      if (next.has(columnId)) {
        next.delete(columnId);
      } else {
        next.add(columnId);
      }

      return next;
    });
  }, []);

  return (
    <div>
      {error ? (
        <div
          role="alert"
          className="mb-3 flex items-start gap-2 rounded-control border border-danger/25
                     bg-danger-tint px-3 py-2 text-[13px] text-danger-text"
        >
          <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          <span>{error}</span>
          <button
            type="button"
            onClick={() => setError(null)}
            className="ml-auto shrink-0 font-medium underline"
          >
            Dismiss
          </button>
        </div>
      ) : null}

      <DndContext
        // A stable id, rather than the generated one.
        //
        // Without it dnd-kit derives the id from an internal counter, which
        // starts fresh in each rendering environment. The server emits
        // aria-describedby="DndDescribedBy-0" and the client, having already
        // incremented its own counter, expects DndDescribedBy-1. React reports
        // that as a hydration mismatch and, as the warning says, will not
        // patch an attribute mismatch up: the card keeps the server value,
        // which points at no element, so a screen reader announces nothing
        // where the drag instructions should be.
        id="maintenance-board"
        sensors={sensors}
        // closestCorners rather than closestCenter: with tall cards and short
        // columns, centre-based collision makes the last position in a column
        // almost impossible to hit.
        collisionDetection={closestCorners}
        onDragStart={handleDragStart}
        onDragEnd={handleDragEnd}
        onDragCancel={() => setActiveCard(null)}
      >
        {/*
          Every column visible at once, sharing the width between them, at any
          zoom level. The columns themselves flex (see BoardColumnView); this
          row only has to stop stretching them and allow the sideways scroll
          that takes over once they hit their minimum width.

          Each column keeps its own vertical scroll, and the page body never
          scrolls sideways, which section 5 requires.
        */}
        <div className="thin-scrollbar -mx-1 flex items-start gap-3 overflow-x-auto px-1 pb-3">
          {columns.map((column) => (
            <BoardColumnView
              key={column.id}
              column={column}
              isCollapsed={collapsed.has(column.id)}
              onToggleCollapsed={toggleCollapsed}
              onOpenTask={onOpenTask}
              hiddenCompletedCount={board.hiddenCompletedCount}
              organizationId={organizationId}
              assignable={assignable}
            />
          ))}
        </div>

        {/*
          The dragged card follows the cursor in an overlay rather than being
          repositioned in the list. Inside the list it would be clipped by the
          column's own overflow as soon as it left the column.
        */}
        <DragOverlay dropAnimation={null}>
          {activeCard ? <KanbanCardOverlay card={activeCard} /> : null}
        </DragOverlay>
      </DndContext>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Column                                                                      */
/* -------------------------------------------------------------------------- */

function BoardColumnView({
  column,
  isCollapsed,
  onToggleCollapsed,
  onOpenTask,
  hiddenCompletedCount,
  organizationId,
  assignable,
}: {
  column: BoardColumn;
  isCollapsed: boolean;
  onToggleCollapsed: (columnId: string) => void;
  onOpenTask: (taskId: string) => void;
  /** Settled cards the board is not showing. Reported under the Done column. */
  hiddenCompletedCount: number;
  organizationId: string;
  assignable: Assignable[];
}) {
  // The column itself is a drop target, so a card can be dropped into the
  // empty space below the last card rather than only onto another card.
  const { setNodeRef, isOver } = useDroppable({
    id: column.id,
    data: { type: "column", column },
  });

  const overLimit =
    column.workInProgressLimit != null &&
    column.tasks.length > column.workInProgressLimit;

  if (isCollapsed) {
    return (
      <div className="w-11 shrink-0">
        <button
          type="button"
          onClick={() => onToggleCollapsed(column.id)}
          className="surface flex h-full w-11 flex-col items-center gap-3 py-3
                     transition-colors hover:border-line-strong"
          aria-expanded="false"
        >
          <ChevronRight className="size-4 shrink-0 text-ink-muted" aria-hidden="true" />
          <span
            className="tabular rounded bg-surface-sunken px-1 text-[11px]
                       font-semibold text-ink-secondary"
          >
            {column.tasks.length}
          </span>
          {/* Rotated so a collapsed column still says which one it is. */}
          <span
            className="whitespace-nowrap text-[12px] font-medium text-ink-secondary"
            style={{ writingMode: "vertical-rl" }}
          >
            {column.name}
          </span>
        </button>
      </div>
    );
  }

  return (
    <section
      className={cx(
        // Columns share the width rather than each taking a fixed 288px.
        //
        // basis-0 with flex-1 is what makes the share equal: without it the
        // content sets the basis and a column holding a long task title ends
        // up wider than its neighbours. min-w-0 lets a column narrow past its
        // content, which is what allows five of them to fit at all.
        //
        // The floor is 196px, below which a card's title wraps to four lines
        // and the board stops being scannable. Past that the row scrolls
        // sideways instead of squeezing further, which is the better failure
        // on a phone.
        "flex min-w-[196px] flex-1 basis-0 flex-col rounded-surface border bg-surface-sunken/60",
        isOver ? "column-active" : "border-line",
      )}
      aria-label={`${column.name}, ${column.tasks.length} tasks`}
    >
      <header className="flex items-center gap-2 px-3 py-2.5">
        <button
          type="button"
          onClick={() => onToggleCollapsed(column.id)}
          className="flex min-w-0 flex-1 items-center gap-1.5 text-left"
          aria-expanded="true"
        >
          <ChevronDown className="size-3.5 shrink-0 text-ink-muted" aria-hidden="true" />

          {/* A colour bar rather than coloured text: the column name has to
              stay readable, and a 2px bar is enough to group at a glance. */}
          {column.colour ? (
            <span
              className="h-3.5 w-[3px] shrink-0 rounded-full"
              style={{ backgroundColor: column.colour }}
              aria-hidden="true"
            />
          ) : null}

          <h2 className="truncate text-[13px] font-semibold">{column.name}</h2>

          <span className="tabular shrink-0 text-[12px] font-medium text-ink-muted">
            {column.tasks.length}
          </span>
        </button>

        {column.overdueCount > 0 ? (
          <span
            className="tabular shrink-0 rounded bg-danger-tint px-1.5 text-[11px]
                       font-semibold text-danger-text"
            title={`${column.overdueCount} overdue`}
          >
            {column.overdueCount} late
          </span>
        ) : null}
      </header>

      {/*
        The work-in-progress limit is advisory. It warns rather than refusing,
        because blocking an urgent repair from entering a full column would be
        worse than the overload it prevents.
      */}
      {overLimit ? (
        <p
          className="mx-3 mb-2 rounded border border-warning/25 bg-warning-tint px-2 py-1
                     text-[11.5px] text-warning-text"
        >
          Over the {column.workInProgressLimit} task limit for this column.
        </p>
      ) : null}

      <div
        ref={setNodeRef}
        // A generous minimum height, so an empty column is still a visible
        // drop target rather than a 20px sliver.
        className="thin-scrollbar min-h-[140px] flex-1 overflow-y-auto px-2 pb-2"
      >
        <SortableContext
          items={column.tasks.map((task) => task.id)}
          strategy={verticalListSortingStrategy}
        >
          <ul className="flex flex-col gap-2">
            {column.tasks.map((card) => (
              <KanbanCard
                key={card.id}
                card={card}
                onOpen={onOpenTask}
                organizationId={organizationId}
                assignable={assignable}
              />
            ))}
          </ul>
        </SortableContext>

        {column.tasks.length === 0 ? (
          <p className="px-2 py-6 text-center text-[12px] text-ink-muted">
            Drop a task here
          </p>
        ) : null}

        {/* Only under a column that settles work, and only when something is
            actually hidden. A note on every column would be noise. */}
        {hiddenCompletedCount > 0
        && (column.category === "completed" || column.category === "cancelled") ? (
          <p className="px-2 pt-2 text-center text-[11.5px] leading-relaxed text-ink-muted">
            {hiddenCompletedCount} finished more than a fortnight ago{" "}
            {hiddenCompletedCount === 1 ? "is" : "are"} not shown.
          </p>
        ) : null}
      </div>
    </section>
  );
}

/* -------------------------------------------------------------------------- */
/* Local reordering                                                            */
/* -------------------------------------------------------------------------- */

/**
 * Moves a card between columns in local state.
 *
 * <p>
 * Purely presentational. The server recomputes the real fractional position
 * from the neighbour ids, so this only has to produce the order the user is
 * about to see; the next server render replaces it with the authoritative one.
 * </p>
 */
function moveCardLocally(
  columns: BoardColumn[],
  taskId: string,
  targetColumnId: string,
  insertAt: number,
): BoardColumn[] {
  let moved: TaskCard | undefined;

  const withoutCard = columns.map((column) => {
    const index = column.tasks.findIndex((task) => task.id === taskId);

    if (index === -1) {
      return column;
    }

    moved = column.tasks[index];

    return { ...column, tasks: column.tasks.filter((task) => task.id !== taskId) };
  });

  if (!moved) {
    return columns;
  }

  const card = moved;

  return withoutCard.map((column) => {
    if (column.id !== targetColumnId) {
      return column;
    }

    const tasks = [...column.tasks];

    tasks.splice(Math.min(insertAt, tasks.length), 0, {
      ...card,
      taskColumnId: targetColumnId,
      // The status category follows the column, as the database trigger will
      // also do. Without this the card would keep its old status chip until
      // the next refresh.
      statusCategory: column.category,
    });

    return { ...column, tasks };
  });
}

function findPreviousSibling(tasks: TaskCard[], taskId: string): string | null {
  const index = tasks.findIndex((task) => task.id === taskId);

  return index > 0 ? (tasks[index - 1]?.id ?? null) : null;
}

function findNextSibling(tasks: TaskCard[], taskId: string): string | null {
  const index = tasks.findIndex((task) => task.id === taskId);

  return index >= 0 && index < tasks.length - 1 ? (tasks[index + 1]?.id ?? null) : null;
}

/** Shown when a board has columns but no tasks at all. */
export function EmptyBoard({ onCreate }: { onCreate: () => void }) {
  return (
    <EmptyState
      title="No maintenance tasks yet"
      description="Log a repair, an inspection, or a turnover job and it will appear here.
                   Tasks reported by a tenant can notify them automatically when the work
                   is finished."
      action={
        <button
          type="button"
          onClick={onCreate}
          className="inline-flex h-9 items-center rounded-control bg-accent px-4
                     text-[13.5px] font-medium text-white hover:bg-accent-hover"
        >
          New task
        </button>
      }
    />
  );
}
