"use client";

import { createBrowserClient } from "@supabase/ssr";

import { readPublicSupabaseConfig } from "@/lib/supabase/config";

/**
 * Browser Supabase client, for the sign-in form.
 *
 * Kept in its own module so that nothing importing it drags in `next/headers`.
 * A single file exporting both a browser and a server client cannot be
 * imported from a client component at all: the bundler follows the import and
 * fails on the server-only API, which is a confusing error to meet for the
 * first time.
 */
export function createBrowserSupabaseClient() {
  const { url, anonKey } = readPublicSupabaseConfig();

  return createBrowserClient(url, anonKey);
}
