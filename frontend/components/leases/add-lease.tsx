"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { CloudUpload, Keyboard, Plus, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import { ChoiceDialog } from "@/components/ui/choice-dialog";
import type { Property, Tenant, Unit } from "@/lib/api";

import { createLeaseAction, type LeaseInput } from "@/app/leases/actions";

/**
 * Recording a lease.
 *
 * <h3>No document is required, and that is the point</h3>
 *
 * The document pipeline reads a scan and proposes terms so nobody has to type
 * them. It is a convenience, not the source of truth. A landlord with a paper
 * lease in a drawer, a lease signed before this system existed, or an
 * agreement made over the phone still has a lease, and refusing to record it
 * without a PDF would make the system useless for exactly the cases it is
 * meant to help with.
 *
 * So the terms are the object here. A lease entered this way starts in the
 * same state as one proposed by an extraction — pending review, in draft — and
 * goes through the same approval before anything is sent.
 *
 * <h3>Why only the property is required</h3>
 *
 * Because everything else can be filled in later and a half-known lease is
 * still worth recording. What a lease cannot be missing is the property: the
 * property carries the time zone, and the time zone decides what calendar date
 * rent is due. A lease with no property has no answer to "when".
 *
 * Reminders need rather more than this, which the lease screen says plainly
 * once the lease exists rather than blocking its creation here.
 */
