"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Pencil, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { FormDialog } from "@/components/ui/form-dialog";
import {
  PropertyFormFields,
  type PropertyFormState,
} from "@/components/portfolio/property-form-fields";
import type { Property } from "@/lib/api";

import { updatePropertyAction } from "@/app/portfolio-actions";

/**
 * Editing a property.
 *
 * <h3>Why the time zone warning only appears when it changes</h3>
 *
 * A caution shown every time the dialog opens is a caution nobody reads. This
 * one appears when the field actually differs from what is stored, which is
 * the only moment it is telling you something: rent dates generated from here
 * on will be read in the new zone, while schedules already generated keep the
 * zone they were built with so past due dates do not move underneath a
 * tenant.
 */
export function EditProperty({
  organizationId,
  property,
}: {
  organizationId: string;
  property: Property;
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  // Keyed by the dialog being mounted only while open, so reopening after a
  // cancelled edit starts from what is stored rather than from the abandoned
  // draft.
  const [form, setForm] = useState<PropertyFormState>(() => toForm(property));

  function open() {
    setForm(toForm(property));
    setError(null);
    setFieldErrors({});
    setIsOpen(true);
  }

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
      const result = await updatePropertyAction(organizationId, property.id, {
        ...form,
        countryCode: property.countryCode,
        notes: property.notes,
      });

      if (!result.ok) {
        setError(result.error ?? "The property could not be saved.");
        setFieldErrors(result.fieldErrors ?? {});

        return;
      }

      setIsOpen(false);
      router.refresh();
    });
  }

  const zoneChanged = form.timeZone.trim() !== property.timeZone;

  return (
    <>
      <Button variant="secondary" size="sm" onClick={open}>
        <Pencil className="size-3.5" aria-hidden="true" />
        Edit
      </Button>

      {isOpen ? (
        <FormDialog
          title="Edit property"
          description="Units, leases, and tenants keep their links to it; only these details change."
          submitLabel="Save changes"
          isSubmitting={isPending}
          onSubmit={submit}
          onCancel={() => setIsOpen(false)}
        >
          <PropertyFormFields
            form={form}
            onChange={update}
            fieldErrors={fieldErrors}
            idPrefix="edit-property"
            timeZoneHint={`Currently ${property.timeZone}. This decides what date rent is due here.`}
          />

          {zoneChanged ? (
            <p
              className="reveal rounded-control border border-warning/40 bg-warning/10
                         px-3 py-2 text-[12px] leading-relaxed text-warning-text"
            >
              Changing the time zone from{" "}
              <span className="font-medium">{property.timeZone}</span> to{" "}
              <span className="font-medium">{form.timeZone}</span> changes what
              calendar date future rent is due on. Schedules already generated
              keep the zone they were built with, so dates a tenant has already
              been told will not move.
            </p>
          ) : null}

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
    </>
  );
}

function toForm(property: Property): PropertyFormState {
  return {
    name: property.name,
    propertyType: property.propertyType,
    timeZone: property.timeZone,
    addressLine1: property.addressLine1 ?? "",
    locality: property.locality ?? "",
    region: property.region ?? "",
    postalCode: property.postalCode ?? "",
  };
}
