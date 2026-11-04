"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Archive, ArchiveRestore, Trash2, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import type { Property } from "@/lib/api";

import {
  archivePropertyAction,
  deletePropertyAction,
  restorePropertyAction,
} from "@/app/portfolio-actions";

/**
 * Taking a property out of use, and getting rid of it.
 *
 * <h3>Why deleting is only reachable from the archived state</h3>
 *
 * Archiving is reversible and deleting is not, so they do not belong side by
 * side: a pair of buttons where one is recoverable and the other is permanent
 * invites the permanent one by accident.
 *
 * Making archive the only first step also means nothing is ever deleted while
 * it is still in the portfolio. By the time Delete is visible the property has
 * already been withdrawn, somebody has seen it leave the list, and anything
 * still depending on it has had a chance to surface.
 *
 * <h3>Why Delete is offered and the answer comes from the database</h3>
 *
 * Whether a property can really be deleted depends on what points at it —
 * units, leases, documents, tasks. This screen could count those and decide,
 * but it would be deciding from a copy of the data, and a stale one: a lease
 * created in another tab a moment ago would not be in it.
 *
 * So Delete is offered, the database answers, and its refusal names the
 * counts. The dialog shows that sentence. The answer is right because it came
 * from the only place that knows.
 */
export function PropertyLifecycleControls({
  organizationId,
  property,
}: {
  organizationId: string;
  property: Property;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [isConfirmingDelete, setIsConfirmingDelete] = useState(false);

  // Set when the database refuses a delete. Its message names what is still
  // attached, so it becomes the dialog's explanation.
  const [refusal, setRefusal] = useState<string | null>(null);

  // Compared against the property's own name, so confirming means reading it
  // rather than clicking through.
  const [typed, setTyped] = useState("");

  // Nullish, not strict: correct whether the field arrives as null or absent.
  const isArchived = property.archivedAt != null;

  function run(
    action: () => Promise<{ ok: boolean; error?: string; status?: number }>,
    fallback: string,
    onSuccess?: () => void,
  ) {
    setError(null);

    startTransition(async () => {
      const result = await action();

      if (result.ok) {
        onSuccess?.();
        router.refresh();

        return;
      }

      // A 409 is a considered refusal rather than a failure.
      if (result.status === 409) {
        setRefusal(
          result.error ?? "This property still has records attached to it.",
        );

        return;
      }

      setError(result.error ?? fallback);
    });
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <div className="flex flex-wrap items-center justify-end gap-2">
        {isArchived ? (
          <>
            <Button
              variant="secondary"
              size="sm"
              disabled={isPending}
              onClick={() =>
                run(
                  () => restorePropertyAction(organizationId, property.id),
                  "This property could not be restored.",
                )
              }
            >
              <ArchiveRestore className="size-3.5" aria-hidden="true" />
              {isPending ? "Working…" : "Restore"}
            </Button>

            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setError(null);
                setRefusal(null);
                setTyped("");
                setIsConfirmingDelete(true);
              }}
            >
              <Trash2 className="size-3.5" aria-hidden="true" />
              Delete
            </Button>
          </>
        ) : (
          <Button
            variant="secondary"
            size="sm"
            disabled={isPending}
            onClick={() =>
              run(
                () => archivePropertyAction(organizationId, property.id),
                "This property could not be archived.",
              )
            }
          >
            <Archive className="size-3.5" aria-hidden="true" />
            {isPending ? "Archiving…" : "Archive"}
          </Button>
        )}
      </div>

      {error ? (
        <p
          role="alert"
          className="reveal max-w-xs text-right text-[12px] text-danger-text"
        >
          {error}
        </p>
      ) : null}

      {isConfirmingDelete ? (
        <FormDialog
          title={
            refusal ? "This property cannot be deleted" : "Delete this property?"
          }
          description={
            refusal
              ? "It stays archived, which keeps everything it carries."
              : "This cannot be undone. Only a property with nothing attached to it can be deleted."
          }
          submitLabel={refusal ? "Keep it archived" : "Delete permanently"}
          isSubmitting={isPending}
          isSubmitDisabled={!refusal && typed.trim() !== property.name}
          onSubmit={() => {
            if (refusal) {
              setIsConfirmingDelete(false);

              return;
            }

            run(
              () => deletePropertyAction(organizationId, property.id),
              "This property could not be deleted.",
              () => {
                setIsConfirmingDelete(false);
                // Nothing left to come back to, so leave rather than render a
                // page for a property that no longer exists.
                router.push("/properties");
              },
            );
          }}
          onCancel={() => setIsConfirmingDelete(false)}
        >
          <div className="rounded-control border border-line bg-surface-subtle px-3 py-2.5">
            <p className="text-[13px] font-medium">{property.name}</p>
            <p className="mt-0.5 text-[12px] text-ink-secondary">
              {property.unitCount} unit
              {property.unitCount === 1 ? "" : "s"} · {property.activeLeaseCount}{" "}
              active lease
              {property.activeLeaseCount === 1 ? "" : "s"}
            </p>
          </div>

          {refusal ? (
            <p
              className="reveal rounded-control border border-warning/40 bg-warning/10
                         px-3 py-2 text-[12.5px] leading-relaxed text-warning-text"
            >
              {refusal}
            </p>
          ) : (
            <>
              <p className="text-[12.5px] leading-relaxed text-ink-secondary">
                Everything this property carries — its units, leases, rent
                history and audit trail — would go with it. If any of that
                still exists, nothing is deleted and it stays archived.
              </p>

              <Field
                label="Type the property's name to confirm"
                htmlFor="confirm-delete"
                required
                hint="Case and spacing have to match."
              >
                <input
                  id="confirm-delete"
                  value={typed}
                  onChange={(event) => setTyped(event.target.value)}
                  placeholder={property.name}
                  autoComplete="off"
                  className={fieldClass}
                />
              </Field>
            </>
          )}

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
        </FormDialog>
      ) : null}
    </div>
  );
}
