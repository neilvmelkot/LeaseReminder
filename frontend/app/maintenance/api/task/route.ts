import { NextResponse } from "next/server";

import { ApiError, maintenanceApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * One task's detail, for the board's side panel.
 *
 * <h3>Why this exists rather than a server action</h3>
 *
 * This is a read, triggered by clicking a card. A server action would work,
 * but it forces a router round trip and re-renders the route for data that is
 * not part of the page's own output. A route handler returns just the JSON.
 *
 * <h3>Why it is still safe to take an organizationId from the client</h3>
 *
 * It isn't trusted. The handler attaches the caller's own session token to the
 * upstream API call, the API checks membership, and PostgreSQL Row Level
 * Security filters the query underneath that. A forged organizationId selects
 * zero rows, which surfaces here as the 404 below.
 *
 * The session check at the top is a fast path, not the boundary: it turns an
 * expired session into a clean 401 instead of an upstream error.
 */
export const dynamic = "force-dynamic";

export async function GET(request: Request) {
  const user = await getCurrentUser();

  if (!user) {
    return NextResponse.json({ error: "unauthenticated" }, { status: 401 });
  }

  const url = new URL(request.url);
  const organizationId = url.searchParams.get("organizationId");
  const taskId = url.searchParams.get("taskId");

  if (!organizationId || !taskId) {
    return NextResponse.json(
      { error: "organizationId and taskId are required" },
      { status: 400 },
    );
  }

  // Both have to be UUIDs. Rejecting the shape here means a malformed value
  // never reaches the database as a failed cast.
  if (!isUuid(organizationId) || !isUuid(taskId)) {
    return NextResponse.json({ error: "malformed identifier" }, { status: 400 });
  }

  try {
    const task = await maintenanceApi.task(organizationId, taskId);

    return NextResponse.json(task, {
      // Task detail is per-user data behind a session. It must not be cached
      // by an intermediary or left in a shared browser's disk cache.
      headers: { "Cache-Control": "no-store" },
    });
  } catch (error) {
    if (error instanceof ApiError) {
      // The upstream status is forwarded as-is, so a 403 stays a 403 and the
      // panel can say "you do not have access" rather than "not found".
      return NextResponse.json(
        { error: error.message },
        { status: error.status },
      );
    }

    return NextResponse.json({ error: "upstream unavailable" }, { status: 502 });
  }
}

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isUuid(value: string): boolean {
  return uuidPattern.test(value);
}
