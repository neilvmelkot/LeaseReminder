import { NextResponse } from "next/server";

import { notificationApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * The caller's notifications, for the bell panel.
 *
 * Takes no user parameter: the upstream database function reads the caller's
 * identity, and the RLS policy on notification_deliveries enforces the same
 * restriction a second time. There is no argument through which one person
 * could request another's inbox.
 */
export const dynamic = "force-dynamic";

export async function GET(request: Request) {
  const user = await getCurrentUser();

  if (!user) {
    return NextResponse.json({ error: "unauthenticated" }, { status: 401 });
  }

  const url = new URL(request.url);
  const includeRead = url.searchParams.get("includeRead") === "true";

  // Clamped, so a crafted query cannot ask for an unbounded page. The database
  // function clamps as well; this keeps the error local.
  const requested = Number(url.searchParams.get("limit") ?? "30");
  const limit = Number.isFinite(requested)
    ? Math.min(Math.max(Math.trunc(requested), 1), 100)
    : 30;

  try {
    const result = await notificationApi.list(includeRead, limit);

    return NextResponse.json(result, {
      // Somebody's personal notifications must never be cached by an
      // intermediary or left in a shared browser's disk cache.
      headers: { "Cache-Control": "no-store" },
    });
  } catch {
    return NextResponse.json({ error: "upstream unavailable" }, { status: 502 });
  }
}
