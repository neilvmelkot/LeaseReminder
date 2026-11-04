"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { MailCheck, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { createBrowserSupabaseClient } from "@/lib/supabase/client";

import { acceptInvitationAction } from "@/app/invite/actions";

/**
 * Setting a password and joining.
 *
 * <h3>Why the email is shown but not editable</h3>
 *
 * An invitation names a person. The server requires the signed-in address to
 * match the invited one, so letting somebody type a different address here
 * would only produce a refusal later, after they had chosen a password. Fixing
 * it on screen states the rule instead of enforcing it by disappointment.
 *
 * <h3>Three paths, one form</h3>
 *
 * The invitee may or may not already have an account: somebody invited to a
 * second workspace already does. So registration is attempted first and a
 * refusal falls through to signing in, and one form covers both without
 * asking them which they are.
 *
 * The third path is the one that matters, because getting it wrong is what
 * broke this: with email confirmation switched on, registering a NEW address
 * also returns no session. Treating that as "the account must already exist"
 * and trying to sign in produces a failure, which then reads as a rejected
 * password — so the invitee is told their password is wrong and left waiting
 * for a verification email that did arrive. See submit() for how the three
 * are told apart.
 */
export function AcceptInvitation({
  token,
  organizationName,
  email,
  roleName,
}: {
  token: string;
  organizationName: string;
  email: string;
  roleName: string;
}) {
  const router = useRouter();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  // Not an error: the account was made and is waiting on a confirmation
  // email. Reporting it in the error style would say something went wrong.
  const [notice, setNotice] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setNotice(null);

    if (password.length < 8) {
      setError("Choose a password of at least 8 characters.");

      return;
    }

    startTransition(async () => {
      const supabase = createBrowserSupabaseClient();

      const { data, error: signUpError } = await supabase.auth.signUp({
        email,
        password,
      });

      // Three outcomes, and this used to collapse them into two.
      //
      // A session means the account was created and email confirmation is
      // switched off for the project. Straight in.
      //
      // No session, with an empty `identities` array, is how Supabase says
      // the address already has an account. It will not say so outright,
      // because that would let anybody test which addresses are registered —
      // so the empty array is the signal, and signing in with the same
      // password is the right next step.
      //
      // No session WITH identities means the account was just created and a
      // confirmation email is on its way. That is the case this got wrong: it
      // tried to sign in, which cannot succeed for an address that has not
      // been confirmed yet, and then reported the failure as a rejected
      // password. An invitee who had done nothing wrong was told their
      // password was wrong and left waiting for a verification that had
      // already been sent.
      const alreadyRegistered = (data.user?.identities?.length ?? 0) === 0;

      if (!signUpError && !data.session && !alreadyRegistered) {
        setNotice(
          "Account created. Check your email for a confirmation link, then "
          + "open this invitation link again to finish joining.",
        );

        return;
      }

      if (signUpError || !data.session) {
        const { error: signInError } = await supabase.auth.signInWithPassword({
          email,
          password,
        });

        if (signInError) {
          // Deliberately one message for both "wrong password" and "the
          // address is registered but not confirmed": distinguishing them
          // would reveal which addresses have accounts.
          setError(
            "That password was not accepted. If you already have an account "
            + "for this address, enter its existing password — and if you have "
            + "just created one, confirm it from your email first.",
          );

          return;
        }
      }

      // Redeemed server-side, where the session cookie is readable and the
      // database can check that the signed-in address matches the invitation.
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
    <form onSubmit={submit} className="surface p-6">
      <h1 className="text-lg leading-tight">Join {organizationName}</h1>
      <p className="mt-1 text-[13px] leading-relaxed text-ink-secondary">
        You have been invited as{" "}
        <span className="font-medium text-ink">{roleName}</span>.
      </p>

      <div className="mt-5 space-y-4">
        <div>
          <span className="block text-[13px] font-medium">Email</span>
          <p
            className="mt-1.5 flex h-9 items-center rounded-control border border-line
                       bg-surface-subtle px-3 text-sm text-ink-secondary"
          >
            {email}
          </p>
          <p className="mt-1 text-[12px] text-ink-secondary">
            The invitation was sent to this address and can only be used with it.
          </p>
        </div>

        <div>
          <label htmlFor="invite-password" className="block text-[13px] font-medium">
            Password
          </label>
          <input
            id="invite-password"
            type="password"
            autoComplete="new-password"
            required
            minLength={8}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            className="mt-1.5 h-9 w-full rounded-control border border-line-strong
                       bg-surface px-3 text-sm transition-colors focus:border-accent
                       focus:outline-none focus:ring-2 focus:ring-accent/20"
          />
          <p className="mt-1 text-[12px] text-ink-secondary">
            At least 8 characters. If you already have an account for this
            address, enter its existing password.
          </p>
        </div>
      </div>

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

      {notice ? (
        <p
          role="status"
          className="mt-4 flex items-start gap-1.5 rounded-control border border-accent/30
                     bg-accent-tint px-3 py-2 text-[12.5px] leading-relaxed text-accent"
        >
          <MailCheck className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {notice}
        </p>
      ) : null}

      <Button
        type="submit"
        variant="primary"
        disabled={isPending}
        className="mt-5 w-full"
      >
        {isPending ? "Joining…" : `Join ${organizationName}`}
      </Button>
    </form>
  );
}
