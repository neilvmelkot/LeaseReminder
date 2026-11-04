import { NextResponse } from "next/server";

import { notificationApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * The unread badge count.
 *
 * Its own endpoint because the badge is polled far more often than the list is
 * opened, and a count is one integer rather than fifty rows. The upstream
 * function reads the caller's identity itself, so there is no user parameter
 * anywhere in this path.
 */
export const dynamic = "force-dynamic";

export async function GET() {
  const user = await getCurrentUser();

  if (!user) {
    // Zero rather than 401: the bell is rendered in the shell on every page,
    // and a 401 from a background poll would look like an error to the client
    // when the real situation is simply a session that has ended.
    return NextResponse.json(
      { unreadCount: 0 },
      { headers: { "Cache-Control": "no-store" } },
    );
  }

  try {
    const result = await notificationApi.unreadCount();

    return NextResponse.json(result, {
      headers: { "Cache-Control": "no-store" },
    });
  } catch {
    return NextResponse.json(
      { unreadCount: 0 },
      { headers: { "Cache-Control": "no-store" } },
    );
  }
}
