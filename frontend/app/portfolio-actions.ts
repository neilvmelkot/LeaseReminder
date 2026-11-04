"use server";

import { revalidatePath } from "next/cache";

import { ApiError, apiFetch } from "@/lib/api";

/**
 * Creating the things a portfolio is made of.
 *
 * <p>
 * Server actions rather than browser fetches, for the same reason as
 * everywhere else: the access token lives in an http-only cookie that only
 * server-side code can read.
 * </p>
 * <p>
 * Each returns a result rather than throwing. A thrown error in a server
 * action reaches the client as an opaque digest with the message stripped,
 * and the message here is the useful part — the API's validator names the
 * field that is wrong.
 * </p>
 */

export interface ActionResult {
  ok: boolean;
  error?: string;
  fieldErrors?: Record<string, string[]>;
  /**
   * The HTTP status, when the API answered at all.
   *
   * Carried because 409 means something specific here: the request was
   * understood and deliberately refused, and the message explains why. A
   * caller can act on that differently from a 400 or a network failure.
   */
  status?: number;
}

export interface PropertyInput {
  name: string;
  propertyType: string;
  timeZone: string;
  addressLine1?: string | null;
  locality?: string | null;
  region?: string | null;
  postalCode?: string | null;
  countryCode?: string | null;
  notes?: string | null;
}

export async function createPropertyAction(
  organizationId: string,
  input: PropertyInput,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(`/api/v1/organizations/${organizationId}/properties`, {
      method: "POST",
      body: JSON.stringify({
        name: input.name.trim(),
        propertyType: input.propertyType,
        timeZone: input.timeZone,
        addressLine1: blankToNull(input.addressLine1),
        addressLine2: null,
        locality: blankToNull(input.locality),
        region: blankToNull(input.region),
        postalCode: blankToNull(input.postalCode),
        countryCode: blankToNull(input.countryCode) ?? "US",
        notes: blankToNull(input.notes),
      }),
    });

    // The dashboard counts properties and the lease form lists them, so both
    // are stale the moment this succeeds.
    revalidatePath("/properties");
    revalidatePath("/");
    revalidatePath("/tenants");

    return { ok: true };
  } catch (error) {
    return describe(error, "add this property");
  }
}

/**
 * Editing a property.
 *
 * <p>
 * The API treats this as a full replacement of the editable fields, so the
 * form sends every one of them — including the ones left untouched. A partial
 * body would read as "clear these", which is how an address silently
 * disappears because somebody only meant to fix a postcode.
 * </p>
 * <p>
 * Changing the time zone is the consequential edit here: it decides what
 * calendar date rent is due at this property. Existing rent schedules keep
 * the zone they were generated with, so past due dates do not move, but
 * future generation follows the new value. The API audits the change with
 * both sides recorded.
 * </p>
 */
export async function updatePropertyAction(
  organizationId: string,
  propertyId: string,
  input: PropertyInput,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}`,
      {
        method: "PATCH",
        body: JSON.stringify({
          name: input.name.trim(),
          propertyType: input.propertyType,
          timeZone: input.timeZone,
          addressLine1: blankToNull(input.addressLine1),
          addressLine2: null,
          locality: blankToNull(input.locality),
          region: blankToNull(input.region),
          postalCode: blankToNull(input.postalCode),
          countryCode: blankToNull(input.countryCode) ?? "US",
          notes: blankToNull(input.notes),
        }),
      },
    );

    revalidatePath("/properties");
    revalidatePath("/properties/[propertyId]", "page");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "save this property");
  }
}

/**
 * Removing a property.
 *
 * <h3>Why there are three of these and not one</h3>
 *
 * "Get rid of this" is two different operations, and which one applies is a
 * fact about the property rather than a preference.
 *
 * A property nothing references can genuinely be deleted — the one added
 * with a typo in its name. A property with leases under it cannot, because
 * that would take its rent schedules, sent mail and audit trail with it;
 * that one is archived, which hides it and keeps everything.
 *
 * Neither function here decides which. app.delete_property counts what
 * points at the property and refuses if anything does, naming the counts;
 * the API turns that refusal into a 409 and passes the sentence through. So
 * the UI can offer delete, and explain accurately when it is not available,
 * without keeping its own copy of the rule.
 */
export async function deletePropertyAction(
  organizationId: string,
  propertyId: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}`,
      { method: "DELETE" },
    );

    revalidatePath("/properties");
    revalidatePath("/");
    revalidatePath("/tenants");

    return { ok: true };
  } catch (error) {
    return describe(error, "delete this property");
  }
}

export async function archivePropertyAction(
  organizationId: string,
  propertyId: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}/archive`,
      { method: "POST" },
    );

    revalidatePath("/properties");
    revalidatePath("/properties/[propertyId]", "page");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "archive this property");
  }
}

export async function restorePropertyAction(
  organizationId: string,
  propertyId: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}/restore`,
      { method: "POST" },
    );

    revalidatePath("/properties");
    revalidatePath("/properties/[propertyId]", "page");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "restore this property");
  }
}

export interface TenantInput {
  legalName: string;
  preferredName?: string | null;
  primaryEmail?: string | null;
  phone?: string | null;
  notificationsOptedIn: boolean;
  notes?: string | null;
}

export async function createTenantAction(
  organizationId: string,
  input: TenantInput,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(`/api/v1/organizations/${organizationId}/tenants`, {
      method: "POST",
      body: JSON.stringify({
        legalName: input.legalName.trim(),
        preferredName: blankToNull(input.preferredName),
        primaryEmail: blankToNull(input.primaryEmail),
        secondaryEmail: null,
        phone: blankToNull(input.phone),
        status: "active",
        notificationsOptedIn: input.notificationsOptedIn,
        notes: blankToNull(input.notes),
      }),
    });

    revalidatePath("/tenants");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "add this tenant");
  }
}

/**
 * An empty string is not a value.
 *
 * A form always produces strings, so an untouched optional field arrives as
 * "". Sending that would store an empty address line rather than no address
 * line, and the two read differently everywhere downstream.
 */
function blankToNull(value: string | null | undefined): string | null {
  const trimmed = value?.trim();

  return trimmed ? trimmed : null;
}

function describe(error: unknown, attempted: string): ActionResult {
  if (error instanceof ApiError) {
    if (error.isUnauthenticated) {
      return { ok: false, error: "Your session has expired. Sign in again." };
    }

    return {
      ok: false,
      error: error.message,
      status: error.status,
      ...(error.fieldErrors ? { fieldErrors: error.fieldErrors } : {}),
    };
  }

  return {
    ok: false,
    error: `The server could not be reached, so it was not possible to ${attempted}.`,
  };
}
