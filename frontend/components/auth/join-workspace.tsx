"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";

import { acceptInvitationAction } from "@/app/invite/actions";

/**
 * Redeeming an invitation when you are already signed in as the invited
 * address.
 *
 * <h3>Why this exists</h3>
 *
 * The password form is for somebody who has no account yet. Once they have
 * one, asking for the password again is both pointless and a dead end — and
 * it is exactly the state an invitee lands in when email confirmation is
 * switched on: they set a password, confirm from their inbox, sign in, and
 * arrive with no workspace, because redeeming the invitation never happened.
 *
 * The invitation is still live at that point. All that is missing is the one
 * call, so this offers it as one button.
 */
export function JoinWorkspace({
  token,
  organizationName,
  roleName,
}: {
  token: string;
  organizationName: string;
  roleName: string;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  function join() {
    setError(null);

    startTransition(async () => {
      const result = await acceptInvitationAction(token);

      if (!result.ok) {
        setError(result.error ?? "The invitation could not be accepted.");

        return;
      }

      router.refresh();
      router.push("/");
    });
  }

  return (
    <div className="surface p-6">
      <h1 className="text-lg leading-tight">Join {organizationName}</h1>
      <p className="mt-1 text-[13px] leading-relaxed text-ink-secondary">
        You are signed in as the invited address, so there is nothing to set
        up. You will join as <span className="font-medium text-ink">{roleName}</span>.
      </p>

      {error ? (
        <p
          role="alert"
          className="mt-4 flex items-start gap-1.5 rounded-control border border-danger/30
                     bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
        >
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      <Button
        variant="primary"
        onClick={join}
        disabled={isPending}
        className="mt-5 w-full"
      >
        {isPending ? "Joining…" : `Join ${organizationName}`}
      </Button>
    </div>
  );
}
