import "server-only";
import { cookies } from "next/headers";
import { defaultLocale, isLocale, type Locale } from "@/lib/i18n/getDictionary";

/**
 * Pre-auth locale: the NEXT_LOCALE cookie set by proxy.ts from Accept-Language.
 * Post-auth pages use the user's saved `profiles.preferred_language` instead (source of
 * truth once it exists) — see app/dashboard/layout.tsx.
 */
export async function getLocale(): Promise<Locale> {
  const cookieStore = await cookies();
  const value = cookieStore.get("NEXT_LOCALE")?.value;
  return isLocale(value) ? value : defaultLocale;
}
