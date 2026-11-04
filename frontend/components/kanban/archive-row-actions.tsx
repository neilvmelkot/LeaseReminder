"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { RotateCcw, Trash2 } from "lucide-react";

import { Button, cx } from "@/components/ui/primitives";
import { deleteTaskAction, setTaskArchivedAction } from "@/app/maintenance/actions";

/**
 * Restore and delete, for one row of the archive.
 *
 * <h3>Why delete asks twice, and why it asks in place</h3>
 *
 * This is the only irreversible action in the application, so it does not
 * happen on one click. It does not open a modal either: a modal for a
 * single-row action moves the question away from the row it is about, and
 * on a list of twenty near-identical archived tasks "are you sure?" in the
 * middle of the screen is a question about which one.
 *
 * So the row itself turns into the confirmation, naming nothing new but
 * keeping the task's own title and reference beside the button that will
 * destroy it.
 */
export function ArchiveRowActions({
  organizationId,
  taskId,
  canAdminister,
}: {
  organizationId: string;
  taskId: string;
  /** Deleting is administrator-only, enforced in the database as well. */
  canAdminister: boolean;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [isConfirming, setIsConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function restore() {
    setError(null);

    startTransition(async () => {
      const result = await setTaskArchivedAction(organizationId, taskId, false);

      if (!result.ok) {
        setError(result.error ?? "That task could not be restored.");

        return;
      }

      router.refresh();
    });
  }

  function remove() {
    setError(null);

    startTransition(async () => {
      const result = await deleteTaskAction(organizationId, taskId);

      if (!result.ok) {
        setError(result.error ?? "That task could not be deleted.");
        setIsConfirming(false);

        return;
      }

      router.refresh();
    });
  }

  return (
    <div className="flex flex-col items-end gap-1">
      {isConfirming ? (
        <div className="flex items-center gap-2">
          <span className="text-[12px] text-ink-secondary">Delete for good?</span>

          <Button
            size="sm"
            variant="ghost"
            onClick={() => setIsConfirming(false)}
            disabled={isPending}
          >
            Cancel
          </Button>

          <Button
            size="sm"
            variant="danger"
            onClick={remove}
            disabled={isPending}
          >
            {isPending ? "Deleting…" : "Delete"}
          </Button>
        </div>
      ) : (
        <div className="flex items-center gap-1.5">
          <Button
            size="sm"
            variant="ghost"
            onClick={restore}
            disabled={isPending}
            title="Put this task back on the board"
          >
            <RotateCcw className="size-3.5" aria-hidden="true" />
            Restore
          </Button>

          <button
            type="button"
            onClick={() => setIsConfirming(true)}
            disabled={isPending || !canAdminister}
            title={
              canAdminister
                ? "Delete this task and its comments permanently"
                : "Only an administrator can delete a task"
            }
            className={cx(
              "inline-flex size-7 items-center justify-center rounded-control",
              "transition-colors",
              canAdminister
                ? "text-ink-muted hover:bg-danger-tint hover:text-danger-text"
                : "cursor-not-allowed text-ink-muted/40",
            )}
          >
            <Trash2 className="size-3.5" aria-hidden="true" />
            <span className="sr-only">Delete permanently</span>
          </button>
        </div>
      )}

      {error ? (
        <p role="alert" className="text-right text-[11.5px] text-danger-text">
          {error}
        </p>
      ) : null}
    </div>
  );
}
