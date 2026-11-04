"use server";

import { revalidatePath } from "next/cache";

import { ApiError, apiFetch } from "@/lib/api";
import type { RentScheduleInput, SchedulePreview } from "@/lib/rent";

/**
 * Server actions for the rent schedule editor.
 */

export interface PreviewResult {
  ok: boolean;
  preview?: SchedulePreview;
  error?: string;
}

export interface SaveResult {
  ok: boolean;
  error?: string;
}

/**
 * Asks the API what dates this definition would produce.
 *
 * <p>
 * Runs the same calculator the worker runs. A second implementation in the
 * browser would be faster and would eventually disagree with the one that
 * actually schedules the reminders, which is the worst possible outcome for a
 * preview: confidently wrong.
 * </p>
 */
export async function previewScheduleAction(
  organizationId: string,
  leaseId: string,
  input: RentScheduleInput,
): Promise<PreviewResult> {
  try {
    const preview = await apiFetch<SchedulePreview>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/rent-schedule/preview`,
      { method: "POST", body: JSON.stringify(toRequest(input)) },
    );

    return { ok: true, preview };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "preview this schedule") };
  }
}

/**
 * Saves the definition.
 *
 * <p>
 * The dates are not generated here. Saving records what was asked for; the
 * worker expands it within seconds. Generating inline would mean a slow
 * request and a second code path doing the same job without the claim that
 * stops two of them running at once.
 * </p>
 */
export async function saveScheduleAction(
  organizationId: string,
  leaseId: string,
  input: RentScheduleInput,
): Promise<SaveResult> {
  try {
    await apiFetch<{ rentScheduleId: string }>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/rent-schedule`,
      { method: "PUT", body: JSON.stringify(toRequest(input)) },
    );

    revalidatePath("/leases/[leaseId]/schedule", "page");
    revalidatePath("/leases");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describeFailure(error, "save this schedule") };
  }
}

/**
 * Shapes the editor's state for the API.
 *
 * <p>
 * One conversion, used by both preview and save, so the thing previewed is
 * exactly the thing saved.
 * </p>
 */
function toRequest(input: RentScheduleInput) {
  return {
    frequency: input.frequency,
    dueDayRule: input.dueDayRule,
    dueDayOfMonth: input.dueDayOfMonth,
    startDate: input.startDate,
    endDate: input.endDate,
    amount: input.amount,
    currency: input.currency,
    timeZone: input.timeZone,
    shortMonthFallback: input.shortMonthFallback,
    businessDayShift: input.businessDayShift,
    holidayCalendarId: input.holidayCalendarId,
    gracePeriodDays: input.gracePeriodDays,
    lateFeeAfterDays: input.lateFeeAfterDays,
  };
}

function describeFailure(error: unknown, attempted: string): string {
  if (error instanceof ApiError) {
    if (error.isUnauthenticated) {
      return "Your session has expired. Sign in again to continue.";
    }

    // The API surfaces the calculator's own validation message, which names
    // the field that is wrong. Far more useful than anything generic.
    return error.message;
  }

  return `The server could not be reached, so it was not possible to ${attempted}.`;
}
