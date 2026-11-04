"use client";

import { useEffect, useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { X } from "lucide-react";

import { createTaskAction } from "@/app/maintenance/actions";
import { Button, cx, Field, Input, Select, Textarea } from "@/components/ui/primitives";
import { useExitAnimation } from "@/components/ui/use-exit-animation";
import { categoryLabel, type TaskCategory } from "@/lib/maintenance";

/**
 * The new-task dialog.
 *
 * <h3>Why the source field is prominent rather than buried</h3>
 *
 * "Reported by tenant" is not metadata. Choosing it makes the reporting tenant
 * a required field, and that one field is what lets the system acknowledge the
 * report and tell them when the work is finished. A tenant who reports a leak
 * and hears nothing assumes it was ignored, so the form asks for it up front
 * and explains why.
 *
 * <h3>Why there is no column picker</h3>
 *
 * A new task lands in the organization's intake column, decided by the
 * database. Letting somebody file a task directly into "Completed" is not a
 * feature, and letting them file into "In Progress" skips the triage the board
 * exists to make visible.
 */

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

export function NewTaskDialog({
  organizationId,
  properties,
  tenants,
  onClose,
}: {
  organizationId: string;
  properties: Array<{ id: string; name: string }>;
  tenants: Array<{ id: string; name: string }>;
  onClose: () => void;
}) {
  const router = useRouter();
  const panelRef = useRef<HTMLDivElement | null>(null);
  const { isClosing, requestClose } = useExitAnimation(onClose);
  const previouslyFocused = useRef<HTMLElement | null>(null);
  const titleRef = useRef<HTMLInputElement | null>(null);

  const [title, setTitle] = useState("");
  const [propertyId, setPropertyId] = useState(properties[0]?.id ?? "");
  const [description, setDescription] = useState("");
  const [priority, setPriority] = useState("medium");
  const [category, setCategory] = useState<TaskCategory>("other");
  const [source, setSource] = useState("staff");
  const [dueDate, setDueDate] = useState("");
  const [reportedByTenantId, setReportedByTenantId] = useState("");
  const [recurrenceIntervalDays, setRecurrenceIntervalDays] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();

  // Deliberately two effects, and the focus one is deliberately mount-only.
  //
  // These were one effect keyed on the close callback. Callers pass an inline
  // arrow for that, so its identity changes every render, the effect re-ran
  // every render, and the focus call dragged the caret back to the first
  // field on every keystroke. Focus-on-open happens once per opening; the
  // component is mounted per opening, so [] is exactly that.
  useEffect(() => {
    previouslyFocused.current = document.activeElement as HTMLElement | null;

    // The title field, because it is the one thing that always has to be
    // filled in and typing should be able to start immediately.
    titleRef.current?.focus();

    const restoreTo = previouslyFocused.current;

    return () => {
      restoreTo?.focus();
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        requestClose();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    return () => document.removeEventListener("keydown", onKeyDown);
  }, [requestClose]);

  const isTenantReport = source === "tenantReport";

  function submit() {
    setError(null);

    if (title.trim().length === 0) {
      setError("A task needs a title.");
      return;
    }

    if (!propertyId) {
      setError("Choose the property this task belongs to.");
      return;
    }

    // Checked here as well as on the server, so the message appears next to
    // the field rather than after a round trip.
    if (isTenantReport && !reportedByTenantId) {
      setError(
        "A tenant report needs the reporting tenant, so they can be told when " +
          "it is received and when it is finished.",
      );
      return;
    }

    startTransition(async () => {
      const result = await createTaskAction(organizationId, {
        propertyId,
        title: title.trim(),
        description: description.trim() || null,
        priority,
        category,
        source,
        dueDate: dueDate || null,
        reportedByTenantId: isTenantReport ? reportedByTenantId : null,
        recurrenceIntervalDays: recurrenceIntervalDays
          ? Number(recurrenceIntervalDays)
          : null,
      });

      if (result.ok) {
        onClose();
        // The board is server-rendered, so a refresh is what brings the new
        // card in with its real position and reference number.
        router.refresh();
      } else {
        setError(result.error ?? "The task could not be created.");
      }
    });
  }

  return (
    <>
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
        aria-labelledby="new-task-heading"
        className={cx(
          `fixed left-1/2 top-1/2 z-50 w-[min(34rem,calc(100vw-2rem))]
           -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-surface
           border border-line bg-surface shadow-floating`,
          isClosing
            ? "motion-safe:animate-[dialog-out_150ms_ease-in_forwards]"
            : "motion-safe:animate-[dialog-in_180ms_cubic-bezier(0.16,1,0.3,1)]",
        )}
      >
        <header className="flex items-center justify-between gap-3 border-b border-line px-5 py-3.5">
          <h2 id="new-task-heading" className="text-[15px] font-semibold">
            New maintenance task
          </h2>
          <button
            type="button"
            onClick={requestClose}
            aria-label="Close"
            className="-mr-1 inline-flex size-8 items-center justify-center rounded-control
                       text-ink-secondary hover:bg-surface-sunken hover:text-ink"
          >
            <X className="size-4" />
          </button>
        </header>

        <div className="max-h-[70vh] space-y-4 overflow-y-auto px-5 py-4">
          <Field label="What needs doing" htmlFor="task-title" required>
            <Input
              id="task-title"
              ref={titleRef}
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder="Roof leak above the stairwell"
              maxLength={300}
            />
          </Field>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Property" htmlFor="task-property" required>
              <Select
                id="task-property"
                value={propertyId}
                onChange={(event) => setPropertyId(event.target.value)}
              >
                {properties.length === 0 ? (
                  <option value="">No properties yet</option>
                ) : null}
                {properties.map((property) => (
                  <option key={property.id} value={property.id}>
                    {property.name}
                  </option>
                ))}
              </Select>
            </Field>

            <Field label="Category" htmlFor="task-category">
              <Select
                id="task-category"
                value={category}
                onChange={(event) => setCategory(event.target.value as TaskCategory)}
              >
                {categories.map((option) => (
                  <option key={option} value={option}>
                    {categoryLabel(option)}
                  </option>
                ))}
              </Select>
            </Field>

            <Field label="Priority" htmlFor="task-priority">
              <Select
                id="task-priority"
                value={priority}
                onChange={(event) => setPriority(event.target.value)}
              >
                <option value="urgent">Urgent</option>
                <option value="high">High</option>
                <option value="medium">Medium</option>
                <option value="low">Low</option>
              </Select>
            </Field>

            <Field
              label="Due date"
              htmlFor="task-due"
              hint="Drives the reminder and escalation rules."
            >
              <Input
                id="task-due"
                type="date"
                value={dueDate}
                onChange={(event) => setDueDate(event.target.value)}
              />
            </Field>
          </div>

          <Field
            label="How did this come in"
            htmlFor="task-source"
            hint="A tenant report can acknowledge receipt and announce completion automatically."
          >
            <Select
              id="task-source"
              value={source}
              onChange={(event) => setSource(event.target.value)}
            >
              <option value="staff">Raised by staff</option>
              <option value="tenantReport">Reported by a tenant</option>
              <option value="inspection">From an inspection</option>
              <option value="preventiveMaintenance">Preventive maintenance</option>
              <option value="vendor">Raised by a vendor</option>
            </Select>
          </Field>

          {isTenantReport ? (
            <Field
              label="Reporting tenant"
              htmlFor="task-tenant"
              required
              hint="They are told when this is received and when it is finished, unless they have opted out."
            >
              <Select
                id="task-tenant"
                value={reportedByTenantId}
                onChange={(event) => setReportedByTenantId(event.target.value)}
              >
                <option value="">Choose a tenant…</option>
                {tenants.map((tenant) => (
                  <option key={tenant.id} value={tenant.id}>
                    {tenant.name}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}

          <Field label="Details" htmlFor="task-description">
            <Textarea
              id="task-description"
              rows={3}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="Anything the person doing the work needs to know."
            />
          </Field>

          <Field
            label="Repeat every"
            htmlFor="task-recurrence"
            hint="Leave blank for a one-off. A repeating task creates its successor when this one is completed, counted from the completion date."
          >
            <div className="flex items-center gap-2">
              <Input
                id="task-recurrence"
                type="number"
                min={1}
                max={3650}
                value={recurrenceIntervalDays}
                onChange={(event) => setRecurrenceIntervalDays(event.target.value)}
                placeholder="90"
                className="w-28"
              />
              <span className="text-[13px] text-ink-secondary">days</span>
            </div>
          </Field>

          {error ? (
            <p
              role="alert"
              className="rounded-control bg-danger-tint px-3 py-2 text-[12.5px] text-danger-text"
            >
              {error}
            </p>
          ) : null}
        </div>

        <footer className="flex items-center justify-between gap-3 border-t border-line
                           bg-surface-subtle px-5 py-3">
          <p className="text-[11.5px] text-ink-secondary">
            Lands in your intake column for triage.
          </p>
          <div className="flex items-center gap-2">
            <Button size="sm" onClick={requestClose} disabled={isPending}>
              Cancel
            </Button>
            <Button variant="primary" size="sm" onClick={submit} disabled={isPending}>
              {isPending ? "Creating…" : "Create task"}
            </Button>
          </div>
        </footer>
      </div>
    </>
  );
}
