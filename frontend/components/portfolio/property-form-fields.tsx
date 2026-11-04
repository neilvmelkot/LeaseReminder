"use client";

import { Field, fieldClass } from "@/components/ui/form-dialog";

export const propertyTypes = [
  { value: "singleFamily", label: "Single family" },
  { value: "multiFamily", label: "Multi family" },
  { value: "condo", label: "Condo" },
  { value: "apartment", label: "Apartment" },
  { value: "townhouse", label: "Townhouse" },
  { value: "commercial", label: "Commercial" },
  { value: "other", label: "Other" },
] as const;

export interface PropertyFormState {
  name: string;
  propertyType: string;
  timeZone: string;
  addressLine1: string;
  locality: string;
  region: string;
  postalCode: string;
}

/**
 * The fields a property is made of, shared by adding and editing.
 *
 * <h3>Why one component rather than two forms</h3>
 *
 * Add and edit must agree about what a property is. Two copies drift: a field
 * added to one, a hint reworded in the other, and eventually a property can
 * be created with something it cannot be edited to have. One component makes
 * that impossible rather than merely discouraged.
 *
 * <h3>On the time zone field</h3>
 *
 * It is editable, and on the edit form it is the consequential one. A rent due
 * date is a calendar date in the property's own zone, so this decides what day
 * a tenant is told to pay. A property two zones from head office inheriting
 * head office's zone is how a reminder goes out on the wrong day.
 */
export function PropertyFormFields({
  form,
  onChange,
  fieldErrors,
  idPrefix,
  timeZoneHint,
}: {
  form: PropertyFormState;
  onChange: (key: keyof PropertyFormState, value: string) => void;
  fieldErrors: Record<string, string[]>;
  /** Keeps ids unique when an add and an edit form exist on one page. */
  idPrefix: string;
  timeZoneHint?: string;
}) {
  return (
    <>
      <Field
        label="Name"
        htmlFor={`${idPrefix}-name`}
        required
        hint="What you call it. Shown on reminders and throughout the app."
        error={fieldErrors["Name"]?.[0] ?? null}
      >
        <input
          id={`${idPrefix}-name`}
          value={form.name}
          onChange={(event) => onChange("name", event.target.value)}
          placeholder="Maple Street"
          maxLength={200}
          className={fieldClass}
        />
      </Field>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Type" htmlFor={`${idPrefix}-type`}>
          <select
            id={`${idPrefix}-type`}
            value={form.propertyType}
            onChange={(event) => onChange("propertyType", event.target.value)}
            className={fieldClass}
          >
            {propertyTypes.map((type) => (
              <option key={type.value} value={type.value}>
                {type.label}
              </option>
            ))}
          </select>
        </Field>

        <Field
          label="Time zone"
          htmlFor={`${idPrefix}-tz`}
          hint={timeZoneHint ?? "Decides what date rent is due here."}
          error={fieldErrors["TimeZone"]?.[0] ?? null}
        >
          <input
            id={`${idPrefix}-tz`}
            value={form.timeZone}
            onChange={(event) => onChange("timeZone", event.target.value)}
            placeholder="America/New_York"
            className={fieldClass}
          />
        </Field>
      </div>

      <Field label="Street address" htmlFor={`${idPrefix}-address`}>
        <input
          id={`${idPrefix}-address`}
          value={form.addressLine1}
          onChange={(event) => onChange("addressLine1", event.target.value)}
          placeholder="14 Maple Street"
          maxLength={300}
          className={fieldClass}
        />
      </Field>

      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="City" htmlFor={`${idPrefix}-city`}>
          <input
            id={`${idPrefix}-city`}
            value={form.locality}
            onChange={(event) => onChange("locality", event.target.value)}
            className={fieldClass}
          />
        </Field>

        <Field label="State" htmlFor={`${idPrefix}-region`}>
          <input
            id={`${idPrefix}-region`}
            value={form.region}
            onChange={(event) => onChange("region", event.target.value)}
            className={fieldClass}
          />
        </Field>

        <Field label="Postcode" htmlFor={`${idPrefix}-postcode`}>
          <input
            id={`${idPrefix}-postcode`}
            value={form.postalCode}
            onChange={(event) => onChange("postalCode", event.target.value)}
            className={fieldClass}
          />
        </Field>
      </div>
    </>
  );
}
