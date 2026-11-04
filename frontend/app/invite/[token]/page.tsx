import { AcceptInvitation } from "@/components/auth/accept-invitation";
import { Logo } from "@/components/ui/logo";
import { JoinWorkspace } from "@/components/auth/join-workspace";
import { membershipApi, roleLabel, type DescribedInvitation } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Redeeming an invitation.
 *
 * <p>
 * Resolved on the server, so an invalid token is answered before anything is
 * typed rather than after. An unknown token and a spent one give the same
 * answer, which is what stops this page being used to discover which tokens
 * exist.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Join a workspace" };

export default async function InvitePage({
  params,
}: {
  params: Promise<{ token: string }>;
}) {
  const { token } = await params;

  let described: DescribedInvitation | null = null;

  try {
    described = await membershipApi.describeInvitation(token);
  } catch {
    described = null;
  }

  if (!described?.isValid) {
    return (
      <main className="grid min-h-dvh place-items-center px-4 py-10">
        <div className="w-full max-w-sm">
          <Brand />
          <div className="surface p-6 text-center">
            <h1 className="text-lg leading-tight">This invitation is not valid</h1>
            <p className="mt-2 text-[13px] leading-relaxed text-ink-secondary">
              It may have expired, been withdrawn, or already been used. Ask
              whoever invited you to send a new one.
            </p>
            <a
              href="/sign-in"
              className="mt-5 inline-flex h-9 items-center rounded-control border
                         border-line-strong bg-surface px-4 text-sm font-medium
                         transition-colors hover:bg-surface-subtle"
            >
              Go to sign in
            </a>
          </div>
        </div>
      </main>
    );
  }

  // Three states, not one.
  //
  // Somebody already signed in as the invited address needs no password: the
  // invitation is live and one call away. That is exactly where an invitee
  // ends up when email confirmation is on — set a password, confirm from the
  // inbox, sign in, and arrive with no workspace because redeeming never
  // happened. Showing them the password form again is a dead end.
  //
  // Signed in as somebody else is its own case, and saying so beats a form
  // that will be refused: the database requires the signed-in address to
  // match the invited one.
  const user = await getCurrentUser();
  const invitedEmail = described.email ?? "";

  const signedInAs = user?.email?.toLowerCase() ?? null;
  const isInvitedUser =
    signedInAs !== null && signedInAs === invitedEmail.toLowerCase();

  return (
    <main className="grid min-h-dvh place-items-center px-4 py-10">
      <div className="w-full max-w-sm">
        <Brand />

        {isInvitedUser ? (
          <JoinWorkspace
            token={token}
            organizationName={described.organizationName ?? "a workspace"}
            roleName={roleLabel(described.role ?? null)}
          />
        ) : signedInAs !== null ? (
          <div className="surface p-6">
            <h1 className="text-lg leading-tight">This invitation is for someone else</h1>
            <p className="mt-2 text-[13px] leading-relaxed text-ink-secondary">
              It was sent to{" "}
              <span className="font-medium text-ink">{invitedEmail}</span>, and you
              are signed in as{" "}
              <span className="font-medium text-ink">{signedInAs}</span>. Sign out
              and open this link again.
            </p>

            {/* POST, not a link: a GET sign-out can be triggered by any page
                that embeds it. */}
            <form action="/auth/sign-out" method="post" className="mt-5">
              <button
                type="submit"
                className="inline-flex h-9 w-full items-center justify-center rounded-control
                           border border-line-strong bg-surface px-4 text-sm font-medium
                           transition-colors hover:bg-surface-subtle"
              >
                Sign out
              </button>
            </form>
          </div>
        ) : (
          <AcceptInvitation
            token={token}
            organizationName={described.organizationName ?? "a workspace"}
            email={invitedEmail}
            roleName={roleLabel(described.role ?? null)}
          />
        )}
      </div>
    </main>
  );
}

function Brand() {
  return (
    <div className="mb-6 flex items-center gap-2.5">
      <span
        className="grid size-8 place-items-center rounded-control bg-accent text-white"
        aria-hidden="true"
      >
        <Logo className="size-[19px]" />
      </span>
      <span className="text-base font-semibold">LeaseReminder</span>
    </div>
  );
}
