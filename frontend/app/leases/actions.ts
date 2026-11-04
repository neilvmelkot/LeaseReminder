"use server";

import { revalidatePath } from "next/cache";

import { ApiError, apiFetch } from "@/lib/api";

/**
 * Creating and governing a lease.
 *
 * <h3>Why a lease can be created without a document</h3>
 *
 * The document pipeline is a convenience: it reads a scan and proposes terms
 * so somebody does not have to type them. It is not the source of truth, and
 * requiring it would mean a landlord with a paper lease in a drawer, or a
 * verbal agreement, or a lease signed before this system existed, could not
 * record what they have.
 *
 * So the terms are the object and the document is optional evidence. A lease
 * typed in by hand starts in exactly the same state as one proposed by an
 * extraction — pending review, in draft — and goes through the same approval.
 * The only difference is that there are no extracted fields to have reviewed,
 * which is why the approval carries no extraction id.
 */

export interface ActionResult {
  ok: boolean;
  error?: string;
  fieldErrors?: Record<string, string[]>;
  status?: number;
  /** The new lease's id, on a successful create. */
  leaseId?: string;
}

export interface LeaseInput {
  propertyId: string;
  unitId?: string | null;
  reference?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  rentAmount?: string | null;
  currency?: string | null;
  securityDepositAmount?: string | null;
  firstPaymentDate?: string | null;
  gracePeriodDays?: string | null;
  lateFeeAmount?: string | null;
  lateFeeIsPercentage: boolean;
  lateFeeAfterDays?: string | null;
  renewalNoticeDays?: string | null;
  paymentInstructions?: string | null;
  notes?: string | null;
  tenantIds: string[];
}

export async function createLeaseAction(
  organizationId: string,
  input: LeaseInput,
): Promise<ActionResult> {
  try {
    const lease = await apiFetch<{ id: string }>(
      `/api/v1/organizations/${organizationId}/leases`,
      {
        method: "POST",
        body: JSON.stringify({
          propertyId: input.propertyId,
          unitId: blankToNull(input.unitId),
          reference: blankToNull(input.reference),
          startDate: blankToNull(input.startDate),
          endDate: blankToNull(input.endDate),
          rentAmount: numberOrNull(input.rentAmount),
          currency: blankToNull(input.currency) ?? "USD",
          securityDepositAmount: numberOrNull(input.securityDepositAmount),
          firstPaymentDate: blankToNull(input.firstPaymentDate),
          gracePeriodDays: numberOrNull(input.gracePeriodDays),
          lateFeeAmount: numberOrNull(input.lateFeeAmount),
          lateFeeIsPercentage: input.lateFeeIsPercentage,
          lateFeeAfterDays: numberOrNull(input.lateFeeAfterDays),
          renewalNoticeDays: numberOrNull(input.renewalNoticeDays),
          renewalTerms: null,
          earlyTerminationTerms: null,
          noticePeriodDays: null,
          paymentInstructions: blankToNull(input.paymentInstructions),
          specialClauses: null,
          notes: blankToNull(input.notes),

          // The first tenant named is the primary one. That is not cosmetic:
          // a rent reminder addresses the primary tenant, so the order here
          // decides who is written to.
          tenants: input.tenantIds.map((tenantId, index) => ({
            tenantId,
            role: index === 0 ? "primary" : "co_tenant",
          })),
        }),
      },
    );

    revalidatePath("/leases");
    revalidatePath("/");

    return { ok: true, leaseId: lease.id };
  } catch (error) {
    return describe(error, "create this lease");
  }
}

/**
 * Approves a lease that was entered by hand.
 *
 * <p>
 * No extraction id, because there are no extracted fields: nobody has to have
 * reviewed a machine's reading of a document that does not exist. The terms
 * were typed in by the person approving them, and the confirmation dialog is
 * what records that they meant it.
 * </p>
 * <p>
 * Approval is the gate for everything downstream — a lease sends no reminders
 * until it is approved — so the reminder and dashboard views are stale the
 * moment this succeeds.
 * </p>
 */
export async function approveManualLeaseAction(
  organizationId: string,
  leaseId: string,
  note: string | null,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/approve`,
      {
        method: "POST",
        body: JSON.stringify({
          termsReviewed: true,
          note: note?.trim() || null,
          extractionId: null,
        }),
      },
    );

    revalidatePath("/leases");
    revalidatePath("/leases/[leaseId]", "page");
    revalidatePath("/reminders");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "approve this lease");
  }
}

export async function rejectLeaseAction(
  organizationId: string,
  leaseId: string,
  reason: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/reject`,
      {
        method: "POST",
        body: JSON.stringify({ reason: reason.trim() }),
      },
    );

    revalidatePath("/leases");
    revalidatePath("/leases/[leaseId]", "page");

    return { ok: true };
  } catch (error) {
    return describe(error, "reject this lease");
  }
}

/**
 * Stops or resumes the reminders for one lease.
 *
 * <p>
 * Pausing is not the same as rejecting or terminating. The lease stays
 * approved and live; only the sending stops. It exists because a dispute, a
 * payment plan, or a tenant who has asked to be contacted another way are all
 * reasons to stop emailing someone without unwinding the lease record.
 * </p>
 */
export async function setLeaseAutomationAction(
  organizationId: string,
  leaseId: string,
  paused: boolean,
  reason?: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/automation/${
        paused ? "pause" : "resume"
      }`,
      {
        method: "POST",
        body: JSON.stringify(paused ? { reason: reason?.trim() || null } : {}),
      },
    );

    revalidatePath("/leases/[leaseId]", "page");
    revalidatePath("/leases");
    revalidatePath("/reminders");

    return { ok: true };
  } catch (error) {
    return describe(
      error,
      paused ? "pause these reminders" : "resume these reminders",
    );
  }
}

/**
 * An empty string is not a value.
 *
 * A form always produces strings, so an untouched optional field arrives as
 * "". Sending that would store an empty reference rather than no reference,
 * and for a date or a number it would be rejected outright.
 */
function blankToNull(value: string | null | undefined): string | null {
  const trimmed = value?.trim();

  return trimmed ? trimmed : null;
}

/**
 * A numeric field, or null.
 *
 * Returns null rather than NaN or 0 for anything unparseable. Zero is a
 * meaningful rent and a meaningful grace period, so it must survive; "" and
 * "abc" must not become it.
 */
function numberOrNull(value: string | null | undefined): number | null {
  const trimmed = value?.trim();

  if (!trimmed) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isFinite(parsed) ? parsed : null;
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
