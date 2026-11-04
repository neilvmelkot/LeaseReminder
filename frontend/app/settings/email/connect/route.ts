import { NextResponse } from "next/server";

import { getAccessToken, getCurrentUser } from "@/lib/supabase/server";

/**
 * Starts the Gmail connection flow.
 *
 * <h3>Why this handler exists at all</h3>
 *
 * The Connect button used to be a plain link straight to the API's authorize
 * endpoint. That endpoint requires authentication, and a top-level browser
 * navigation carries no Authorization header — the access token lives in an
 * http-only cookie on this origin, which only server-side code can read. So
 * the link returned 401 every time, and no amount of configuration would have
 * fixed it.
 *
 * It also cannot be a fetch from the browser: the API's reply sets the PKCE
 * attempt cookie, and the callback from Google needs that cookie to exist.
 *
 * So this handler is the authenticated front door. It is on this origin, so
 * the session cookie reaches it; it attaches the bearer token the API wants;
 * and it passes the API's two outputs — the Set-Cookie and the redirect to
 * Google — back to the browser unchanged.
 *
 * <h3>What is deliberately not touched</h3>
 *
 * The cookie is forwarded verbatim. It is a <code>__Host-</code> cookie, which
 * means the browser will only accept it with Secure, Path=/ and no Domain, and
 * will only send it back to this exact host. Rewriting any of those attributes
 * would either make the browser reject it or weaken the thing that makes the
 * prefix worth using.
 *
 * The PKCE verifier inside it is never read here, and never reaches the
 * browser's JavaScript — it is http-only in both directions.
 *
 * <h3>The deployment constraint this creates</h3>
 *
 * A <code>__Host-</code> cookie set by this origin is sent to the API only if
 * the API is on the same host. Cookies ignore port numbers, so
 * localhost:3000 setting it and localhost:8081 receiving it is fine. In
 * production the API has to sit behind the same hostname as the frontend — a
 * path prefix on one domain, not api.example.com beside app.example.com. If
 * that ever changes, this flow needs the attempt state moved out of a cookie
 * and into the database, keyed by the OAuth state value.
 */
export const dynamic = "force-dynamic";

export async function GET(request: Request) {
  const user = await getCurrentUser();

  if (!user) {
    return NextResponse.redirect(new URL("/sign-in", request.url));
  }

  const organizationId = new URL(request.url).searchParams.get("organizationId");

  if (!organizationId) {
    return NextResponse.redirect(
      new URL("/settings/email?connection=failed&reason=No+workspace+was+specified.", request.url),
    );
  }

  const token = await getAccessToken();
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

  const headers = new Headers();

  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  // Manual, because the whole point is to intercept the API's redirect rather
  // than follow it on the server — following it would send the server to
  // Google's consent screen, which is not a thing a server can complete.
  const response = await fetch(
    `${apiBaseUrl}/api/v1/organizations/${organizationId}/email-account/authorize`,
    { method: "GET", headers, redirect: "manual", cache: "no-store" },
  );

  const location = response.headers.get("location");

  if (!location) {
    // Not a redirect, so something refused. The API's ProblemDetails carry no
    // internals and its message names the actual reason — an unconfigured
    // OAuth client, a missing encryption key, the wrong role — all of which
    // are more useful than "could not connect".
    const problem = (await response.json().catch(() => null)) as
      | { detail?: string; title?: string }
      | null;

    const reason =
      problem?.detail
      ?? problem?.title
      ?? (response.status === 401
        ? "Your session has expired. Sign in again."
        : "The mailbox could not be connected.");

    return NextResponse.redirect(
      new URL(
        `/settings/email?connection=failed&reason=${encodeURIComponent(reason)}`,
        request.url,
      ),
    );
  }

  const redirect = NextResponse.redirect(location, { status: 302 });

  // Every cookie the API set, forwarded as-is. getSetCookie returns them
  // individually, which matters because a single joined header cannot be
  // split reliably — Expires dates contain commas.
  for (const cookie of response.headers.getSetCookie()) {
    redirect.headers.append("set-cookie", cookie);
  }

  return redirect;
}
