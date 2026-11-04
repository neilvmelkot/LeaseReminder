"use server";

import { revalidatePath } from "next/cache";

import { ApiError, membershipApi } from "@/lib/api";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

/**
 * Redeems an invitation for the signed-in user.
 *
 * <p>
 * Server-side, because the access token lives in an http-only cookie. The
 * database does the actual checking: it verifies the token's digest, that the
 * invitation is live, and that the signed-in address matches the invited one.
 * This function carries no authority of its own.
 * </p>
 */
export async function acceptInvitationAction(token: string): Promise<ActionResult> {
  try {
    await membershipApi.acceptInvitation(token);

    // Membership changes what every page shows, including the shell.
    revalidatePath("/", "layout");

    return { ok: true };
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.isUnauthenticated) {
        return { ok: false, error: "Sign in first, then open the link again." };
      }

      return { ok: false, error: error.message };
    }

    return { ok: false, error: "The server could not be reached." };
  }
}
