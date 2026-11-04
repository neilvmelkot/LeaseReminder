import { Lock } from "lucide-react";

/**
 * Why the controls on this page are not available to you.
 *
 * <h3>Why this says which role you have</h3>
 *
 * "You do not have permission" tells somebody they are stuck without telling
 * them what to do. Naming their actual role, and who does have the authority,
 * turns it into a next step: they know whether to ask an administrator or
 * whether they have signed in as the wrong person.
 *
 * <h3>Why it appears at all, rather than hiding the controls</h3>
 *
 * A page that silently omits its actions looks broken or incomplete. A page
 * that shows them disabled, with a sentence saying why, is legible. The
 * database refuses the action independently either way — this is explanation,
 * not enforcement.
 */
export function PermissionNotice({
  reason,
  action,
}: {
  /** From `permissions(role).reason`. Null when the caller may proceed. */
  reason: string | null;
  /**
   * What they were trying to do, folded into the sentence. Optional, because
   * a page-level notice reads better without it.
   */
  action?: string;
}) {
  if (!reason) {
    return null;
  }

  return (
    <p
      className="flex items-start gap-2 rounded-control border border-line
                 bg-surface-subtle px-3 py-2.5 text-[12.5px] leading-relaxed
                 text-ink-secondary"
    >
      <Lock className="mt-0.5 size-3.5 shrink-0 text-ink-muted" aria-hidden="true" />
      <span>
        {action ? (
          <>
            <span className="font-medium text-ink">{action}</span> is not available
            to you.{" "}
          </>
        ) : null}
        {reason}
      </span>
    </p>
  );
}
