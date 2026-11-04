import Link from "next/link";
import { redirect } from "next/navigation";
import { Archive, ChevronLeft } from "lucide-react";

import { Shell } from "@/components/shell";
import { ArchiveRowActions } from "@/components/kanban/archive-row-actions";
import {
  EmptyState,
  ErrorState,
  PageHeader,
  Panel,
  Priority,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import { api, ApiError, maintenanceApi, permissions } from "@/lib/api";
import { categoryLabel, formatShortDate, statusLabel } from "@/lib/maintenance";
import type { TaskCard } from "@/lib/maintenance";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * The archive: tasks withdrawn from the board.
 *
 * <h3>Why this is a list and not a board</h3>
 *
 * An archived task has no position and nobody is going to drag it anywhere.
 * What somebody comes here to do is find one — a duplicate they filed by
 * mistake, a job that was cancelled, something logged against the wrong
 * property — and then either put it back or get rid of it. That is a table
 * with two buttons on each row.
 *
 * <h3>Why deleting lives only here</h3>
 *
 * A task carries its comment thread and its activity trail, and destroying
 * those by a misclick on the board would lose the answer to "what happened
 * with that leak". So there is no delete on the board at all: a task has to
 * be archived first, which is reversible and visible, and only then can it be
 * destroyed. The database enforces that order as well, so it holds for a
 * direct API call and not just for the button.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Task archive" };

export default async function TaskArchivePage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let tasks: TaskCard[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      const board = await maintenanceApi.board(organization.id, {
        archivedOnly: true,
        // The board hides settled cards after a fortnight so the Done column
        // stays a column. The archive is the opposite kind of place: it is
        // where you go to look for something old, so the window is lifted.
        includeAllCompleted: true,
      });

      // The archive has no columns in any meaningful sense — every task here
      // is off the board — so they are flattened and ordered by how recently
      // they were worked on.
      tasks = board.columns
        .flatMap((column) => column.tasks)
        .sort((left, right) => right.referenceNumber - left.referenceNumber);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/maintenance"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          Back to the board
        </Link>
      </div>

      <PageHeader
        title="Task archive"
        description="Tasks taken off the board. Restoring one puts it back where it was."
        meta={
          tasks.length > 0 ? (
            <Status tone="neutral">
              {tasks.length} archived
            </Status>
          ) : null
        }
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before logging maintenance tasks."
          />
        </Panel>
      ) : (
        <Panel>
          {tasks.length === 0 ? (
            <EmptyState
              icon={<Archive className="size-5" aria-hidden="true" />}
              title="Nothing archived"
              description="Archiving a task takes it off the board without losing its comments or history. You can put it back at any time."
            />
          ) : (
            <TableFrame minWidth="56rem">
              <thead>
                <tr>
                  <Th>Task</Th>
                  <Th>Location</Th>
                  <Th>Priority</Th>
                  <Th>Status when archived</Th>
                  <Th>Due</Th>
                  <Th align="right">Actions</Th>
                </tr>
              </thead>
              <tbody>
                {tasks.map((task) => (
                  <tr key={task.id} className="transition-colors hover:bg-surface-subtle">
                    <Td>
                      <span className="tabular mr-1.5 text-[11.5px] font-semibold text-ink-muted">
                        #{task.referenceNumber}
                      </span>
                      <span className="text-[13.5px] font-medium">{task.title}</span>
                      <span className="mt-0.5 block text-[11.5px] text-ink-muted">
                        {categoryLabel(task.category)}
                      </span>
                    </Td>

                    <Td>
                      {task.propertyName}
                      {task.unitNumber ? ` · Unit ${task.unitNumber}` : ""}
                    </Td>

                    <Td>
                      <Priority priority={task.priority} alwaysShow />
                    </Td>

                    <Td>
                      <Status tone="neutral">{statusLabel(task.statusCategory)}</Status>
                    </Td>

                    <Td>
                      {task.dueDate ? (
                        formatShortDate(task.dueDate)
                      ) : (
                        <span className="text-ink-muted">—</span>
                      )}
                    </Td>

                    <Td align="right">
                      {access.canWrite ? (
                        <ArchiveRowActions
                          organizationId={organizationId}
                          taskId={task.id}
                          canAdminister={access.canAdminister}
                        />
                      ) : (
                        <span className="text-[12px] text-ink-muted">
                          Read-only
                        </span>
                      )}
                    </Td>
                  </tr>
                ))}
              </tbody>
            </TableFrame>
          )}
        </Panel>
      )}
    </Shell>
  );
}
