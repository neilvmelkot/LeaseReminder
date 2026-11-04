import "server-only";

import { createServerClient } from "@supabase/ssr";
import { cookies } from "next/headers";

import { readPublicSupabaseConfig } from "@/lib/supabase/config";

/**
 * Server-side Supabase client, bound to the request's cookies.
 *
 * The `server-only` import at the top is deliberate: it makes any attempt to
 * import this module from a client component a build error with a clear
 * message, rather than a confusing failure about `next/headers` deep in the
 * bundler output.
 */
export async function createServerSupabaseClient() {
  const { url, anonKey } = readPublicSupabaseConfig();
  const cookieStore = await cookies();

  return createServerClient(url, anonKey, {
    cookies: {
      getAll() {
        return cookieStore.getAll();
      },
      setAll(cookiesToSet) {
        try {
          for (const { name, value, options } of cookiesToSet) {
            cookieStore.set(name, value, options);
          }
        } catch {
          // Called from a server component, where cookies are read-only.
          // Session refresh is the proxy's job (proxy.ts), so this is both
          // expected and genuinely harmless; swallowing it is not hiding a
          // problem.
        }
      },
    },
  });
}

/**
 * The current session's access token, or null.
 *
 * This is the token the API validates. It is never persisted by this
 * application and never logged: read from the session cookie, attached to one
 * outgoing request, discarded.
 */
export async function getAccessToken(): Promise<string | null> {
  const supabase = await createServerSupabaseClient();

  // getSession reads the cookie without contacting the auth server, which is
  // what makes it cheap enough to call per request. Trusting its contents is
  // fine here because the only use is forwarding the token, and the API
  // re-validates the signature regardless, so a tampered cookie gains nothing.
  const {
    data: { session },
  } = await supabase.auth.getSession();

  return session?.access_token ?? null;
}

/**
 * The signed-in user, verified against the auth server.
 *
 * Deliberately getUser rather than getSession: getUser checks the token with
 * the identity provider instead of trusting the cookie, which is the right
 * standard for deciding whether to render a page at all.
 */
export async function getCurrentUser() {
  const supabase = await createServerSupabaseClient();

  const {
    data: { user },
  } = await supabase.auth.getUser();

  return user;
}
