import "server-only";
import { cookies } from "next/headers";
import {
  defaultThemePreference,
  isThemePreference,
  THEME_COOKIE,
  type ThemePreference,
} from "@/lib/theme/theme";

/** Mirrors lib/i18n/getLocale.ts: a cookie read in a Server Component, no DB round-trip. */
export async function getThemePreference(): Promise<ThemePreference> {
  const value = (await cookies()).get(THEME_COOKIE)?.value;
  return isThemePreference(value) ? value : defaultThemePreference;
}
