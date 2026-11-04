"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { Building2, Plus, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";

import { createTenantAction } from "@/app/portfolio-actions";

/**
 * Adding a tenant.
 *
 * <h3>Why this is unavailable until a property exists</h3>
 *
 * A tenant exists to be named on a lease, and a lease belongs to a property.
 * Collecting tenants with nowhere to put them produces a list of contacts that
 * does nothing, and the person who filled it in has to come back later and
 * work out which of them goes where.
 *
 * So the control says what is missing, and links to it, rather than accepting
 * input it cannot use. The database does not enforce this — a tenant row is
 * perfectly valid on its own — which is exactly why the interface has to.
 */
export function AddTenant({
  organizationId,
  propertyCount,
  canWrite,
}: {
  organizationId: string;
  propertyCount: number;
  canWrite: boolean;
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const [form, setForm] = useState({
    legalName: "",
    preferredName: "",
    primaryEmail: "",
    phone: "",
    notificationsOptedIn: true,
  });

  function update<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setForm((previous) => ({ ...previous, [key]: value }));
  }

  function submit() {
    setError(null);
    setFieldErrors({});

    if (form.legalName.trim().length === 0) {
      setError("A legal name is required.");

      return;
    }

    startTransition(async () => {
      const result = await createTenantAction(organizationId, form);

      if (!result.ok) {
        setError(result.error ?? "The tenant could not be added.");
        setFieldErrors(result.fieldErrors ?? {});

        return;
      }

      setIsOpen(false);
      router.refresh();
    });
  }

  // Nothing, not an explanation. The page says why once, in a banner
  // under its heading; saying it again in the slot where the button would be
  // is the same sentence twice and squeezes a paragraph into a button's
  // space.
  if (!canWrite) {
    return null;
  }

  if (propertyCount === 0) {
    return (
      <p
        className="flex items-start gap-2 rounded-control border border-line
                   bg-surface-subtle px-3 py-2.5 text-[12.5px] leading-relaxed
                   text-ink-secondary"
      >
        <Building2 className="mt-0.5 size-3.5 shrink-0 text-ink-muted" aria-hidden="true" />
        <span>
          Add a property first. A tenant is named on a lease, and a lease belongs
          to a property, so there is nowhere to put one yet.{" "}
          <Link href="/properties" className="font-medium text-accent hover:underline">
            Go to properties
          </Link>
        </span>
      </p>
    );
  }

  return (
    <>
      <Button variant="primary" size="sm" onClick={() => setIsOpen(true)}>
        <Plus className="size-3.5" aria-hidden="true" />
        Add tenant
      </Button>

      {isOpen ? (
        <FormDialog
          title="Add a tenant"
          description="Somebody who will be named on a lease. You can attach them to one afterwards."
          submitLabel="Add tenant"
          isSubmitting={isPending}
          onSubmit={submit}
          onCancel={() => setIsOpen(false)}
        >
          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="Legal name"
              htmlFor="tenant-legal"
              required
              hint="As it appears on the lease."
              error={fieldErrors["LegalName"]?.[0] ?? null}
            >
              <input
                id="tenant-legal"
                value={form.legalName}
                onChange={(event) => update("legalName", event.target.value)}
                placeholder="Priya Raman"
                maxLength={200}
                className={fieldClass}
              />
            </Field>

            <Field
              label="Preferred name"
              htmlFor="tenant-preferred"
              hint="What reminders will call them."
            >
              <input
                id="tenant-preferred"
                value={form.preferredName}
                onChange={(event) => update("preferredName", event.target.value)}
                placeholder="Priya"
                maxLength={200}
                className={fieldClass}
              />
            </Field>
          </div>

          <Field
            label="Email"
            htmlFor="tenant-email"
            hint="Where rent reminders go. Without one, nothing can be sent to them."
            error={fieldErrors["PrimaryEmail"]?.[0] ?? null}
          >
            <input
              id="tenant-email"
              type="email"
              value={form.primaryEmail}
              onChange={(event) => update("primaryEmail", event.target.value)}
              placeholder="priya@example.com"
              className={fieldClass}
            />
          </Field>

          <Field label="Phone" htmlFor="tenant-phone">
            <input
              id="tenant-phone"
              value={form.phone}
              onChange={(event) => update("phone", event.target.value)}
              className={fieldClass}
            />
          </Field>

          <label className="flex items-start gap-2.5 rounded-control border border-line
                            bg-surface-subtle px-3 py-2.5">
            <input
              type="checkbox"
              checked={form.notificationsOptedIn}
              onChange={(event) =>
                update("notificationsOptedIn", event.target.checked)
              }
              className="mt-0.5 size-4 accent-accent"
            />
            <span className="text-[12.5px] leading-relaxed">
              <span className="font-medium">Send reminders to this tenant</span>
              <span className="block text-ink-secondary">
                Turn this off and they receive nothing, whatever a reminder rule
                says. This is their consent, and it overrides every rule.
              </span>
            </span>
          </label>

          {error ? (
            <p
              role="alert"
              className="flex items-start gap-1.5 rounded-control border border-danger/30
                         bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}
        </FormDialog>
      ) : null}
    </>
  );
}
