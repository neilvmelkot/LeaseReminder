"use client";

import { useEffect, useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import {
  Building2,
  CalendarClock,
  Check,
  Clock,
  DollarSign,
  Archive,
  ArchiveRestore,
  Lock,
  Pencil,
  Repeat,
  Send,
  User,
  X,
} from "lucide-react";

import { addCommentAction } from "@/app/maintenance/actions";
import {
  Avatar,
  Button,
  cx,
  Priority,
  Status,
  Textarea,
} from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";
import {
  AssigneePicker,
  type Assignable,
} from "@/components/kanban/assignee-picker";
import { TaskEditForm } from "@/components/kanban/task-edit-form";
import { setTaskArchivedAction } from "@/app/maintenance/actions";

/**
 * How much of the activity trail is shown before asking.
 *
 * Ten, because the recent entries are the ones that explain the current state
 * of the task. The rest is history, and on a task that has been open for
 * months it is enough history to push the comment thread off the screen.
 */
const activityPreviewCount = 10;
import {
  categoryLabel,
  dueDateLabel,
  formatShortDate,
  relativeTime,
  sourceLabel,
  statusLabel,
  type TaskDetail,
} from "@/lib/maintenance";

/**
 * The task detail panel.
 *
 * <h3>Why a side panel and not a page</h3>
 *
 * Opening a card should not lose the board. A full page navigation means the
 * scroll position, the collapsed columns, and the filters are all rebuilt on
 * the way back, and triaging twenty tasks becomes twenty round trips through
 * that. A panel keeps the board behind it.
 *
 * <h3>Focus handling</h3>
 *
 * Section 5 requires dialogs to trap and restore focus. On open, focus moves
 * into the panel and the element that opened it is remembered; on close, focus
 * returns there. Escape closes. Without that, a keyboard user who opens a card
 * is stranded: tab continues through the board behind the panel.
 */
export function TaskDrawer({
  task,
  organizationId,
  isLoading,
  onClose,
  assignable,
  onChanged,
}: {
  task: TaskDetail | null;
  organizationId: string;
  isLoading: boolean;
  onClose: () => void;
  /** Everyone the task can be given to: workspace members and active vendors. */
  assignable: Assignable[];
  /**
   * Called after anything in here changes the task.
   *
   * The panel holds a fetched copy, so a change made inside it is invisible
   * until that copy is replaced. The parent owns the fetch, so it does the
   * replacing.
   */
  onChanged: () => void;
}) {
  const panelRef = useRef<HTMLDivElement | null>(null);
  const { isClosing, requestClose } = useExitAnimation(onClose);
  const previouslyFocused = useRef<HTMLElement | null>(null);

  // Deliberately two effects, and the focus one is deliberately mount-only.
  //
  // These were one effect keyed on the close callback. Callers pass an inline
  // arrow for that, so its identity changes every render, the effect re-ran
  // every render, and the focus call dragged the caret back to the first
  // field on every keystroke. Focus-on-open happens once per opening; the
  // component is mounted per opening, so [] is exactly that.
  useEffect(() => {
    previouslyFocused.current = document.activeElement as HTMLElement | null;

    // Focus the panel itself rather than the first control: landing on the
    // Close button means an accidental Enter dismisses what you just opened.
    panelRef.current?.focus();

    const restoreTo = previouslyFocused.current;

    return () => {
      // Restore focus to whatever opened the panel, so tab order continues
      // from the card rather than from the top of the document.
      restoreTo?.focus();
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.stopPropagation();
        requestClose();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    return () => document.removeEventListener("keydown", onKeyDown);
  }, [requestClose]);

  return (
    <>
      {/* The scrim is clickable to dismiss, and hidden from assistive
          technology: the Close button is the accessible route out. */}
      <div
        className={cx(
          "fixed inset-0 z-40 bg-ink/25 backdrop-blur-[1px]",
          isClosing
            ? "motion-safe:animate-[fade-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[fade-in_150ms_ease-out]",
        )}
        onClick={requestClose}
        aria-hidden="true"
      />

      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={task ? `Task ${task.referenceNumber}` : "Task"}
        tabIndex={-1}
        className={cx(
          `fixed inset-y-0 right-0 z-50 flex w-full max-w-[34rem] flex-col
           border-l border-line bg-surface shadow-floating outline-none`,
          isClosing
            ? "motion-safe:animate-[drawer-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[drawer-in_200ms_cubic-bezier(0.16,1,0.3,1)]",
        )}
      >
        {isLoading || !task ? (
          <div className="flex flex-1 items-center justify-center p-8">
            <p className="text-[13px] text-ink-secondary">Loading task…</p>
          </div>
        ) : (
          <TaskDrawerBody
            task={task}
            organizationId={organizationId}
            onClose={onClose}
            assignable={assignable}
            onChanged={onChanged}
          />
        )}
      </div>
    </>
  );
}

function TaskDrawerBody({
  task,
  organizationId,
  onClose,
  assignable,
  onChanged,
}: {
  task: TaskDetail;
  organizationId: string;
  onClose: () => void;
  assignable: Assignable[];
  onChanged: () => void;
}) {
  const [showAllActivity, setShowAllActivity] = useState(false);
  const [isEditing, setIsEditing] = useState(false);
  const [archiveError, setArchiveError] = useState<string | null>(null);
  const [comment, setComment] = useState("");
  const [isInternal, setIsInternal] = useState(false);
  const [commentError, setCommentError] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();
  const router = useRouter();

  const due = dueDateLabel(task);

  const visibleActivity = showAllActivity
    ? task.activity
    : task.activity.slice(0, activityPreviewCount);

  function submitComment() {
    if (comment.trim().length === 0) {
      return;
    }

    setCommentError(null);

    startTransition(async () => {
      const result = await addCommentAction(
        organizationId,
        task.id,
        comment,
        isInternal,
      );

      if (result.ok) {
        setComment("");
        setIsInternal(false);
      } else {
        setCommentError(result.error ?? "The comment could not be added.");
      }
    });
  }

  return (
    <>
      {/* ------------------------------------------------------------------ */}
      {/* Header                                                             */}
      {/* ------------------------------------------------------------------ */}
      <header className="flex items-start gap-3 border-b border-line px-5 py-4">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="tabular text-[12px] font-semibold text-ink-muted">
              #{task.referenceNumber}
            </span>
            <Status tone={task.statusCategory === "completed" ? "success" : "neutral"}>
              {task.columnName}
            </Status>
            <Priority priority={task.priority} alwaysShow />
          </div>

          <h2 className="mt-1.5 text-[17px] font-semibold leading-snug">{task.title}</h2>

          <p className="mt-1 text-[12.5px] text-ink-secondary">
            {sourceLabel(task.source)} · {categoryLabel(task.category)}
          </p>
        </div>

        <div className="-mr-1 -mt-1 flex shrink-0 items-center gap-1">
          {/* Hidden while editing: the form has its own Cancel, and two ways
              out of one state is how somebody loses what they typed. */}
          {isEditing ? null : (
            <>
              <Button variant="ghost" size="sm" onClick={() => setIsEditing(true)}>
                <Pencil className="size-3.5" aria-hidden="true" />
                Edit
              </Button>

              <Button
                variant="ghost"
                size="sm"
                disabled={isPending}
                onClick={() => {
                  setArchiveError(null);

                  startTransition(async () => {
                    const result = await setTaskArchivedAction(
                      organizationId,
                      task.id,
                      task.archivedAt == null,
                    );

                    if (!result.ok) {
                      setArchiveError(
                        result.error ?? "That could not be changed.",
                      );

                      return;
                    }

                    router.refresh();
                    onChanged();

                    // An archived task is off the board, so leaving the panel
                    // open on it would show something that is no longer
                    // there.
                    if (task.archivedAt == null) {
                      onClose();
                    }
                  });
                }}
              >
                {task.archivedAt == null ? (
                  <>
                    <Archive className="size-3.5" aria-hidden="true" />
                    Archive
                  </>
                ) : (
                  <>
                    <ArchiveRestore className="size-3.5" aria-hidden="true" />
                    Restore
                  </>
                )}
              </Button>
            </>
          )}

          <button
            type="button"
            onClick={onClose}
            aria-label="Close task"
            className="inline-flex size-8 shrink-0 items-center justify-center
                       rounded-control text-ink-secondary transition-colors
                       hover:bg-surface-sunken hover:text-ink"
          >
            <X className="size-4" />
          </button>
        </div>
      </header>

      <div className="flex-1 overflow-y-auto">
        {archiveError ? (
          <p
            role="alert"
            className="reveal mx-5 mt-4 rounded-control border border-danger/30
                       bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
          >
            {archiveError}
          </p>
        ) : null}

        {task.archivedAt != null ? (
          <p
            className="mx-5 mt-4 rounded-control border border-warning/40 bg-warning/10
                       px-3 py-2 text-[12.5px] leading-relaxed text-warning-text"
          >
            This task is archived, so it is not on the board. Everything on it
            is kept, and Restore puts it back.
          </p>
        ) : null}

        {isEditing ? (
          <section className="border-b border-line px-5 py-4">
            <TaskEditForm
              organizationId={organizationId}
              task={task}
              onDone={() => setIsEditing(false)}
              onSaved={onChanged}
            />
          </section>
        ) : null}

        {/* ---------------------------------------------------------------- */}
        {/* Facts                                                            */}
        {/* ---------------------------------------------------------------- */}
        <dl className="grid grid-cols-2 gap-x-4 gap-y-3 border-b border-line px-5 py-4">
          <Fact icon={<Building2 className="size-3.5" />} label="Location">
            {task.propertyName}
            {task.unitNumber ? `, unit ${task.unitNumber}` : ""}
          </Fact>

          <Fact icon={<CalendarClock className="size-3.5" />} label="Due">
            {due ? (
              <span
                className={
                  due.tone === "danger"
                    ? "font-medium text-danger-text"
                    : due.tone === "warning"
                      ? "text-warning-text"
                      : undefined
                }
              >
                {due.text}
                {task.dueDate ? (
                  // Both the relative and the absolute date. The relative one
                  // is what people act on; the absolute one is what they put
                  // in a message to a contractor.
                  <span className="ml-1 text-ink-muted">
                    ({formatShortDate(task.dueDate)})
                  </span>
                ) : null}
              </span>
            ) : (
              <span className="text-ink-muted">No due date</span>
            )}
          </Fact>

          <Fact icon={<User className="size-3.5" />} label="Assigned to">
            <span className="flex flex-wrap items-center gap-1.5">
              {task.assignees.length === 0 ? (
                <span className="text-warning-text">Unassigned</span>
              ) : (
                task.assignees.map((assignee) => (
                  <span
                    key={`${assignee.userId ?? assignee.vendorId}`}
                    className="inline-flex items-center gap-1"
                  >
                    <Avatar name={assignee.displayName} size="xs" />
                    <span>{assignee.displayName}</span>
                    {assignee.isLead && task.assignees.length > 1 ? (
                      <span className="text-[11px] text-ink-muted">(lead)</span>
                    ) : null}
                  </span>
                ))
              )}

              {/* Exact here, unlike on the card: the detail payload carries
                  the assignees' ids, so who is already on the task is known
                  rather than matched by name. */}
              <AssigneePicker
                organizationId={organizationId}
                taskId={task.id}
                assigned={task.assignees.map((assignee) => ({
                  userId: assignee.userId,
                  vendorId: assignee.vendorId,
                }))}
                candidates={assignable}
                onAssigned={onChanged}
              />
            </span>
          </Fact>

          <Fact icon={<DollarSign className="size-3.5" />} label="Cost">
            {task.actualCost !== null ? (
              <>
                <span className="tabular font-medium">
                  {task.currency} {task.actualCost.toFixed(2)}
                </span>
                {task.estimatedCost !== null ? (
                  // The estimate beside the actual, because the comparison is
                  // the useful part.
                  <span className="ml-1 text-ink-muted">
                    (est. {task.estimatedCost.toFixed(2)})
                  </span>
                ) : null}
              </>
            ) : task.estimatedCost !== null ? (
              <span className="tabular text-ink-secondary">
                Est. {task.currency} {task.estimatedCost.toFixed(2)}
              </span>
            ) : (
              <span className="text-ink-muted">Not recorded</span>
            )}
          </Fact>

          {task.reportedByTenantName ? (
            <Fact icon={<User className="size-3.5" />} label="Reported by">
              {task.reportedByTenantName}
              <span className="ml-1 text-[11px] text-ink-muted">(tenant)</span>
            </Fact>
          ) : null}

          {task.recurrenceIntervalDays ? (
            <Fact icon={<Repeat className="size-3.5" />} label="Repeats">
              Every {task.recurrenceIntervalDays} days
            </Fact>
          ) : null}

          {task.completedAt ? (
            <Fact icon={<Check className="size-3.5" />} label="Completed">
              {relativeTime(task.completedAt)}
            </Fact>
          ) : null}

          <Fact icon={<Clock className="size-3.5" />} label="Last activity">
            {relativeTime(task.lastActivityAt)}
          </Fact>
        </dl>

        {task.description ? (
          <section className="border-b border-line px-5 py-4">
            <h3 className="eyebrow mb-2">Description</h3>
            <p className="whitespace-pre-wrap text-[13.5px] leading-relaxed">
              {task.description}
            </p>
          </section>
        ) : null}

        {task.subtasks.length > 0 ? (
          <section className="border-b border-line px-5 py-4">
            <h3 className="eyebrow mb-2">
              Subtasks ({task.subtasks.filter((s) => s.statusCategory === "completed").length}
              {" of "}
              {task.subtasks.length} done)
            </h3>
            <ul className="space-y-1.5">
              {task.subtasks.map((subtask) => (
                <li key={subtask.id} className="flex items-center gap-2 text-[13px]">
                  <span
                    className={cx(
                      "grid size-4 shrink-0 place-items-center rounded-full border",
                      subtask.statusCategory === "completed"
                        ? "border-success bg-success text-white"
                        : "border-line-strong",
                    )}
                    aria-hidden="true"
                  >
                    {subtask.statusCategory === "completed" ? (
                      <Check className="size-2.5" strokeWidth={3} />
                    ) : null}
                  </span>
                  <span
                    className={
                      subtask.statusCategory === "completed"
                        ? "text-ink-muted line-through"
                        : undefined
                    }
                  >
                    {subtask.title}
                  </span>
                  <span className="ml-auto text-[11.5px] text-ink-muted">
                    {statusLabel(subtask.statusCategory)}
                  </span>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {/* ---------------------------------------------------------------- */}
        {/* Comments                                                         */}
        {/* ---------------------------------------------------------------- */}
        <section className="border-b border-line px-5 py-4">
          <h3 className="eyebrow mb-3">Comments ({task.comments.length})</h3>

          {task.comments.length === 0 ? (
            <p className="text-[13px] text-ink-secondary">No comments yet.</p>
          ) : (
            <ul className="space-y-3">
              {task.comments.map((entry) => (
                <li key={entry.id} className="flex gap-2.5">
                  <Avatar name={entry.authorDisplayName} size="sm" className="mt-0.5" />
                  <div className="min-w-0 flex-1">
                    <p className="flex items-center gap-1.5 text-[12.5px]">
                      <span className="font-semibold">{entry.authorDisplayName}</span>
                      <span className="text-ink-muted">
                        {relativeTime(entry.createdAt)}
                      </span>
                      {entry.isInternal ? (
                        // Visibly marked, because the author needs to know at a
                        // glance that this one is not going to the tenant.
                        <span
                          className="inline-flex items-center gap-0.5 rounded bg-warning-tint
                                     px-1 text-[10.5px] font-semibold text-warning-text"
                        >
                          <Lock className="size-2.5" aria-hidden="true" />
                          Internal
                        </span>
                      ) : null}
                    </p>
                    <p className="mt-0.5 whitespace-pre-wrap text-[13.5px] leading-relaxed">
                      {entry.body}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}

          {/* -------------------------------------------------------------- */}
          {/* New comment                                                    */}
          {/* -------------------------------------------------------------- */}
          <div className="mt-4">
            <label htmlFor="new-comment" className="sr-only">
              Add a comment
            </label>
            <Textarea
              id="new-comment"
              rows={3}
              value={comment}
              onChange={(event) => setComment(event.target.value)}
              placeholder={
                isInternal
                  ? "Internal note. The tenant will not see this."
                  : "Add a comment…"
              }
              disabled={isPending}
            />

            {commentError ? (
              <p role="alert" className="mt-1.5 text-[12px] text-danger-text">
                {commentError}
              </p>
            ) : null}

            <div className="mt-2 flex items-center justify-between gap-3">
              <label className="flex cursor-pointer items-center gap-1.5 text-[12.5px]">
                <input
                  type="checkbox"
                  checked={isInternal}
                  onChange={(event) => setIsInternal(event.target.checked)}
                  className="size-3.5 rounded border-line-strong accent-accent"
                />
                <span className={isInternal ? "font-medium text-warning-text" : undefined}>
                  Internal note
                </span>
                {task.reportedByTenantName ? (
                  <span className="text-ink-muted">
                    {isInternal
                      ? `· hidden from ${task.reportedByTenantName}`
                      : `· visible to ${task.reportedByTenantName}`}
                  </span>
                ) : null}
              </label>

              <Button
                variant="primary"
                size="sm"
                onClick={submitComment}
                disabled={isPending || comment.trim().length === 0}
              >
                <Send className="size-3.5" aria-hidden="true" />
                {isPending ? "Posting…" : "Comment"}
              </Button>
            </div>
          </div>
        </section>

        {/* ---------------------------------------------------------------- */}
        {/* Activity                                                         */}
        {/* ---------------------------------------------------------------- */}
        <section className="px-5 py-4">
          <h3 className="eyebrow mb-3">Activity</h3>

          <ol className="space-y-2.5">
            {visibleActivity.map((entry) => (
              <li key={entry.id} className="flex gap-2.5 text-[12.5px]">
                <Avatar
                  name={entry.actorKind === "system" ? null : entry.actorDisplayName}
                  size="xs"
                  className="mt-0.5"
                />
                <p className="min-w-0 flex-1 text-ink-secondary">
                  <span className="font-medium text-ink">
                    {entry.actorKind === "system" ? "System" : entry.actorDisplayName}
                  </span>{" "}
                  {entry.summary}
                  <span className="ml-1 text-ink-muted">
                    · {relativeTime(entry.createdAt)}
                  </span>
                </p>
              </li>
            ))}
          </ol>

          {/* A long task accumulates a lot of this, and the useful entries are
              the recent ones — so the tail is behind a click rather than
              pushing the comment thread off the screen. */}
          {task.activity.length > activityPreviewCount ? (
            <button
              type="button"
              onClick={() => setShowAllActivity((shown) => !shown)}
              className="mt-3 text-[12.5px] font-medium text-accent transition-colors
                         hover:underline"
            >
              {showAllActivity
                ? "Show recent activity only"
                : `Show all ${task.activity.length} entries`}
            </button>
          ) : null}
        </section>
      </div>
    </>
  );
}

function Fact({
  icon,
  label,
  children,
}: {
  icon: React.ReactNode;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="min-w-0">
      <dt className="flex items-center gap-1 text-[11px] font-medium uppercase tracking-wide text-ink-muted">
        <span aria-hidden="true">{icon}</span>
        {label}
      </dt>
      <dd className="mt-0.5 text-[13px]">{children}</dd>
    </div>
  );
}
