import { NextResponse } from "next/server";

import { createServerSupabaseClient } from "@/lib/supabase/server";

/**
 * Sign out.
 *
 * <h3>Why this is POST only</h3>
 *
 * A GET sign-out link can be triggered by any page that embeds it: an image
 * tag, a prefetch, a link in an email. That is cross-site request forgery
 * (section 4.F). The damage is small, somebody is logged out unexpectedly, but
 * the fix is free: make it a POST from a form, which a cross-origin page
 * cannot issue against this origin without a CORS grant that is not given.
 *
 * It also revokes the session on the server rather than just clearing the
 * cookie, so the refresh token cannot be replayed (section 4.A).
 */
export async function POST(request: Request) {
  const supabase = await createServerSupabaseClient();

  const {
    data: { user },
  } = await supabase.auth.getUser();

  if (user) {
    // Revokes the refresh token server-side. Clearing the cookie alone would
    // leave a working token in anybody's hands who had already copied it.
    await supabase.auth.signOut();
  }

  // Redirect to a path on this origin, built from the request's own URL.
  // Never from a user-supplied parameter: a sign-out that honours a
  // ?returnTo= is an open redirect (section 4.F), and a sign-out page is a
  // convincing place to bounce somebody to a fake sign-in form.
  const redirectUrl = new URL("/sign-in", request.url);

  return NextResponse.redirect(redirectUrl, {
    // 303, so the browser follows with GET rather than repeating the POST.
    status: 303,
  });
}
