"use server";

import { revalidatePath } from "next/cache";

import { ApiError, api } from "@/lib/api";

export interface CreateWorkspaceResult {
  ok: boolean;
  error?: string;
  /** Per-field messages from the API's validator, when it rejected the input. */
  fieldErrors?: Record<string, string[]>;
}

/**
 * Creates the workspace.
 *
 * <p>
 * The first person to do this becomes its owner, which the database arranges:
 * a bootstrap policy in migration 0009 permits exactly one membership insert
 * for an organization that has none yet, and the API adds the caller as owner
 * in the same transaction. There is no way to create an organization and not
 * belong to it, and no second chance to claim one somebody else made.
 * </p>
 */
export async function createWorkspaceAction(
  name: string,
  timeZone: string,
): Promise<CreateWorkspaceResult> {
  try {
    await api.createOrganization({ name: name.trim(), timeZone });

    // Everything behind the shell reads the current organization, so the
    // whole tree is stale the moment this succeeds.
    revalidatePath("/", "layout");

    return { ok: true };
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.isUnauthenticated) {
        return { ok: false, error: "Your session has expired. Sign in again." };
      }

      // The API validates the time zone against the real IANA database and
      // names the offending value, which is more useful than anything that
      // could be written here.
      return {
        ok: false,
        error: error.message,
        ...(error.fieldErrors ? { fieldErrors: error.fieldErrors } : {}),
      };
    }

    return {
      ok: false,
      error: "The server could not be reached, so the workspace was not created.",
    };
  }
}
