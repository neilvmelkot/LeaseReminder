"use server";

import { revalidatePath } from "next/cache";

import { ApiError, emailApi } from "@/lib/api";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

/**
 * Disconnects the mailbox.
 *
 * <p>
 * This destroys the stored credential rather than flagging it, and cancels
 * every pending send. A disconnect that leaves a usable token in the database
 * is not a disconnect, and one that leaves reminders queued would deliver mail
 * from an account the user believes they have unlinked.
 * </p>
 */
export async function disconnectMailboxAction(
  organizationId: string,
): Promise<ActionResult> {
  try {
    await emailApi.disconnect(organizationId);

    revalidatePath("/settings/email");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    if (error instanceof ApiError) {
      return {
        ok: false,
        error: error.isUnauthenticated
          ? "Your session has expired. Sign in again to continue."
          : error.message,
      };
    }

    return { ok: false, error: "The server could not be reached." };
  }
}

/**
 * Sends a test message to your own address.
 *
 * <p>
 * Returns success as soon as the message is queued, which is the honest
 * answer: the API cannot know whether Google accepted it, because the API
 * does not do the sending. The result appears in the send log a moment later,
 * and that is the thing worth looking at.
 * </p>
 */
export async function sendTestEmailAction(
  organizationId: string,
): Promise<ActionResult> {
  try {
    await emailApi.sendTest(organizationId);

    revalidatePath("/settings/email");

    return { ok: true };
  } catch (error) {
    if (error instanceof ApiError) {
      return {
        ok: false,
        error: error.isUnauthenticated
          ? "Your session has expired. Sign in again to continue."
          : error.message,
      };
    }

    return { ok: false, error: "The server could not be reached." };
  }
}
