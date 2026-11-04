import { NextResponse } from "next/server";

import { ApiError, documentApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Mints a short-lived viewing URL for the document viewer.
 *
 * <h3>Why a route handler rather than a server action</h3>
 *
 * The viewer renews its URL on a timer while somebody reads, without any user
 * gesture. A server action is designed around a mutation triggered by an
 * interaction; a plain GET that the component can call on a schedule is the
 * honest shape for "fetch me a fresh link".
 *
 * <h3>Why the URL is not embedded in the page</h3>
 *
 * A signed URL is a bearer credential for that object: anyone holding it has
 * access for its lifetime. Putting one in the server-rendered HTML would place
 * it in the document source, in any cache that saw the response, and in the
 * browser history. Fetching it separately keeps it in memory for as long as
 * the tab is open and no longer.
 */
export const dynamic = "force-dynamic";

export async function GET(request: Request) {
  const user = await getCurrentUser();

  if (!user) {
    return NextResponse.json({ error: "Not signed in." }, { status: 401 });
  }

  const documentId = new URL(request.url).searchParams.get("documentId");

  if (!documentId) {
    return NextResponse.json({ error: "A document is required." }, { status: 400 });
  }

  try {
    // Authorization is the API's and the database's. Row Level Security
    // refuses a document outside the caller's organization, so this handler
    // does not re-implement the check -- it would be a second copy of a rule
    // that is already enforced where it cannot be bypassed.
    const signed = await documentApi.viewUrl(documentId);

    return NextResponse.json(signed, {
      headers: {
        // Never cached, anywhere. The URL expires in minutes, and a cached
        // copy would both stop working and sit in a store it has no business
        // being in.
        "Cache-Control": "no-store, no-cache, must-revalidate",
      },
    });
  } catch (error) {
    if (error instanceof ApiError) {
      return NextResponse.json(
        { error: error.message },
        { status: error.isUnauthenticated ? 401 : 502 },
      );
    }

    return NextResponse.json(
      { error: "The document could not be opened." },
      { status: 502 },
    );
  }
}
