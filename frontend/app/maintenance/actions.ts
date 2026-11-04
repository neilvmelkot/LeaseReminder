"use server";

import { revalidatePath } from "next/cache";

import { apiFetch } from "@/lib/api";

/**
 * Server actions for the maintenance board.
 *
 * <h3>Why these are server actions rather than browser fetches</h3>
 *
 * The board is interactive, so the drag handler runs in the browser. But the
 * API is authenticated with the session's access token, and that token lives
 * in an http-only cookie read by server-only code. A browser fetch would mean
 * either exposing the token to client JavaScript or writing a proxy route
 * handler for each verb.
 *
 * A server action is the same thing as the proxy route without the
 * boilerplate: the body is serialised to the server, the token is attached
 * there, and only the result crosses back. The token never enters the client
 * bundle.
 *
 * <h3>Why every action returns a result object instead of throwing</h3>
 *
 * A thrown error in a server action reaches the client as an opaque digest
 * with the message stripped, which is correct for security and useless for
 * telling somebody that their drag was rejected. So each action catches,
 * classifies, and returns a message the board can show while rolling back its
 * optimistic update.
 */

export interface ActionResult {
  ok: boolean;
  error?: string;
}

/**
 * Moves a card.
 *
 * <p>
 * Takes the neighbouring card ids, not a position. The client knows what the
 * drop landed between; it does not reliably know the current positions,
 * because somebody else may have reordered the column since the board loaded.
 * The server recomputes from current truth, so two simultaneous drags merge
 * rather than both writing the same midpoint.
 * </p>
 */
export async function moveTaskAction(
  organizationId: string,
  taskId: string,
  targetColumnId: string,
  previousTaskId: string | null,
  nextTaskId: string | null,
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}/move`,
      {
        method: "POST",
        body: JSON.stringify({ targetColumnId, previousTaskId, nextTaskId }),
      },
    );

    // The board holds its own optimistic state, so this is not what updates
    // the UI. It refreshes the server-rendered counts in the column headers
    // and the dashboard, which the optimistic update does not know about.
    revalidatePath("/maintenance");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "move this task") };
  }
}

export async function createTaskAction(
  organizationId: string,
  input: {
    propertyId: string;
    title: string;
    unitId?: string | null;
    description?: string | null;
    priority: string;
    category: string;
    source: string;
    dueDate?: string | null;
    reportedByTenantId?: string | null;
    estimatedCost?: number | null;
    recurrenceIntervalDays?: number | null;
  },
): Promise<ActionResult & { taskId?: string }> {
  try {
    const created = await apiFetch<{ id: string }>(
      `/api/v1/organizations/${organizationId}/maintenance/tasks`,
      { method: "POST", body: JSON.stringify(input) },
    );

    revalidatePath("/maintenance");

    return { ok: true, taskId: created.id };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "create this task") };
  }
}

export async function addCommentAction(
  organizationId: string,
  taskId: string,
  body: string,
  isInternal: boolean,
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}/comments`,
      { method: "POST", body: JSON.stringify({ body, isInternal }) },
    );


    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "add this comment") };
  }
}

export async function assignTaskAction(
  organizationId: string,
  taskId: string,
  assignee: { userId?: string | null; vendorId?: string | null; isLead: boolean },
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}/assign`,
      { method: "POST", body: JSON.stringify(assignee) },
    );

    revalidatePath("/maintenance");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "assign this task") };
  }
}

export async function updateTaskAction(
  organizationId: string,
  taskId: string,
  patch: {
    title?: string;
    description?: string | null;
    priority?: string;
    category?: string;
    dueDate?: string | null;
    clearDueDate?: boolean;
    estimatedCost?: number | null;
    actualCost?: number | null;
    vendorId?: string | null;
  },
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}`,
      { method: "PATCH", body: JSON.stringify(patch) },
    );

    revalidatePath("/maintenance");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "update this task") };
  }
}

/**
 * Withdraws a task from the board, or puts it back.
 *
 * <p>
 * Archiving is not deleting and not completing. It keeps the comment thread,
 * the activity trail and the costs, and it is reversible — which is why there
 * is no delete at all: nothing needs one, and offering it would make a
 * comment thread destructible by a misclick.
 * </p>
 * <p>
 * Both calls are idempotent in the database, so a double click is a no-op
 * rather than an error.
 * </p>
 */
export async function setTaskArchivedAction(
  organizationId: string,
  taskId: string,
  archived: boolean,
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}/${
        archived ? "archive" : "restore"
      }`,
      { method: "POST" },
    );

    // Both, because the task moves between them: archiving takes it off the
    // board and into the archive, and restoring does the reverse. Revalidating
    // only the page the button was on leaves the other one stale.
    revalidatePath("/maintenance");
    revalidatePath("/maintenance/archive");

    return { ok: true };
  } catch (error) {
    return {
      ok: false,
      error: describeFailure(
        error,
        archived ? "archive this task" : "restore this task",
      ),
    };
  }
}

/**
 * Deletes an archived task, permanently.
 *
 * <p>
 * The database refuses this for a task that is not already archived, so
 * there is no route from the board straight to gone. Archiving is the
 * reversible first step and this is the second, which is what keeps a
 * comment thread from being destroyed by one misclick.
 * </p>
 * <p>
 * Administrators only, enforced in the same place.
 * </p>
 */
export async function deleteTaskAction(
  organizationId: string,
  taskId: string,
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}`,
      { method: "DELETE" },
    );

    revalidatePath("/maintenance/archive");
    revalidatePath("/maintenance");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "delete this task") };
  }
}

export async function toggleNotificationRuleAction(
  organizationId: string,
  ruleId: string,
  enabled: boolean,
): Promise<ActionResult> {
  try {
    await apiFetch(
      `/api/v1/organizations/${organizationId}/maintenance/notification-rules/${ruleId}`,
      { method: "PATCH", body: JSON.stringify({ enabled }) },
    );

    revalidatePath("/settings/notifications");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "change this rule") };
  }
}

/* -------------------------------------------------------------------------- */
/* Notification centre                                                         */
/* -------------------------------------------------------------------------- */

export async function markNotificationsReadAction(
  notificationIds: string[] | null,
): Promise<ActionResult> {
  try {
    await apiFetch("/api/v1/notifications/read", {
      method: "POST",
      body: JSON.stringify({ notificationIds }),
    });

    // The badge appears in the shell on every page, so the whole layout is
    // revalidated rather than one route.
    revalidatePath("/", "layout");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "mark these as read") };
  }
}

export async function dismissNotificationAction(
  notificationId: string,
): Promise<ActionResult> {
  try {
    await apiFetch(`/api/v1/notifications/${notificationId}/dismiss`, {
      method: "POST",
    });

    revalidatePath("/", "layout");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "dismiss this") };
  }
}

/**
 * Turns an API failure into something worth showing a person.
 *
 * <p>
 * The three cases that matter are distinguished because the right next step
 * differs: a 401 means sign in again, a 403 means ask somebody with the right
 * role, and anything else means try again. A single "something went wrong"
 * leaves the reader with no idea which.
 * </p>
 */
function describeFailure(error: unknown, attempted: string): string {
  if (error instanceof Error && "status" in error) {
    const status = (error as Error & { status: number }).status;

    if (status === 401) {
      return "Your session has expired. Sign in again to continue.";
    }

    if (status === 403) {
      return `You do not have permission to ${attempted}.`;
    }

    if (status === 409) {
      return error.message;
    }

    if (status === 400 || status === 422) {
      return error.message;
    }
  }

  return `Could not ${attempted}. Please try again.`;
}
