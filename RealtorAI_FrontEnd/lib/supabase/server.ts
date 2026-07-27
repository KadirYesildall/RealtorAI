import "server-only";
import { cookies } from "next/headers";
import { createServerClient } from "@supabase/ssr";
import type { Database } from "@/lib/supabase/database.types";

/**
 * Server-only Supabase client. Uses the anon key bound to the caller's session cookie —
 * RLS enforces per-row access. The service-role key MUST NOT be used here (constitution
 * Constraints: every mutation in this feature is user-scoped and RLS-protected).
 *
 * Create a fresh client per request; never share/cache across requests.
 */
export async function createClient() {
  const cookieStore = await cookies();

  return createServerClient<Database>(
    process.env.NEXT_PUBLIC_SUPABASE_URL!,
    process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY!,
    {
      cookies: {
        getAll() {
          return cookieStore.getAll();
        },
        setAll(cookiesToSet) {
          try {
            cookiesToSet.forEach(({ name, value, options }) => {
              cookieStore.set(name, value, options);
            });
          } catch {
            // Called from a Server Component render, where cookies cannot be written.
            // Session refresh is handled by proxy.ts in that case — safe to ignore here.
          }
        },
      },
    }
  );
}
