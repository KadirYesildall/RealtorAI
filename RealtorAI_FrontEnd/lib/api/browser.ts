import { createClient } from "@/lib/supabase/client";
import { createApiClient, type ApiClient } from "@/lib/api/client";

/**
 * T012 — API client for Client Components, intended to be used through TanStack Query.
 *
 * The token is resolved per request rather than captured once at construction: @supabase/ssr
 * refreshes the access token in the background, so a captured token would start failing with
 * 401 after an hour of an open tab. Asking for it on each call means the caller always sends
 * the current one.
 */
export function createBrowserApiClient(): ApiClient {
  const supabase = createClient();

  return createApiClient(async () => {
    const {
      data: { session },
    } = await supabase.auth.getSession();

    return session?.access_token ?? null;
  });
}
