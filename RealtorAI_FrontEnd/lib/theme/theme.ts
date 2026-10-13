/**
 * Theme preference — shared by the server (which stamps `data-theme` on <html>) and the
 * client toggle. Deliberately NOT stored on `profiles`: unlike language, a theme choice is
 * a per-device preference (dark on a laptop at night, light on a shared office screen), so
 * it lives in a cookie rather than following the account across devices.
 */
export const themePreferences = ["light", "dark", "system"] as const;

export type ThemePreference = (typeof themePreferences)[number];

export const defaultThemePreference: ThemePreference = "system";

export const THEME_COOKIE = "theme";

/** One year — a theme choice should outlive a session without being permanent. */
export const THEME_COOKIE_MAX_AGE = 60 * 60 * 24 * 365;

export function isThemePreference(
  value: string | undefined | null
): value is ThemePreference {
  return !!value && (themePreferences as readonly string[]).includes(value);
}

/**
 * The value for `<html data-theme>`. "system" maps to *no attribute* so the
 * `prefers-color-scheme` rule in globals.css takes over — that is what lets the OS theme
 * paint correctly on the first frame without a blocking inline script.
 */
export function themeAttribute(
  preference: ThemePreference
): "light" | "dark" | undefined {
  return preference === "system" ? undefined : preference;
}
