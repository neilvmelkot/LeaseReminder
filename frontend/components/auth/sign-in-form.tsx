"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";

import { Button } from "@/components/ui/primitives";
import { Logo } from "@/components/ui/logo";
import { createBrowserSupabaseClient } from "@/lib/supabase/client";

/**
 * Sign in, and create an account.
 *
 * <h3>What this page deliberately does not tell you</h3>
 *
 * Section 4.A requires that a password reset must not reveal whether an email
 * address has an account. The same reasoning applies to sign-in and is easier
 * to get wrong there: "no account with that email" and "wrong password" are
 * two different answers, and together they let anybody test whether a given
 * person is a customer.
 *
 * So every sign-in failure renders one message. The identity provider knows
 * which it was, and its own logs record it; the browser does not.
 *
 * <h3>Why there is no open sign-up</h3>
 *
 * A workspace holds lease terms, tenant contact details, and a mailbox that
 * sends in its owner's name. Who may see that is the owner's decision, not
 * whoever finds the URL, so joining is by invitation.
 *
 * One exception, and it is narrow: a brand-new deployment has nobody to issue
 * the first invitation. The API reports whether any user exists at all, and
 * only while none does is self-registration offered. After the first account
 * that answer is false permanently.
 */
export function SignInForm({
  needsFirstUser,
}: {
  /**
   * True only while this deployment has no users. Resolved on the server so
   * the page never flashes a registration form it is about to withdraw.
   */
  needsFirstUser: boolean;
}) {
  const router = useRouter();

  const [mode, setMode] = useState<"signIn" | "signUp">(
    needsFirstUser ? "signUp" : "signIn",
  );
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [status, setStatus] = useState<"idle" | "working">("idle");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const isSignUp = mode === "signUp";

  function switchTo(next: "signIn" | "signUp") {
    setMode(next);
    setError(null);
    setNotice(null);
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();

    setStatus("working");
    setError(null);
    setNotice(null);

    try {
      const supabase = createBrowserSupabaseClient();

      if (isSignUp) {
        if (!needsFirstUser) {
          // Belt and braces. The form is not offered in this state, but a
          // stale tab could still submit one, and the answer should be the
          // same as it would be from any other direction.
          setError("Accounts are created by invitation. Ask for an invitation link.");
          setStatus("idle");

          return;
        }

        // Checked here only to give a useful message before the round trip.
        // Supabase enforces its own minimum, and the real strength
        // requirement is a project setting rather than something this form
        // can promise.
        if (password.length < 8) {
          setError("Choose a password of at least 8 characters.");
          setStatus("idle");

          return;
        }

        const { data, error: signUpError } = await supabase.auth.signUp({
          email,
          password,
          options: {
            emailRedirectTo: `${window.location.origin}/auth/callback`,
          },
        });

        if (signUpError) {
          setError("That account could not be created. Check the address and try again.");
          setStatus("idle");

          return;
        }

        // A session comes back when email confirmation is switched off, which
        // is the usual setup for a personal deployment. Then the account
        // exists and is already signed in.
        if (data.session) {
          router.refresh();
          router.push("/");

          return;
        }

        // No session means either a confirmation email is on its way, or the
        // address already had an account. The same sentence covers both, so
        // this form cannot be used to discover which.
        setNotice(
          "Check your email for a confirmation link. If an account already "
          + "exists for that address, sign in instead.",
        );
        setStatus("idle");

        return;
      }

      const { error: signInError } = await supabase.auth.signInWithPassword({
        email,
        password,
      });

      if (signInError) {
        // One message for every failure. The provider's own error text
        // distinguishes a missing account from a wrong password, which is
        // exactly what must not reach the browser.
        setError("That email and password combination was not accepted.");
        setStatus("idle");

        return;
      }

      // refresh() rather than push(), so the server components re-render with
      // the new session cookie rather than rendering from a stale one.
      router.refresh();
      router.push("/");
    } catch {
      setError(
        isSignUp
          ? "The account could not be created. Please try again."
          : "Sign in could not be completed. Please try again.",
      );
      setStatus("idle");
    }
  }

  async function handleReset() {
    if (!email) {
      setError("Enter your email address first.");

      return;
    }

    setStatus("working");
    setError(null);

    try {
      const supabase = createBrowserSupabaseClient();

      await supabase.auth.resetPasswordForEmail(email, {
        redirectTo: `${window.location.origin}/auth/callback`,
      });
    } catch {
      // Swallowed on purpose. Reporting a failure here would distinguish a
      // known address from an unknown one, which is the thing this flow
      // exists not to do.
    } finally {
      // The same message either way, including when the address has no
      // account (section 4.A).
      setNotice(
        "If an account exists for that address, a reset link has been sent to it.",
      );
      setStatus("idle");
    }
  }

  return (
    <main className="grid min-h-dvh place-items-center px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex items-center gap-2.5">
          <span
            className="grid size-8 place-items-center rounded-control bg-accent text-white"
            aria-hidden="true"
          >
            <Logo className="size-[19px]" />
          </span>
          <span className="text-base font-semibold">LeaseReminder</span>
        </div>

        <form onSubmit={handleSubmit} className="surface p-6">
          <h1 className="text-lg leading-tight">
            {isSignUp ? "Create the first account" : "Sign in"}
          </h1>
          <p className="mt-1 text-[13px] text-ink-secondary">
            {isSignUp
              ? "Nobody has set this up yet, so this account becomes the owner."
              : "Manage your properties, leases, and rent reminders."}
          </p>

          <div className="mt-5 space-y-4">
            <div>
              {/* A real label with htmlFor, not a placeholder. A placeholder
                  disappears as soon as the field has content, which leaves a
                  screen reader and a distracted user with an unlabelled box. */}
              <label htmlFor="email" className="block text-[13px] font-medium">
                Email
              </label>
              <input
                id="email"
                name="email"
                type="email"
                autoComplete="email"
                required
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                className="mt-1.5 h-9 w-full rounded-control border border-line-strong
                           bg-surface px-3 text-sm placeholder:text-ink-muted"
              />
            </div>

            <div>
              <label htmlFor="password" className="block text-[13px] font-medium">
                Password
              </label>
              <input
                id="password"
                name="password"
                type="password"
                // new-password when registering, so a password manager offers
                // to generate one instead of suggesting a saved credential
                // that does not exist yet.
                autoComplete={isSignUp ? "new-password" : "current-password"}
                required
                minLength={isSignUp ? 8 : undefined}
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="mt-1.5 h-9 w-full rounded-control border border-line-strong
                           bg-surface px-3 text-sm"
              />
              {isSignUp ? (
                <p className="mt-1 text-[12px] text-ink-secondary">
                  At least 8 characters.
                </p>
              ) : null}
            </div>
          </div>

          {error ? (
            // role="alert" so the message is announced, not just displayed
            // (section 5 asks for accessible validation messages).
            <p
              role="alert"
              className="mt-4 rounded-control bg-danger-tint px-3 py-2 text-[13px]
                         text-danger-text"
            >
              {error}
            </p>
          ) : null}

          {notice ? (
            <p
              role="status"
              className="mt-4 rounded-control bg-accent-tint px-3 py-2 text-[13px]
                         text-accent"
            >
              {notice}
            </p>
          ) : null}

          <Button
            type="submit"
            variant="primary"
            disabled={status === "working"}
            className="mt-5 w-full"
          >
            {status === "working"
              ? isSignUp
                ? "Creating your account..."
                : "Signing in..."
              : isSignUp
                ? "Create account"
                : "Sign in"}
          </Button>

          {!isSignUp ? (
            <button
              type="button"
              onClick={handleReset}
              disabled={status === "working"}
              className="mt-3 w-full rounded-control py-1.5 text-[13px] text-ink-secondary
                         hover:text-ink disabled:opacity-55"
            >
              Forgot your password?
            </button>
          ) : null}

          {needsFirstUser ? (
            <p className="mt-4 border-t border-line pt-4 text-center text-[13px] text-ink-secondary">
              {isSignUp
                ? "Setting up for the first time. "
                : "This deployment has no accounts yet. "}
              <button
                type="button"
                onClick={() => switchTo(isSignUp ? "signIn" : "signUp")}
                disabled={status === "working"}
                className="font-medium text-accent hover:underline disabled:opacity-55"
              >
                {isSignUp ? "Sign in instead" : "Create the first account"}
              </button>
            </p>
          ) : null}
        </form>
      </div>
    </main>
  );
}
