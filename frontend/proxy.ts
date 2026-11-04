import { createServerClient } from "@supabase/ssr";
import { NextResponse, type NextRequest } from "next/server";

/**
 * Session refresh and route protection.
 *
 * Named proxy.ts because Next 16 renamed this convention from middleware.ts;
 * the behaviour is unchanged.
 *
 * Two jobs, and they are related:
 *
 *   1. <b>Refresh the session cookie.</b> Server components cannot write
 *      cookies, so without middleware an access token would expire mid-visit
 *      and every subsequent server render would see an unauthenticated user.
 *
 *   2. <b>Redirect an unauthenticated request away from an application page.</b>
 *      This is a convenience, NOT the authorization boundary. The real
 *      boundaries are the API's authentication (every endpoint requires a
 *      validated token, by a fallback policy rather than per-endpoint opt-in)
 *      and PostgreSQL Row Level Security underneath it. Middleware runs before
 *      the page and can be bypassed by calling the API directly, so treating
 *      it as the gate would be a mistake. It exists so that a signed-out user
 *      sees a sign-in form instead of an empty dashboard.
 */

/** Paths that never require a session. */
// /invite is public by necessity: an invitee has no session until they have
// redeemed the invitation, and redirecting them to sign in would send them
// somewhere that cannot help. The page itself discloses nothing without a
// valid token, and an unknown token and a spent one answer identically.
const publicPaths = ["/sign-in", "/auth", "/api/health", "/invite"];

/**
 * The design harness, which has no session to show and no data to protect.
 *
 * Exempted only in development. The page itself also 404s outside
 * development, so this is the second of two independent reasons it cannot be
 * reached in production, not the only one. NODE_ENV is substituted at build
 * time, so the comparison is resolved before the bundle ships rather than
 * evaluated per request.
 */
const developmentOnlyPublicPaths =
  process.env.NODE_ENV === "production" ? [] : ["/design-preview"];

export async function proxy(request: NextRequest) {
  let response = NextResponse.next({ request });

  const supabaseUrl = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const supabaseAnonKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

  if (!supabaseUrl || !supabaseAnonKey) {
    // Without configuration there is no way to establish a session, so the
    // only safe behaviour is to let the request through and let the page
    // report the misconfiguration. Redirecting would loop.
    return response;
  }

  const supabase = createServerClient(supabaseUrl, supabaseAnonKey, {
    cookies: {
      getAll() {
        return request.cookies.getAll();
      },
      setAll(cookiesToSet) {
        // Set on BOTH the request and the response. The request copy is what
        // the page sees in this same pass; the response copy is what the
        // browser stores for the next one. Setting only one of them produces
        // a session that works intermittently, which is miserable to debug.
        for (const { name, value } of cookiesToSet) {
          request.cookies.set(name, value);
        }

        response = NextResponse.next({ request });

        for (const { name, value, options } of cookiesToSet) {
          response.cookies.set(name, value, options);
        }
      },
    },
  });

  // getUser, not getSession: it validates the token with the auth server and
  // refreshes it when needed. getSession would trust the cookie's contents,
  // which is fine for reading a token to forward but not for deciding whether
  // somebody is signed in.
  const {
    data: { user },
  } = await supabase.auth.getUser();

  const { pathname } = request.nextUrl;
  const isPublic = [...publicPaths, ...developmentOnlyPublicPaths].some(
    (path) => pathname === path || pathname.startsWith(path + "/"),
  );

  if (!user && !isPublic) {
    const signIn = new URL("/sign-in", request.url);

    return NextResponse.redirect(signIn);
  }

  if (user && pathname === "/sign-in") {
    return NextResponse.redirect(new URL("/", request.url));
  }

  return response;
}

export const config = {
  matcher: [
    // Everything except Next's static output and the favicon. Running
    // this on static assets would add an auth round trip to every image
    // request for no benefit.
    "/((?!_next/static|_next/image|favicon.ico|.*\\.(?:svg|png|jpg|jpeg|gif|webp|ico)$).*)",
  ],
};
