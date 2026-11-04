/**
 * The role vocabulary, and what each role may do.
 *
 * <h3>Why this is its own module</h3>
 *
 * Both halves of the app need it. A server component reads the caller's role
 * to decide what to render; a client component needs the same labels to draw
 * a role picker and the same sentence to explain a refusal.
 *
 * It used to live in <code>lib/api.ts</code>, which imports the Supabase
 * server client to read the access token. Importing a label from there pulled
 * <code>next/headers</code> into the browser bundle and the build failed — so
 * the vocabulary lives here, where it has no dependencies at all, and
 * <code>lib/api.ts</code> re-exports it for the server side.
 *
 * Nothing here is enforcement. The database decides what a role may do; this
 * is how a screen explains itself instead of presenting a control that
 * returns 403. When the two disagree the database wins, which surfaces as a
 * refusal with a message rather than as silent data loss.
 */

export type OrgRole = "owner" | "admin" | "propertyManager" | "accounting" | "readOnly";

/** Roles an invitation may carry. Ownership is not transferable this way. */
export const invitableRoles: Array<{ value: OrgRole; label: string; hint: string }> = [
  {
    value: "admin",
    label: "Administrator",
    hint: "Everything an owner can do, including approving leases and connecting the mailbox",
  },
  {
    value: "propertyManager",
    label: "Property manager",
    hint: "Adds and edits properties, tenants, and maintenance; reviews leases but cannot approve them",
  },
  {
    value: "accounting",
    label: "Accounting",
    hint: "Reads leases, rent schedules, and the send log",
  },
  {
    value: "readOnly",
    label: "Read only",
    hint: "Sees the portfolio and nothing else",
  },
];

export function roleLabel(role: OrgRole | string | null): string {
  switch (role) {
    case "owner":
      return "Owner";
    case "admin":
      return "Administrator";
    case "propertyManager":
      return "Property manager";
    case "accounting":
      return "Accounting";
    case "readOnly":
      return "Read only";
    default:
      return role ?? "—";
  }
}

/**
 * What a role may do, and the sentence to show somebody who cannot.
 *
 * Derived in one place so two screens cannot disagree about what a role
 * means, and so the explanation always names the caller's actual role — that
 * is what turns "you do not have permission" into a next step.
 */
export function permissions(role: OrgRole | null | undefined) {
  const administers = role === "owner" || role === "admin";
  const writes = administers || role === "propertyManager";

  return {
    /** Add or edit properties, units, tenants, leases, and maintenance. */
    canWrite: writes,
    /** Approve a lease, connect the mailbox, invite people, edit templates. */
    canAdminister: administers,
    reason: administers
      ? null
      : writes
        ? "Your role is Property manager, which cannot do this. An owner or administrator can."
        : `Your role is ${roleLabel(role ?? null)}, which is read-only here. Ask an owner or administrator.`,
  };
}
