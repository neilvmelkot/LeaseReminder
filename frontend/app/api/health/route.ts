import { NextResponse } from "next/server";

/**
 * Liveness for Render's health check.
 *
 * Returns a fixed shape and nothing else: no version, no build id, no
 * dependency status. A health endpoint is unauthenticated by necessity, and
 * one that reports which versions are running is free reconnaissance.
 *
 * It deliberately does not check the API or Supabase. Render restarts an
 * instance that fails this check, so making it depend on a downstream service
 * would turn a brief API blip into a restart loop of a perfectly healthy
 * frontend.
 */
export const dynamic = "force-dynamic";

export function GET() {
  return NextResponse.json(
    { status: "live" },
    { headers: { "Cache-Control": "no-store" } },
  );
}
