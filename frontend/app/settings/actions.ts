"use server";

import { revalidatePath } from "next/cache";

import { ApiError, membershipApi, type OrgRole } from "@/lib/api";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

export interface InviteResult extends ActionResult {
  /**
   * The invitation link, returned once.
   *
   * The server stores only the token's SHA-256 digest, so there is no endpoint
   * that could produce this again. If it is lost, the invitation is revoked
   * and reissued rather than recovered.
   */
  link?: string;
}

export async function updateWorkspaceAction(
  organizationId: string,
  name: string,
  timeZone: string,
): Promise<ActionResult> {
  try {
    await membershipApi.updateOrganization(organizationId, {
      name: name.trim(),
      timeZone,
    });

    // The workspace name is in the shell on every page.
    revalidatePath("/", "layout");

    return { ok: true };
  } catch (error) {
    return describe(error, "save these settings");
  }
}

export async function inviteMemberAction(
  organizationId: string,
  email: string,
  role: OrgRole,
  origin: string,
): Promise<InviteResult> {
  try {
    const { token } = await membershipApi.invite(organizationId, {
      email: email.trim(),
      role,
    });

    revalidatePath("/settings");

    // Assembled here rather than on the server, because the server does not
    // know what address the browser reached it by — localhost in development,
    // a Render URL in production, possibly a custom domain later.
    return { ok: true, link: `${origin}/invite/${token}` };
  } catch (error) {
    return describe(error, "send this invitation");
  }
}

export async function revokeInvitationAction(
  invitationId: string,
): Promise<ActionResult> {
  try {
    await membershipApi.revokeInvitation(invitationId);
    revalidatePath("/settings");

    return { ok: true };
  } catch (error) {
    return describe(error, "withdraw this invitation");
  }
}

export async function removeMemberAction(
  organizationId: string,
  userId: string,
): Promise<ActionResult> {
  try {
    await membershipApi.removeMember(organizationId, userId);
    revalidatePath("/settings");

    return { ok: true };
  } catch (error) {
    return describe(error, "remove this person");
  }
}

/**
 * Changes somebody's role.
 *
 * <p>
 * Every rule about which changes are allowed lives in
 * <c>app.change_member_role</c> — ownership cannot be granted, the owner
 * cannot be demoted, and nobody may change their own role — so a refusal
 * arrives as a message written for a person and is passed straight through.
 * </p>
 */
export async function changeMemberRoleAction(
  organizationId: string,
  userId: string,
  role: OrgRole,
): Promise<ActionResult> {
  try {
    await membershipApi.changeMemberRole(organizationId, userId, role);
    revalidatePath("/settings");

    return { ok: true };
  } catch (error) {
    return describe(error, "change this role");
  }
}

function describe(error: unknown, attempted: string): ActionResult {
  if (error instanceof ApiError) {
    if (error.isUnauthenticated) {
      return { ok: false, error: "Your session has expired. Sign in again." };
    }

    return { ok: false, error: error.message };
  }

  return {
    ok: false,
    error: `The server could not be reached, so it was not possible to ${attempted}.`,
  };
}
