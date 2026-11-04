"use client";

import { KanbanBoard } from "@/components/kanban/board";
import { TaskRow } from "@/components/kanban/task-card";
import { Panel, TableFrame, Th } from "@/components/ui/primitives";

import { fixtureBoard, fixtureCards } from "./fixtures";

/**
 * The interactive half of the design harness.
 *
 * <p>
 * Split out of the page for a structural reason, not a stylistic one. The
 * board and the task table both need an <em>onOpenTask</em> callback, and a
 * server component cannot hand a function to a client component: the function
 * would have to be serialised into the payload, and Next rejects it. The
 * handler therefore has to be created on the client side of the boundary,
 * which is here.
 * </p>
 *
 * <p>
 * The handlers are deliberately inert. Opening the drawer would need a live
 * task, and the point of this page is the visual design, not the data path.
 * </p>
 */
export function PreviewBoard() {
  return (
    <KanbanBoard
      board={fixtureBoard}
      organizationId={fixtureBoard.organizationId}
      onOpenTask={noop}
        assignable={[]}
    />
  );
}

export function PreviewTaskTable() {
  // Worst first. An overdue task is the only thing on this screen that is
  // already costing somebody money, so it does not belong below a task that
  // is not due for a fortnight.
  const ordered = [...fixtureCards].sort(
    (left, right) => Number(right.isOverdue) - Number(left.isOverdue),
  );

  return (
    <Panel>
      <TableFrame minWidth="52rem">
        <thead>
          <tr>
            <Th>Task</Th>
            <Th>Location</Th>
            <Th>Priority</Th>
            <Th>Due</Th>
            <Th>Assigned</Th>
            <Th>Status</Th>
          </tr>
        </thead>
        <tbody>
          {ordered.map((card) => (
            <TaskRow key={card.id} card={card} onOpen={noop} />
          ))}
        </tbody>
      </TableFrame>
    </Panel>
  );
}

function noop() {
  /* Inert on purpose; see the note above. */
}
