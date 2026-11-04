"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { Field, fieldClass } from "@/components/ui/form-dialog";
import { categoryLabel, type TaskCategory, type TaskDetail } from "@/lib/maintenance";

import { updateTaskAction } from "@/app/maintenance/actions";

const categories: TaskCategory[] = [
  "plumbing",
  "electrical",
  "hvac",
  "appliance",
  "structural",
  "roofing",
  "pest",
  "landscaping",
  "cleaning",
  "safety",
  "inspection",
  "turnover",
  "administrative",
  "other",
];

/**
 * Editing a task's details.
 *
 * <h3>Why clearing the due date is its own flag</h3>
 *
 * The API takes <code>clearDueDate</code> alongside <code>dueDate</code>,
 * because null cannot say which of two different things is meant: "leave the
 * due date alone" and "remove it". A patch that treated an absent value as
 * "remove" would silently drop the deadline off every task somebody renamed.
 *
 * So an emptied date field sets the flag, and an untouched one sends neither.
 *
 * <h3>What is not editable here</h3>
 *
 * The property, the unit, and the reporting tenant. Those are what the task
 * *is* — moving a repair to a different property is not an edit, it is a
 * different task, and silently rewriting it would detach the work from the
 * history and the notifications that already named it. The column is not here
 * either: that is what dragging the card does, and it fires the notification
 * rules.
 */
export function TaskEditForm({
  organizationId,
  task,
  onDone,
  onSaved,
}: {
  organizationId: string;
  task: TaskDetail;
  onDone: () => void;
  /** For a caller holding a fetched copy of the task. */
  onSaved?: () => void;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState({
    title: task.title,
    description: task.description ?? "",
    priority: task.priority as string,
    category: task.category as string,
    dueDate: task.dueDate ?? "",
    estimatedCost:
      task.estimatedCost == null ? "" : String(task.estimatedCost),
    actualCost: task.actualCost == null ? "" : String(task.actualCost),
  });

  function update(key: keyof typeof form, value: string) {
    setForm((previous) => ({ ...previous, [key]: value }));
  }

  function save() {
    setError(null);

    if (form.title.trim().length === 0) {
      setError("A task needs a title.");

      return;
    }

    const hadDueDate = task.dueDate !== null;
    const wantsNoDueDate = form.dueDate.trim() === "";

    startTransition(async () => {
      const result = await updateTaskAction(organizationId, task.id, {
        title: form.title.trim(),
        description: form.description.trim() || null,
        priority: form.priority,
        category: form.category,
        // Only one of these is ever meaningful. Sending a date and the clear
        // flag together would be contradictory.
        ...(wantsNoDueDate
          ? { clearDueDate: hadDueDate }
          : { dueDate: form.dueDate }),
        estimatedCost: numberOrNull(form.estimatedCost),
        actualCost: numberOrNull(form.actualCost),
      });

      if (!result.ok) {
        setError(result.error ?? "The task could not be saved.");

        return;
      }

      onDone();
      router.refresh();
      onSaved?.();
    });
  }

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault();
        save();
      }}
    >
      <Field label="Title" htmlFor="task-title" required>
        <input
          id="task-title"
          value={form.title}
          onChange={(event) => update("title", event.target.value)}
          maxLength={300}
          className={fieldClass}
        />
      </Field>

      <Field
        label="Description"
        htmlFor="task-description"
        hint="What needs doing, and anything a contractor would need to know."
      >
        <textarea
          id="task-description"
          value={form.description}
          onChange={(event) => update("description", event.target.value)}
          rows={4}
          maxLength={10000}
          className={fieldClass}
        />
      </Field>

      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Priority" htmlFor="task-priority">
          <select
            id="task-priority"
            value={form.priority}
            onChange={(event) => update("priority", event.target.value)}
            className={fieldClass}
          >
            <option value="urgent">Urgent</option>
            <option value="high">High</option>
            <option value="medium">Medium</option>
            <option value="low">Low</option>
          </select>
        </Field>

        <Field label="Category" htmlFor="task-category">
          <select
            id="task-category"
            value={form.category}
            onChange={(event) => update("category", event.target.value)}
            className={fieldClass}
          >
            {categories.map((option) => (
              <option key={option} value={option}>
                {categoryLabel(option)}
              </option>
            ))}
          </select>
        </Field>

        <Field
          label="Due"
          htmlFor="task-due"
          hint={`Read in ${task.propertyName}'s zone.`}
        >
          <input
            id="task-due"
            type="date"
            value={form.dueDate}
            onChange={(event) => update("dueDate", event.target.value)}
            className={fieldClass}
          />
        </Field>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={`Estimated cost (${task.currency})`} htmlFor="task-estimate">
          <input
            id="task-estimate"
            type="number"
            step="0.01"
            min="0"
            value={form.estimatedCost}
            onChange={(event) => update("estimatedCost", event.target.value)}
            className={fieldClass}
          />
        </Field>

        <Field
          label={`Actual cost (${task.currency})`}
          htmlFor="task-actual"
          hint="Fill this in once the work is done."
        >
          <input
            id="task-actual"
            type="number"
            step="0.01"
            min="0"
            value={form.actualCost}
            onChange={(event) => update("actualCost", event.target.value)}
            className={fieldClass}
          />
        </Field>
      </div>

      {error ? (
        <p
          role="alert"
          className="reveal flex items-start gap-1.5 rounded-control border
                     border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                     text-danger-text"
        >
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      <div className="flex items-center gap-2">
        <Button type="submit" variant="primary" size="sm" disabled={isPending}>
          {isPending ? "Saving…" : "Save changes"}
        </Button>
        <Button type="button" variant="ghost" size="sm" disabled={isPending} onClick={onDone}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/**
 * A cost, or null.
 *
 * Null rather than 0 for an empty field: zero is a real cost — a repair done
 * under warranty — and must survive, while "" must not become it.
 */
function numberOrNull(value: string): number | null {
  const trimmed = value.trim();

  if (!trimmed) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : null;
}
