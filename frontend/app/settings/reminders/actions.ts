"use server";

import { revalidatePath } from "next/cache";

import { ApiError, reminderRuleApi, type SaveReminderRuleInput } from "@/lib/api";

/**
 * Server actions for recipients, groups, and reminder rules.
 *
 * <p>
 * Each returns a result object rather than throwing, for the same reason the
 * maintenance actions do: a thrown error reaches the client as an opaque
 * digest with the message stripped, which is correct for security and
 * useless for telling somebody their rule was rejected.
 * </p>
 */

export interface ActionResult {
  ok: boolean;
  error?: string;
  id?: string;
}

function describe(error: unknown, attempted: string): string {
  if (error instanceof ApiError) {
    if (error.isUnauthenticated) {
      return "Your session has expired. Sign in again to continue.";
    }

    if (error.isForbidden) {
      return error.message;
    }

    // 400, 409, 422 all carry a message written for a person. Surfacing it
    // is the whole point of the API returning ProblemDetails.
    if (error.status < 500) {
      return error.message;
    }
  }

  return `Could not ${attempted}. Please try again.`;
}

/**
 * Both pages, because the two are read together.
 *
 * A rule names recipients and a recipient reports how many rules use it, so
 * changing either leaves the other stale.
 */
function revalidateBoth(): void {
  revalidatePath("/settings/reminders");
  revalidatePath("/reminders");
}

/* -------------------------------------------------------------------------- */
/* Recipients                                                                  */
/* -------------------------------------------------------------------------- */

export async function saveRecipientAction(
  organizationId: string,
  recipientId: string | null,
  input: {
    email: string;
    displayName?: string | null;
    tenantId?: string | null;
    active?: boolean;
  },
): Promise<ActionResult> {
  try {
    if (recipientId) {
      await reminderRuleApi.updateRecipient(organizationId, recipientId, input);
      revalidateBoth();

      return { ok: true, id: recipientId };
    }

    const created = await reminderRuleApi.createRecipient(organizationId, input);
    revalidateBoth();

    return { ok: true, id: created.id };
  } catch (error) {
    return { ok: false, error: describe(error, "save this recipient") };
  }
}

export async function deleteRecipientAction(
  organizationId: string,
  recipientId: string,
): Promise<ActionResult> {
  try {
    await reminderRuleApi.deleteRecipient(organizationId, recipientId);
    revalidateBoth();

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "remove this recipient") };
  }
}

/* -------------------------------------------------------------------------- */
/* Groups                                                                      */
/* -------------------------------------------------------------------------- */

export async function saveGroupAction(
  organizationId: string,
  groupId: string | null,
  input: {
    name: string;
    kind?: string;
    description?: string | null;
    recipientIds: string[];
  },
): Promise<ActionResult> {
  try {
    if (groupId) {
      await reminderRuleApi.updateGroup(organizationId, groupId, input);
      revalidateBoth();

      return { ok: true, id: groupId };
    }

    const created = await reminderRuleApi.createGroup(organizationId, input);
    revalidateBoth();

    return { ok: true, id: created.id };
  } catch (error) {
    return { ok: false, error: describe(error, "save this group") };
  }
}

export async function deleteGroupAction(
  organizationId: string,
  groupId: string,
): Promise<ActionResult> {
  try {
    await reminderRuleApi.deleteGroup(organizationId, groupId);
    revalidateBoth();

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "remove this group") };
  }
}

/* -------------------------------------------------------------------------- */
/* Rules                                                                       */
/* -------------------------------------------------------------------------- */

export async function saveRuleAction(
  organizationId: string,
  ruleId: string | null,
  input: SaveReminderRuleInput,
): Promise<ActionResult> {
  try {
    if (ruleId) {
      await reminderRuleApi.updateRule(organizationId, ruleId, input);
      revalidateBoth();

      return { ok: true, id: ruleId };
    }

    const created = await reminderRuleApi.createRule(organizationId, input);
    revalidateBoth();

    return { ok: true, id: created.id };
  } catch (error) {
    return { ok: false, error: describe(error, "save this rule") };
  }
}

export async function deleteRuleAction(
  organizationId: string,
  ruleId: string,
): Promise<ActionResult> {
  try {
    await reminderRuleApi.deleteRule(organizationId, ruleId);
    revalidateBoth();

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "remove this rule") };
  }
}
