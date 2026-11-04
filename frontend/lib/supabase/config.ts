/**
 * The public Supabase configuration, shared by the browser and server clients.
 *
 * <h3>Which key belongs here, and which never does</h3>
 *
 * Only the publishable (anon) key, and only under a `NEXT_PUBLIC_` name.
 * Everything with that prefix is compiled into the browser bundle, so the
 * prefix is effectively a declaration that a value is public. The anon key is
 * built for exactly that: on its own it grants nothing, because every table is
 * behind Row Level Security and every policy requires a verified session.
 *
 * The service-role key is absent on purpose. It bypasses Row Level Security
 * entirely, so it lives only in the API and worker processes
 * (section 4.B: "Never expose service-role keys in frontend code or public
 * environment variables"). If a change here appears to need it, the right fix
 * is an API endpoint, not an environment variable.
 */
export function readPublicSupabaseConfig(): { url: string; anonKey: string } {
  const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const anonKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

  if (!url || !anonKey) {
    // Failing loudly at first use beats rendering a sign-in form that cannot
    // authenticate anybody and gives no clue why.
    throw new Error(
      "NEXT_PUBLIC_SUPABASE_URL and NEXT_PUBLIC_SUPABASE_ANON_KEY must both be set. " +
        "See .env.example.",
    );
  }

  // A crude guard against the single most damaging configuration mistake
  // available here. A Supabase JWT-format key carries its role in the payload,
  // so a service-role key put behind a NEXT_PUBLIC_ name is detectable, and it
  // would otherwise be shipped to every browser that loads the application.
  if (anonKey.includes("service_role")) {
    throw new Error(
      "NEXT_PUBLIC_SUPABASE_ANON_KEY appears to contain a service-role key. " +
        "That key bypasses Row Level Security and must never reach the browser.",
    );
  }

  return { url, anonKey };
}
