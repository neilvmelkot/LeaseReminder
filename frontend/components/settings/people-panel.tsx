"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Check, Copy, Link2, Plus, TriangleAlert, UserMinus } from "lucide-react";

import { Button, Panel, Status } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { PillTabs } from "@/components/ui/pill-tabs";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import type { Invitation, Member } from "@/lib/api";
import { invitableRoles, roleLabel, type OrgRole } from "@/lib/roles";

import {
  changeMemberRoleAction,
  inviteMemberAction,
  removeMemberAction,
  revokeInvitationAction,
} from "@/app/settings/actions";

type View = "members" | "invitations";

/**
 * Who is in this workspace, and who has been asked.
 *
 * <h3>Why the link is shown rather than emailed</h3>
 *
 * Sending it would mean the invitation arriving from the connected Gmail
 * account, which is the account that mails tenants about rent. Mixing
 * administrative mail into it is a decision worth making deliberately rather
 * than by default, and until then handing over the link is honest about what
 * is happening: somebody has to pass it on.
 *
 * It is shown exactly once. The server keeps only the token's digest, so there
 * is nothing to show again — an invitation whose link is lost is withdrawn and
 * reissued, not recovered.
 */
export function PeoplePanel({
  organizationId,
  members,
  invitations,
  currentUserId,
  canAdminister,
  permissionReason,
}: {
  organizationId: string;
  members: Member[];
  invitations: Invitation[];
  currentUserId: string | null;
  canAdminister: boolean;
  permissionReason: string | null;
}) {
  const router = useRouter();
  const [view, setView] = useState<View>("members");
  const [isInviting, setIsInviting] = useState(false);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  const [email, setEmail] = useState("");
  const [role, setRole] = useState<OrgRole>("propertyManager");
  const [removing, setRemoving] = useState<Member | null>(null);
  const [issuedLink, setIssuedLink] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  // Settled invitations stay in the list as history, but only the live ones
  // are worth a count on the tab.
  const pending = invitations.filter(
    (invitation) => !invitation.acceptedAt && !invitation.revokedAt,
  );

  function invite() {
    setError(null);

    if (!email.trim()) {
      setError("Enter an email address.");

      return;
    }

    startTransition(async () => {
      const result = await inviteMemberAction(
        organizationId,
        email,
        role,
        window.location.origin,
      );

      if (!result.ok || !result.link) {
        setError(result.error ?? "The invitation could not be created.");

        return;
      }

      setIssuedLink(result.link);
      setEmail("");
      setView("invitations");
      router.refresh();
    });
  }

  function revoke(invitationId: string) {
    setError(null);

    startTransition(async () => {
      const result = await revokeInvitationAction(invitationId);

      if (!result.ok) {
        setError(result.error ?? "The invitation could not be withdrawn.");

        return;
      }

      router.refresh();
    });
  }

  function remove() {
    if (!removing) {
      return;
    }

    const userId = removing.userId;

    setError(null);

    startTransition(async () => {
      const result = await removeMemberAction(organizationId, userId);

      if (!result.ok) {
        setError(result.error ?? "That person could not be removed.");
        setRemoving(null);

        return;
      }

      setRemoving(null);
      router.refresh();
    });
  }

  async function copyLink() {
    if (!issuedLink) {
      return;
    }

    try {
      await navigator.clipboard.writeText(issuedLink);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be refused. The link is on screen and
      // selectable, so there is still a way through.
      setCopied(false);
    }
  }

  return (
    <Panel
      title="People"
      description="Everyone here can see every lease and tenant in this workspace."
      action={
        canAdminister ? (
          <Button variant="primary" size="sm" onClick={() => setIsInviting(true)}>
            <Plus className="size-3.5" aria-hidden="true" />
            Invite
          </Button>
        ) : null
      }
      padded
    >
      <div className="space-y-3">
        <PillTabs
          ariaLabel="People"
          activeKey={view}
          onSelect={setView}
          size="sm"
          tabs={[
            { key: "members", label: "Members", count: members.length },
            { key: "invitations", label: "Invited", count: pending.length },
          ]}
        />

        {!canAdminister ? (
          <PermissionNotice reason={permissionReason} action="Inviting people" />
        ) : null}

        {error ? (
          <p
            role="alert"
            className="reveal flex items-start gap-1.5 rounded-control border
                       border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                       text-danger-text"
          >
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            {error}
          </p>
        ) : null}

        {/* The one sight of the link. */}
        {issuedLink ? (
          <div
            className="reveal rounded-control border border-accent/40 bg-accent-tint
                       px-3 py-2.5"
          >
            <p className="flex items-center gap-1.5 text-[12.5px] font-medium text-accent">
              <Link2 className="size-3.5" aria-hidden="true" />
              Send this link to them
            </p>
            <p className="mt-1 text-[12px] leading-relaxed text-ink-secondary">
              Shown once. Only its fingerprint is stored, so it cannot be
              retrieved later — withdraw and reissue the invitation if it is
              lost.
            </p>
            <div className="mt-2 flex items-center gap-2">
              <code
                className="thin-scrollbar min-w-0 flex-1 overflow-x-auto rounded-control
                           border border-line bg-surface px-2 py-1.5 font-mono text-[11.5px]"
              >
                {issuedLink}
              </code>
              <Button size="sm" variant="subtle" onClick={copyLink}>
                {copied ? (
                  <Check className="size-3.5" aria-hidden="true" />
                ) : (
                  <Copy className="size-3.5" aria-hidden="true" />
                )}
                {copied ? "Copied" : "Copy"}
              </Button>
            </div>
            <button
              type="button"
              onClick={() => setIssuedLink(null)}
              className="mt-2 text-[12px] text-ink-secondary hover:text-ink"
            >
              Done
            </button>
          </div>
        ) : null}

        {view === "members" ? (
          <ul className="divide-y divide-line">
            {members.map((member) => (
              <li
                key={member.userId}
                className="row-interactive flex items-center justify-between gap-3 py-2.5"
              >
                <div className="min-w-0">
                  <p className="truncate text-[13px] font-medium">
                    {member.displayName ?? member.email}
                    {member.userId === currentUserId ? (
                      <span className="ml-1.5 text-[12px] font-normal text-ink-secondary">
                        (you)
                      </span>
                    ) : null}
                  </p>
                  {member.displayName ? (
                    <p className="truncate text-[12px] text-ink-secondary">
                      {member.email}
                    </p>
                  ) : null}
                </div>

                <div className="flex shrink-0 items-center gap-2">
                  {/* A dropdown where a change is allowed, a chip where it is
                      not. The owner's role and your own cannot be changed —
                      the first would leave the workspace unowned, the second
                      is how a sole administrator locks themselves out — and
                      app.change_member_role refuses both regardless, so
                      offering a control that will be rejected would only
                      teach people to distrust it. */}
                  {canAdminister
                  && member.role !== "owner"
                  && member.userId !== currentUserId ? (
                    <RolePicker
                      organizationId={organizationId}
                      userId={member.userId}
                      role={member.role}
                    />
                  ) : (
                    <Status tone={member.role === "owner" ? "success" : "neutral"}>
                      {roleLabel(member.role)}
                    </Status>
                  )}

                  {/* The owner cannot be removed: the workspace would have
                      nobody who can administer it. Nor can you remove
                      yourself, which would be the same mistake by accident. */}
                  {canAdminister
                  && member.role !== "owner"
                  && member.userId !== currentUserId ? (
                    <Button
                      size="sm"
                      variant="ghost"
                      disabled={isPending}
                      onClick={() => setRemoving(member)}
                    >
                      <UserMinus className="size-3.5" aria-hidden="true" />
                      <span className="sr-only">Remove {member.email}</span>
                    </Button>
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
        ) : invitations.length === 0 ? (
          <p className="py-6 text-center text-[13px] text-ink-secondary">
            Nobody has been invited yet.
          </p>
        ) : (
          <ul className="divide-y divide-line">
            {invitations.map((invitation) => {
              const state = invitation.acceptedAt
                ? "accepted"
                : invitation.revokedAt
                  ? "withdrawn"
                  : new Date(invitation.expiresAt) < new Date()
                    ? "expired"
                    : "pending";

              return (
                <li
                  key={invitation.id}
                  className="row-interactive flex items-center justify-between gap-3 py-2.5"
                >
                  <div className="min-w-0">
                    <p className="truncate text-[13px] font-medium">
                      {invitation.email}
                    </p>
                    <p className="text-[12px] text-ink-secondary">
                      {roleLabel(invitation.role)}
                      {state === "pending"
                        ? ` · expires ${new Date(invitation.expiresAt).toLocaleDateString()}`
                        : ""}
                    </p>
                  </div>

                  <div className="flex shrink-0 items-center gap-2">
                    <Status
                      tone={
                        state === "accepted"
                          ? "success"
                          : state === "pending"
                            ? "info"
                            : "neutral"
                      }
                    >
                      {state === "accepted"
                        ? "Joined"
                        : state === "pending"
                          ? "Waiting"
                          : state === "expired"
                            ? "Expired"
                            : "Withdrawn"}
                    </Status>

                    {canAdminister && state === "pending" ? (
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={isPending}
                        onClick={() => revoke(invitation.id)}
                      >
                        Withdraw
                      </Button>
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {isInviting ? (
        <FormDialog
          title="Invite someone"
          description="They will be able to see every lease and tenant in this workspace."
          submitLabel="Create invitation"
          isSubmitting={isPending}
          onSubmit={invite}
          onCancel={() => setIsInviting(false)}
        >
          <Field
            label="Email"
            htmlFor="invite-email"
            required
            hint="The invitation can only be used by this address."
          >
            <input
              id="invite-email"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              placeholder="colleague@example.com"
              className={fieldClass}
            />
          </Field>

          <Field label="Role" htmlFor="invite-role">
            <select
              id="invite-role"
              value={role}
              onChange={(event) => setRole(event.target.value as OrgRole)}
              className={fieldClass}
            >
              {invitableRoles.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <p className="mt-1 text-[12px] leading-relaxed text-ink-secondary">
              {invitableRoles.find((option) => option.value === role)?.hint}
            </p>
          </Field>

          <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12px] leading-relaxed text-ink-secondary">
            Ownership cannot be given by invitation. The invitation expires in
            seven days, and you will get a link to pass on — it is shown once.
          </p>
        </FormDialog>
      ) : null}

      {/* Removal is not reversible from this screen — getting somebody back
          means a fresh invitation they have to accept again — so it
          interrupts rather than confirming in place, and names the person so
          the wrong row cannot be confirmed by muscle memory. */}
      {removing ? (
        <ConfirmDialog
          title={`Remove ${removing.displayName ?? removing.email}?`}
          confirmLabel="Remove"
          isWorking={isPending}
          onConfirm={remove}
          onCancel={() => setRemoving(null)}
          body={
            <>
              <p>
                They lose access to every lease, tenant and document in{" "}
                this workspace immediately.
              </p>
              <p className="mt-2">
                Their account still exists, but it will not belong to any
                workspace. Getting them back means sending a new invitation.
              </p>
            </>
          }
        />
      ) : null}
    </Panel>
  );
}

/**
 * Changing one member's role, in place.
 *
 * <h3>Why a bare select and not a dialog</h3>
 *
 * A role change is one value with five options and no consequence that needs
 * explaining twice. A dialog would add two clicks to confirm something the
 * dropdown already states, and the change is trivially reversible.
 *
 * It is only rendered where a change is permitted, so there is no disabled
 * state to explain: the owner and yourself show a plain chip instead.
 */
/**
 * Changing one member's role, with a confirmation.
 *
 * <h3>Why the dropdown does not apply the change</h3>
 *
 * A select that acts on change is a one-keystroke mistake: an arrow key on a
 * focused dropdown silently demotes somebody, and the only sign is a chip
 * that now reads differently. A role decides what a person can see and do in
 * a workspace holding lease terms and tenant contact details, so the choice
 * and the commitment are two separate acts.
 *
 * Confirmed in place rather than in a modal, for the same reason the archive
 * does it: on a list of similar rows, a dialog asking "are you sure?" has
 * moved the question away from the row it is about.
 *
 * It is only rendered where a change is permitted, so there is no disabled
 * state to explain — the owner and yourself show a plain chip instead, and
 * app.change_member_role refuses both regardless.
 */
function RolePicker({
  organizationId,
  userId,
  role,
}: {
  organizationId: string;
  userId: string;
  role: OrgRole;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [chosen, setChosen] = useState<OrgRole | null>(null);
  const [error, setError] = useState<string | null>(null);

  function confirm() {
    if (!chosen) {
      return;
    }

    setError(null);

    startTransition(async () => {
      const result = await changeMemberRoleAction(organizationId, userId, chosen);

      if (!result.ok) {
        setError(result.error ?? "That role could not be changed.");
        setChosen(null);

        return;
      }

      setChosen(null);
      router.refresh();
    });
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <div className="flex items-center gap-1.5">
        <select
          // Shows what has been picked but not yet applied, so the pending
          // state is visible in the control itself rather than only in the
          // sentence beside it.
          value={chosen ?? role}
          disabled={isPending}
          aria-label="Role"
          onChange={(event) => {
            const next = event.target.value as OrgRole;

            setError(null);
            setChosen(next === role ? null : next);
          }}
          className="h-7 rounded-control border border-line-strong bg-surface px-1.5
                     text-[12.5px] transition-colors focus:border-accent
                     focus:outline-none focus:ring-2 focus:ring-accent/20
                     disabled:opacity-60"
        >
          {invitableRoles.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>

        {chosen ? (
          <>
            <Button
              size="sm"
              variant="ghost"
              disabled={isPending}
              onClick={() => {
                setChosen(null);
                setError(null);
              }}
            >
              Cancel
            </Button>
            <Button size="sm" variant="primary" disabled={isPending} onClick={confirm}>
              {isPending ? "Changing…" : "Change"}
            </Button>
          </>
        ) : null}
      </div>

      {chosen ? (
        <p className="text-right text-[11.5px] text-ink-secondary">
          Change from {roleLabel(role)} to {roleLabel(chosen)}?
        </p>
      ) : null}

      {error ? (
        <p role="alert" className="max-w-[18rem] text-right text-[11.5px] text-danger-text">
          {error}
        </p>
      ) : null}
    </div>
  );
}
