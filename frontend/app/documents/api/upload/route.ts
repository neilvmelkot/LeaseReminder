import { NextResponse } from "next/server";

import { getAccessToken, getCurrentUser } from "@/lib/supabase/server";

/**
 * Streams a lease document upload through to the API.
 *
 * <h3>Why this exists instead of a server action</h3>
 *
 * A server action would serialise the file into the action payload, meaning a
 * 50 MB scan crosses the network twice and is buffered in the Next process on
 * the way. This handler attaches the session's bearer token and hands the body
 * straight on.
 *
 * <h3>What it does not do</h3>
 *
 * It does not validate the file. It cannot: the only honest check is on the
 * bytes, and that happens in the API where the result can be acted on. This
 * handler is a credential-attaching pipe and nothing else, which is why it has
 * no idea what a lease is.
 */
export const dynamic = "force-dynamic";

/** The API's limit, so an oversized body is refused before it is forwarded. */
const MAXIMUM_BYTES = 50 * 1024 * 1024;

export async function POST(request: Request) {
  const user = await getCurrentUser();

  if (!user) {
    return NextResponse.json({ error: "Not signed in." }, { status: 401 });
  }

  const form = await request.formData();
  const file = form.get("file");
  const organizationId = form.get("organizationId");
  const name = form.get("name");

  if (!(file instanceof File)) {
    return NextResponse.json({ error: "No file was uploaded." }, { status: 400 });
  }

  if (typeof organizationId !== "string" || organizationId.length === 0) {
    return NextResponse.json(
      { error: "No workspace was specified." },
      { status: 400 },
    );
  }

  if (file.size > MAXIMUM_BYTES) {
    return NextResponse.json(
      { error: "That file is larger than the 50 MB limit." },
      { status: 413 },
    );
  }

  const token = await getAccessToken();
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

  const forwarded = new FormData();
  forwarded.append("file", file, file.name);

  // Optional, and only forwarded when it is actually something. An empty
  // field would be sent as "" and stored as a blank label, which renders as a
  // document with no name at all.
  if (typeof name === "string" && name.trim().length > 0) {
    forwarded.append("name", name.trim());
  }

  // Built rather than inlined: under exactOptionalPropertyTypes, passing an
  // explicitly undefined "headers" is not the same as omitting it, and the
  // former is not a valid RequestInit.
  //
  // Content-Type is deliberately absent either way: fetch generates the
  // multipart boundary along with the body, and naming the type without the
  // boundary produces a request the server cannot parse.
  const headers = new Headers();

  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  const response = await fetch(
    `${apiBaseUrl}/api/v1/organizations/${organizationId}/documents`,
    {
      method: "POST",
      headers,
      body: forwarded,
      cache: "no-store",
    },
  );

  if (!response.ok) {
    // The API returns ProblemDetails with no internal detail, so its message
    // is safe to pass on -- and far more useful than a generic one, since it
    // names why the file was refused.
    const payload = (await response.json().catch(() => null)) as
      | { detail?: string; title?: string }
      | null;

    return NextResponse.json(
      {
        error:
          payload?.detail ?? payload?.title ?? "The document could not be uploaded.",
      },
      { status: response.status },
    );
  }

  return NextResponse.json(await response.json(), { status: 201 });
}
