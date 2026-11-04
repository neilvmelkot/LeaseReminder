import { SignInForm } from "@/components/auth/sign-in-form";
import { membershipApi } from "@/lib/api";

/**
 * Sign in.
 *
 * <p>
 * A server component, so whether this deployment still needs its first account
 * is resolved before anything renders. Asking from the browser would mean the
 * page briefly offering a registration form and then withdrawing it, which
 * looks like a bug and, for the half-second it is on screen, invites somebody
 * to use it.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Sign in" };

export default async function SignInPage() {
  let needsFirstUser = false;

  try {
    const status = await membershipApi.bootstrapStatus();
    needsFirstUser = status.needsFirstUser;
  } catch {
    // The API being unreachable is not a reason to offer registration. The
    // safe default is the restrictive one: a sign-in form, which fails with a
    // clear message if the API really is down.
    needsFirstUser = false;
  }

  return <SignInForm needsFirstUser={needsFirstUser} />;
}
