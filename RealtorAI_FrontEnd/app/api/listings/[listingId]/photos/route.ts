import type { NextRequest } from "next/server";
import { z } from "zod";
import { resolveBaseUrl } from "@/lib/api/client";
import { createClient } from "@/lib/supabase/server";

/**
 * T080 — photo upload, from the wizard to the .NET API (FR-002, FR-003).
 *
 * Why a Route Handler and not a Server Action: a Server Action body is capped at 1 MB by
 * default, and a photo is up to 50 MiB. This handler forwards the browser's multipart body to
 * the API byte for byte — read whole, never re-encoded — so the bytes the API hashes are the
 * bytes the agent chose (FR-001). The wizard sends one photo per request, so one body is at most
 * one photo. Streaming the body through instead (`duplex: "half"`) was tried first: the upstream
 * request never completed against Kestrel, so the body is read and then sent.
 *
 * Why proxy.ts skips this path: when proxy runs, Next.js buffers the request body so both proxy
 * and the handler can read it, and silently truncates it past `proxyClientMaxBodySize` (10 MB) —
 * a truncated photo would arrive as a "damaged" file. Session refresh, the one thing proxy does
 * here, happens in this handler instead.
 *
 * Authorization is the API's. The handler attaches the session's token; the API decides whether
 * this agent may add photos to this listing (FR-063) and judges each file.
 */
export async function POST(request: NextRequest, context: RouteContext<"/api/listings/[listingId]/photos">) {
  const { listingId } = await context.params;

  if (!z.uuid().safeParse(listingId).success) {
    return Response.json({ title: "Not found", status: 404 }, { status: 404 });
  }

  const supabase = await createClient();

  // getUser() validates the session against Supabase and refreshes it when needed — the job
  // proxy.ts does for every other path.
  const {
    data: { user },
  } = await supabase.auth.getUser();
  const {
    data: { session },
  } = await supabase.auth.getSession();

  if (!user || !session) {
    return Response.json({ title: "Sign in again", status: 401 }, { status: 401 });
  }

  const contentType = request.headers.get("content-type");

  if (!contentType?.startsWith("multipart/form-data")) {
    return Response.json({ title: "Send photos as multipart/form-data", status: 422 }, { status: 422 });
  }

  const upstream = await fetch(`${resolveBaseUrl()}/api/listings/${encodeURIComponent(listingId)}/photos`, {
    method: "POST",
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${session.access_token}`,
      // The boundary is in this header, so it is passed on unchanged with the body it describes.
      "Content-Type": contentType,
    },
    body: await request.arrayBuffer(),
    cache: "no-store",
  });

  return new Response(upstream.body, {
    status: upstream.status,
    headers: { "Content-Type": upstream.headers.get("content-type") ?? "application/json" },
  });
}
