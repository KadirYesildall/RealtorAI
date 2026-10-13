import "server-only";
import { createClient } from "@/lib/supabase/server";
import { createApiClient, type ApiClient } from "@/lib/api/client";

/**
 * T012 — API client for Server Components, Server Actions, and Route Handlers.
 *
 * Reads the access token from the request's Supabase session cookie. Created fresh per
 * request and never cached across requests, for the same reason `lib/supabase/server.ts`
 * says so: a shared client would carry one user's token into another user's request.
 */
export function createServerApiClient(): ApiClient {
  return createApiClient(async () => {
    const supabase = await createClient();

    // getSession() reads the cookie without a round trip. That is appropriate here because
    // the token is not being trusted locally — it is forwarded to the API, which validates
    // the signature against Supabase's JWKS before acting on it.
    const {
      data: { session },
    } = await supabase.auth.getSession();

    return session?.access_token ?? null;
  });
}
