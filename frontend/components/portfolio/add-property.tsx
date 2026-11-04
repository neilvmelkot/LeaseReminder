"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Plus, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { FormDialog } from "@/components/ui/form-dialog";
import {
  PropertyFormFields,
  type PropertyFormState,
} from "@/components/portfolio/property-form-fields";

import { createPropertyAction } from "@/app/portfolio-actions";

/**
 * Adding a property.
 *
 * <h3>On the time zone field</h3>
 *
 * It defaults to the workspace's, and it is editable, because this is the one
 * that actually decides when a tenant is told to pay. A rent due date is a
 * calendar date in the property's own zone; a property two time zones from
 * head office inheriting head office's zone is how a reminder goes out on the
 * wrong day.
 */
export function AddProperty({
  organizationId,
  defaultTimeZone,
  canWrite,
}: {
  organizationId: string;
  defaultTimeZone: string;
  canWrite: boolean;
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const [form, setForm] = useState<PropertyFormState>({
    name: "",
    propertyType: "singleFamily",
    timeZone: defaultTimeZone,
    addressLine1: "",
    locality: "",
    region: "",
    postalCode: "",
  });

  function update(key: keyof PropertyFormState, value: string) {
    setForm((previous) => ({ ...previous, [key]: value }));
  }

  function submit() {
    setError(null);
    setFieldErrors({});

    if (form.name.trim().length === 0) {
      setError("Give the property a name.");

      return;
    }

    startTransition(async () => {
      const result = await createPropertyAction(organizationId, form);

      if (!result.ok) {
        setError(result.error ?? "The property could not be added.");
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

  return (
    <>
      <Button variant="primary" size="sm" onClick={() => setIsOpen(true)}>
        <Plus className="size-3.5" aria-hidden="true" />
        Add property
      </Button>

      {isOpen ? (
        <FormDialog
          title="Add a property"
          description="Units, leases, and tenants all hang off a property, so this comes first."
          submitLabel="Add property"
          isSubmitting={isPending}
          onSubmit={submit}
          onCancel={() => setIsOpen(false)}
        >
          <PropertyFormFields
            form={form}
            onChange={update}
            fieldErrors={fieldErrors}
            idPrefix="property"
          />

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
