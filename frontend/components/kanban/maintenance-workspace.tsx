"use client";

import { useCallback, useState } from "react";
import { Columns3, List, Plus } from "lucide-react";

import { KanbanBoard, EmptyBoard } from "@/components/kanban/board";
import { PillTabs } from "@/components/ui/pill-tabs";
import { TaskDrawer } from "@/components/kanban/task-drawer";
import type { Assignable } from "@/components/kanban/assignee-picker";
import { TaskRow } from "@/components/kanban/task-card";
import { NewTaskDialog } from "@/components/kanban/new-task-dialog";
import {
  Button,
  EmptyState,
  Panel,
  TableFrame,
  Th,
} from "@/components/ui/primitives";
import type { Board, TaskCard, TaskDetail } from "@/lib/maintenance";

/**
 * The maintenance workspace: board and list views over the same data, plus the
 * task panel and the new-task dialog.
 *
 * <h3>Why both a board and a list</h3>
 *
 * A board is excellent for "what is the state of the work" and poor for "what
 * is overdue across every property, worst first". The second question has
 * nothing to do with dragging, and forcing it through a board means reading
 * five columns and comparing by eye.
 *
 * Both views render the same already-fetched cards, so switching is instant
 * and costs no request.
 *
 * <h3>Why the task panel fetches on open</h3>
 *
 * The board payload carries only what a card shows. The detail view needs the
 * description, the full comment thread, the activity timeline, and the
 * subtasks, which on a board of two hundred cards would be a large payload for
 * data nobody has asked to see. So the panel fetches one task when it opens.
 */
export function MaintenanceWorkspace({
  board,
  organizationId,
  properties,
  tenants,
  assignable,
}: {
  board: Board;
  organizationId: string;
  properties: Array<{ id: string; name: string }>;
  tenants: Array<{ id: string; name: string }>;
  /** Everyone a task can be given to: workspace members and active vendors. */
  assignable: Assignable[];
}) {
  const [view, setView] = useState<"board" | "list">("board");
  const [openTaskId, setOpenTaskId] = useState<string | null>(null);
  const [openTask, setOpenTask] = useState<TaskDetail | null>(null);
  const [isLoadingTask, setIsLoadingTask] = useState(false);
  const [isCreating, setIsCreating] = useState(false);

  const openTaskPanel = useCallback(
    async (taskId: string, options?: { silent?: boolean }) => {
      setOpenTaskId(taskId);

      // A silent refetch keeps the current content on screen while the new
      // copy arrives. Clearing it would flash the loading state after every
      // small change, which reads as the panel reloading rather than
      // updating.
      if (!options?.silent) {
        setOpenTask(null);
        setIsLoadingTask(true);
      }

      try {
        // A small route handler rather than a server action: this is a read,
        // and an action would force a router round trip for data that is not
        // part of the page's own render.
        const response = await fetch(
          `/maintenance/api/task?organizationId=${organizationId}&taskId=${taskId}`,
          { cache: "no-store" },
        );

        if (response.ok) {
          setOpenTask((await response.json()) as TaskDetail);
        } else {
          // Closing is better than an empty panel: the board is still there
          // and the user can try again.
          setOpenTaskId(null);
        }
      } catch {
        setOpenTaskId(null);
      } finally {
        setIsLoadingTask(false);
      }
    },
    [organizationId],
  );

  const closeTaskPanel = useCallback(() => {
    setOpenTaskId(null);
    setOpenTask(null);
  }, []);

  const allCards: TaskCard[] = board.columns.flatMap((column) => column.tasks);

  // Worst first: overdue before not, then by how overdue, then by priority.
  // That ordering is the whole reason the list view exists.
  const priorityWeight = { urgent: 0, high: 1, medium: 2, low: 3 } as const;

  const sortedCards = [...allCards].sort((left, right) => {
    if (left.isOverdue !== right.isOverdue) {
      return left.isOverdue ? -1 : 1;
    }

    const leftDays = left.daysUntilDue ?? Number.MAX_SAFE_INTEGER;
    const rightDays = right.daysUntilDue ?? Number.MAX_SAFE_INTEGER;

    if (leftDays !== rightDays) {
      return leftDays - rightDays;
    }

    return priorityWeight[left.priority] - priorityWeight[right.priority];
  });

  return (
    <div>
      {/* ------------------------------------------------------------------ */}
      {/* Toolbar                                                            */}
      {/* ------------------------------------------------------------------ */}
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <PillTabs
          ariaLabel="View"
          activeKey={view}
          onSelect={setView}
          size="sm"
          tabs={[
            {
              key: "board",
              label: "Board",
              icon: <Columns3 className="size-3.5" />,
            },
            { key: "list", label: "List", icon: <List className="size-3.5" /> },
          ]}
        />

        <div className="flex items-center gap-3">
          <p className="text-[12.5px] text-ink-secondary">
            <span className="tabular font-semibold text-ink">{board.openTaskCount}</span>{" "}
            open
            {board.overdueTaskCount > 0 ? (
              <>
                {" · "}
                <span className="tabular font-semibold text-danger-text">
                  {board.overdueTaskCount}
                </span>{" "}
                overdue
              </>
            ) : null}
          </p>

          <Button variant="primary" size="sm" onClick={() => setIsCreating(true)}>
            <Plus className="size-3.5" aria-hidden="true" />
            New task
          </Button>
        </div>
      </div>

      {/* ------------------------------------------------------------------ */}
      {/* Views                                                              */}
      {/* ------------------------------------------------------------------ */}
      {allCards.length === 0 ? (
        <Panel>
          <EmptyBoard onCreate={() => setIsCreating(true)} />
        </Panel>
      ) : view === "board" ? (
        <KanbanBoard
          board={board}
          organizationId={organizationId}
          onOpenTask={openTaskPanel}
          assignable={assignable}
        />
      ) : (
        <Panel>
          {sortedCards.length === 0 ? (
            <EmptyState
              title="Nothing to show"
              description="No tasks match the current filters."
            />
          ) : (
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
                {sortedCards.map((card) => (
                  <TaskRow key={card.id} card={card} onOpen={openTaskPanel} />
                ))}
              </tbody>
            </TableFrame>
          )}
        </Panel>
      )}

      {/* ------------------------------------------------------------------ */}
      {/* Overlays                                                           */}
      {/* ------------------------------------------------------------------ */}
      {openTaskId ? (
        <TaskDrawer
          task={openTask}
          organizationId={organizationId}
          assignable={assignable}
          onChanged={() => {
            if (openTaskId) {
              void openTaskPanel(openTaskId, { silent: true });
            }
          }}
          isLoading={isLoadingTask}
          onClose={closeTaskPanel}
        />
      ) : null}

      {isCreating ? (
        <NewTaskDialog
          organizationId={organizationId}
          properties={properties}
          tenants={tenants}
          onClose={() => setIsCreating(false)}
        />
      ) : null}
    </div>
  );
}