export function AddLease({
  organizationId,
  properties,
  unitsByProperty,
  tenants,
  canWrite,
  autoOpen = false,
}: {
  organizationId: string;
  properties: Property[];
  /** Keyed by property id. Empty for a property with no units yet. */
  unitsByProperty: Record<string, Unit[]>;
  tenants: Tenant[];
  canWrite: boolean;
  /**
   * Opens the chooser on mount, for the dashboard's "Add lease" link.
   *
   * Read once. Keeping it in step with the URL afterwards would mean
   * reopening the dialog every time somebody dismissed it without
   * navigating away.
   */
  autoOpen?: boolean;
}) {
  const router = useRouter();

  // Three states, not two. "choosing" is the fork between typing the terms
  // and letting the document pipeline read them; "form" is the terms
  // themselves. Opening straight onto the form is what hid the scanner from
  // everybody who did not already know the Documents page existed.
  const [stage, setStage] = useState<"closed" | "choosing" | "form">(
    // The initialiser rather than an effect: an effect would render the page
    // once without the dialog and then again with it, which flashes.
    autoOpen && canWrite && properties.length > 0 ? "choosing" : "closed",
  );
  const isOpen = stage === "form";
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const [form, setForm] = useState<LeaseInput>(() => emptyForm(properties));

  // Order matters, so this is an array rather than a Set: the first tenant
  // named becomes the primary one, and the primary tenant is who a rent
  // reminder is addressed to.
  const [tenantIds, setTenantIds] = useState<string[]>([]);

  const units = unitsByProperty[form.propertyId] ?? [];
  const selectedProperty = properties.find(
    (property) => property.id === form.propertyId,
  );

  function openForm() {
    setForm(emptyForm(properties));
    setTenantIds([]);
    setError(null);
    setFieldErrors({});
    setStage("form");
  }

  function update<K extends keyof LeaseInput>(key: K, value: LeaseInput[K]) {
    setForm((previous) => ({ ...previous, [key]: value }));
  }

  function toggleTenant(tenantId: string) {
    setTenantIds((previous) =>
      previous.includes(tenantId)
        ? previous.filter((id) => id !== tenantId)
        : [...previous, tenantId],
    );
  }

  function submit() {
    setError(null);
    setFieldErrors({});

    if (!form.propertyId) {
      setError("Choose the property this lease is for.");

      return;
    }

    // Caught here rather than by the API, because the API's message for it
    // would be about a foreign key and this one is about the lease.
    if (form.startDate && form.endDate && form.endDate < form.startDate) {
      setError("The end date is before the start date.");

      return;
    }

    startTransition(async () => {
      const result = await createLeaseAction(organizationId, {
        ...form,
        tenantIds,
      });

      if (!result.ok) {
        setError(result.error ?? "This lease could not be created.");
        setFieldErrors(result.fieldErrors ?? {});

        return;
      }

      setStage("closed");

      // Straight to the lease, because creating it is the beginning of the
      // job rather than the end: it still has to be reviewed and approved
      // before it sends anything, and that is where this says so.
      if (result.leaseId) {
        router.push(`/leases/${result.leaseId}`);
      } else {
        router.refresh();
      }
    });
  }

  if (!canWrite) {
    return null;
  }

  // A lease hangs off a property, so there is nothing to offer until one
  // exists. Said here rather than letting the form open onto an empty list.
  if (properties.length === 0) {
    return (
      <Button variant="primary" disabled title="Add a property first">
        <Plus className="size-4" aria-hidden="true" />
        Add lease
      </Button>
    );
  }

  return (
    <>
      <Button variant="primary" onClick={() => setStage("choosing")}>
        <Plus className="size-4" aria-hidden="true" />
        Add lease
      </Button>

      {stage === "choosing" ? (
        <ChoiceDialog
          title="Add a lease"
          description="Both routes end in the same place: terms you check and approve before anything is sent."
          onCancel={() => setStage("closed")}
          options={[
            {
              key: "scan",
              label: "Upload or scan the document",
              description:
                "A PDF, a photo of the pages, or a Word file. The terms are read out of it and "
                + "presented for you to check, so most of the fields are already filled in.",
              icon: CloudUpload,
              recommended: true,
              onSelect: () => {
                setStage("closed");
                router.push("/documents");
              },
            },
            {
              key: "manual",
              label: "Enter the terms myself",
              description:
                "For a lease agreed on paper, over the phone, or signed long before this system "
                + "existed. Only the property is required; the rest can follow.",
              icon: Keyboard,
              onSelect: openForm,
            },
          ]}
        />
      ) : null}

      {isOpen ? (
        <FormDialog
          title="Add a lease"
          description="No document needed. The terms are what matter, and nothing is sent until you approve them."
          submitLabel="Create lease"
          isSubmitting={isPending}
          onSubmit={submit}
          onCancel={() => setStage("closed")}
        >
          <Field
            label="Property"
            htmlFor="lease-property"
            required
            hint={
              selectedProperty
                ? `Rent dates are read in ${selectedProperty.timeZone}.`
                : "The property decides what time zone rent dates are read in."
            }
            error={fieldErrors["PropertyId"]?.[0] ?? null}
          >
            <select
              id="lease-property"
              value={form.propertyId}
              onChange={(event) => {
                update("propertyId", event.target.value);
                // The old unit belongs to the old property, and the composite
                // key would refuse it. Cleared rather than silently sent.
                update("unitId", null);
              }}
              className={fieldClass}
            >
              {properties.map((property) => (
                <option key={property.id} value={property.id}>
                  {property.name}
                </option>
              ))}
            </select>
          </Field>

          {units.length > 0 ? (
            <Field
              label="Unit"
              htmlFor="lease-unit"
              hint="Which door. A single-family house usually has one."
            >
              <select
                id="lease-unit"
                value={form.unitId ?? ""}
                onChange={(event) => update("unitId", event.target.value || null)}
                className={fieldClass}
              >
                <option value="">Not specified</option>
                {units.map((unit) => (
                  <option key={unit.id} value={unit.id}>
                    {unit.unitNumber}
                  </option>
                ))}
              </select>
            </Field>
          ) : null}

          <Field
            label="Reference"
            htmlFor="lease-reference"
            hint="Your own name for this lease. Shown in lists."
          >
            <input
              id="lease-reference"
              value={form.reference ?? ""}
              onChange={(event) => update("reference", event.target.value)}
              placeholder="Maple Street — Unit 2 — 2026"
              maxLength={200}
              className={fieldClass}
            />
          </Field>

          <SectionLabel>Term</SectionLabel>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Starts" htmlFor="lease-start">
              <input
                id="lease-start"
                type="date"
                value={form.startDate ?? ""}
                onChange={(event) => update("startDate", event.target.value)}
                className={fieldClass}
              />
            </Field>

            <Field label="Ends" htmlFor="lease-end">
              <input
                id="lease-end"
                type="date"
                value={form.endDate ?? ""}
                onChange={(event) => update("endDate", event.target.value)}
                className={fieldClass}
              />
            </Field>
          </div>

          <SectionLabel>Rent</SectionLabel>

          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Amount" htmlFor="lease-rent">
              <input
                id="lease-rent"
                type="number"
                step="0.01"
                min="0"
                value={form.rentAmount ?? ""}
                onChange={(event) => update("rentAmount", event.target.value)}
                className={fieldClass}
              />
            </Field>

            <Field label="Currency" htmlFor="lease-currency">
              <input
                id="lease-currency"
                value={form.currency ?? ""}
                onChange={(event) =>
                  update("currency", event.target.value.toUpperCase())
                }
                maxLength={3}
                placeholder="USD"
                className={fieldClass}
              />
            </Field>

            <Field
              label="First payment"
              htmlFor="lease-first-payment"
              hint="Anchors the schedule."
            >
              <input
                id="lease-first-payment"
                type="date"
                value={form.firstPaymentDate ?? ""}
                onChange={(event) => update("firstPaymentDate", event.target.value)}
                className={fieldClass}
              />
            </Field>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Security deposit" htmlFor="lease-deposit">
              <input
                id="lease-deposit"
                type="number"
                step="0.01"
                min="0"
                value={form.securityDepositAmount ?? ""}
                onChange={(event) =>
                  update("securityDepositAmount", event.target.value)
                }
                className={fieldClass}
              />
            </Field>

            <Field
              label="Grace period"
              htmlFor="lease-grace"
              hint="Days after the due date before rent counts as late."
            >
              <input
                id="lease-grace"
                type="number"
                min="0"
                max="60"
                value={form.gracePeriodDays ?? ""}
                onChange={(event) => update("gracePeriodDays", event.target.value)}
                className={fieldClass}
              />
            </Field>
          </div>

          <SectionLabel>Late fee</SectionLabel>

          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Fee" htmlFor="lease-late-fee">
              <input
                id="lease-late-fee"
                type="number"
                step="0.01"
                min="0"
                value={form.lateFeeAmount ?? ""}
                onChange={(event) => update("lateFeeAmount", event.target.value)}
                className={fieldClass}
              />
            </Field>

            <Field label="Charged as" htmlFor="lease-late-fee-kind">
              <select
                id="lease-late-fee-kind"
                value={form.lateFeeIsPercentage ? "percent" : "fixed"}
                onChange={(event) =>
                  update("lateFeeIsPercentage", event.target.value === "percent")
                }
                className={fieldClass}
              >
                <option value="fixed">A fixed amount</option>
                <option value="percent">A percentage of rent</option>
              </select>
            </Field>

            <Field label="After" htmlFor="lease-late-after" hint="Days late.">
              <input
                id="lease-late-after"
                type="number"
                min="0"
                max="90"
                value={form.lateFeeAfterDays ?? ""}
                onChange={(event) => update("lateFeeAfterDays", event.target.value)}
                className={fieldClass}
              />
            </Field>
          </div>

          <SectionLabel>Tenants</SectionLabel>

          {tenants.length === 0 ? (
            <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                          text-[12.5px] leading-relaxed text-ink-secondary">
              No tenants have been added yet. The lease can be created without
              them and they can be named later — but a rent reminder has nobody
              to go to until one is.
            </p>
          ) : (
            <div>
              <ul className="divide-y divide-line rounded-control border border-line">
                {tenants.map((tenant) => {
                  const position = tenantIds.indexOf(tenant.id);

                  return (
                    <li key={tenant.id}>
                      <label
                        className="flex cursor-pointer items-center gap-2.5 px-3 py-2
                                   transition-colors hover:bg-surface-subtle"
                      >
                        <input
                          type="checkbox"
                          checked={position >= 0}
                          onChange={() => toggleTenant(tenant.id)}
                          className="size-3.5 accent-accent"
                        />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-[13px] font-medium">
                            {tenant.preferredName ?? tenant.legalName}
                          </span>
                          {tenant.primaryEmail ? (
                            <span className="block truncate text-[11.5px] text-ink-secondary">
                              {tenant.primaryEmail}
                            </span>
                          ) : (
                            <span className="block text-[11.5px] text-warning-text">
                              No email address — cannot be sent reminders
                            </span>
                          )}
                        </span>
                        {position === 0 ? (
                          <span className="shrink-0 rounded-control bg-accent-tint px-1.5
                                           py-0.5 text-[11px] font-semibold text-accent">
                            Primary
                          </span>
                        ) : position > 0 ? (
                          <span className="shrink-0 text-[11px] text-ink-muted">
                            Co-tenant
                          </span>
                        ) : null}
                      </label>
                    </li>
                  );
                })}
              </ul>

              <p className="mt-1 text-[12px] leading-relaxed text-ink-secondary">
                The first one you tick is the primary tenant, which is who a
                rent reminder is addressed to. The rest are co-tenants.
              </p>
            </div>
          )}

          <SectionLabel>Anything else</SectionLabel>

          <Field
            label="Payment instructions"
            htmlFor="lease-payment-instructions"
            hint="Included in reminders, so write it as the tenant should read it."
          >
            <textarea
              id="lease-payment-instructions"
              value={form.paymentInstructions ?? ""}
              onChange={(event) =>
                update("paymentInstructions", event.target.value)
              }
              rows={2}
              maxLength={2000}
              className={fieldClass}
            />
          </Field>

          <Field label="Notes" htmlFor="lease-notes" hint="Internal. Never sent.">
            <textarea
              id="lease-notes"
              value={form.notes ?? ""}
              onChange={(event) => update("notes", event.target.value)}
              rows={2}
              maxLength={10000}
              className={fieldClass}
            />
          </Field>

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

          <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12px] leading-relaxed text-ink-secondary">
            The lease is created as a draft awaiting review. Nothing is sent to
            anybody until you approve it, and you can change any of this before
            you do.
          </p>
        </FormDialog>
      ) : null}
    </>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="border-b border-line pb-1 text-[11.5px] font-semibold tracking-wide
                  text-ink-secondary uppercase">
      {children}
    </p>
  );
}

function emptyForm(properties: Property[]): LeaseInput {
  return {
    propertyId: properties[0]?.id ?? "",
    unitId: null,
    reference: "",
    startDate: "",
    endDate: "",
    rentAmount: "",
    currency: "USD",
    securityDepositAmount: "",
    firstPaymentDate: "",
    gracePeriodDays: "",
    lateFeeAmount: "",
    lateFeeIsPercentage: false,
    lateFeeAfterDays: "",
    renewalNoticeDays: "",
    paymentInstructions: "",
    notes: "",
    tenantIds: [],
  };
}
