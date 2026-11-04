"use server";

import { revalidatePath } from "next/cache";

import { ApiError, apiFetch } from "@/lib/api";

/**
 * Reviewing, approving, naming and removing lease documents.
 *
 * <p>
 * Server actions rather than browser fetches, because the access token lives
 * in an http-only cookie only server-side code can read.
 * </p>
 * <p>
 * Each returns a result rather than throwing. A thrown error in a server
 * action reaches the browser as an opaque digest with its message stripped,
 * and the message is the useful part here — the database's refusals explain
 * themselves, and the approval gate's refusal is the whole point of it.
 * </p>
 */

export interface ActionResult {
  ok: boolean;
  error?: string;
  /** The HTTP status, when the API answered at all. */
  status?: number;
}

/**
 * Records a decision on one extracted field.
 *
 * <p>
 * The three decisions are not interchangeable. Accepting keeps the extracted
 * value; correcting replaces it with what the reviewer typed; rejecting says
 * the field should not be used at all. All three count as "decided", which is
 * what the approval gate requires — approval is refused while any field is
 * still untouched, so this is how a document becomes approvable.
 * </p>
 * <p>
 * The corrected value is sent as JSON text because the column is jsonb: a
 * lease term may be a number, a date, a string or a boolean, and the database
 * stores it with its type rather than flattening everything to text.
 * </p>
 */
export async function reviewFieldAction(
  fieldId: string,
  decision: "accepted" | "corrected" | "rejected",
  correctedValue?: unknown,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(`/api/v1/extracted-fields/${fieldId}/review`, {
      method: "POST",
      body: JSON.stringify({
        decision,
        correctedValue:
          correctedValue === undefined || correctedValue === null
            ? null
            : JSON.stringify(correctedValue),
      }),
    });

    return { ok: true };
  } catch (error) {
    return describe(error, "record that decision");
  }
}

/**
 * Approves the lease terms read from a document.
 *
 * <h3>Why termsReviewed is sent as a literal true</h3>
 *
 * The API refuses an approval that does not carry it, so that approval cannot
 * quietly become an empty POST from somewhere that never showed anybody the
 * terms. Sending it here is the client's half of that: this action is only
 * ever called from the review screen's confirmation dialog.
 *
 * <h3>What actually decides whether this succeeds</h3>
 *
 * Not this function, and not the screen. When an extraction id is present the
 * database refuses the approval unless every field of it has been decided and
 * no blocking issue is still open. The UI disables the button under the same
 * conditions so a user is not invited to click and be refused — but the
 * database is the authority, and a disagreement between the two shows up as a
 * refusal with a message rather than as a lease approved on unreviewed terms.
 */
export async function approveLeaseAction(
  organizationId: string,
  leaseId: string,
  options: { extractionId: string | null; note: string | null },
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/approve`,
      {
        method: "POST",
        body: JSON.stringify({
          termsReviewed: true,
          note: options.note,
          extractionId: options.extractionId,
        }),
      },
    );

    // Approval is what lets reminders be scheduled, so the pages that report
    // on that are stale the moment it succeeds.
    revalidatePath("/documents");
    revalidatePath("/leases");
    revalidatePath("/reminders");
    revalidatePath("/");

    return { ok: true };
  } catch (error) {
    return describe(error, "approve this lease");
  }
}

/**
 * Renames a document, or clears the name.
 *
 * <p>
 * An empty name is a valid request: it removes the label and the document goes
 * back to showing the filename it was uploaded with. That filename is never
 * altered by any of this — it is the record of what arrived.
 * </p>
 */
export async function renameDocumentAction(
  documentId: string,
  name: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(`/api/v1/documents/${documentId}`, {
      method: "PATCH",
      body: JSON.stringify({ name: name.trim() || null }),
    });

    revalidatePath("/documents");
    revalidatePath("/documents/[documentId]", "page");

    return { ok: true };
  } catch (error) {
    return describe(error, "rename this document");
  }
}

/**
 * Removes a document from the list.
 *
 * <p>
 * Soft, and the wording in the UI should not promise otherwise. The stored
 * object stays until a retention run removes it and records that it has gone,
 * because "the user deleted this" and "the bytes are gone" are different
 * facts and the retention policy needs both.
 * </p>
 */
export async function deleteDocumentAction(
  documentId: string,
): Promise<ActionResult> {
  try {
    await apiFetch<unknown>(`/api/v1/documents/${documentId}`, {
      method: "DELETE",
    });

    revalidatePath("/documents");

    return { ok: true };
  } catch (error) {
    return describe(error, "delete this document");
  }
}

function describe(error: unknown, attempted: string): ActionResult {
  if (error instanceof ApiError) {
    if (error.isUnauthenticated) {
      return { ok: false, error: "Your session has expired. Sign in again." };
    }

    return { ok: false, error: error.message, status: error.status };
  }

  return {
    ok: false,
    error: `The server could not be reached, so it was not possible to ${attempted}.`,
  };
}
