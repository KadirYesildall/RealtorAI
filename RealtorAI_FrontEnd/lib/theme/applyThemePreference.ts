import {
  THEME_COOKIE,
  THEME_COOKIE_MAX_AGE,
  themeAttribute,
  type ThemePreference,
} from "@/lib/theme/theme";

/**
 * Browser-only: persists the choice and repaints immediately.
 *
 * Kept out of the component because React Compiler's immutability rule (correctly) rejects
 * writing to module-external values like `document.cookie` inside component code — the
 * effect belongs in a plain function the component calls from its event handler.
 *
 * The cookie is deliberately not httpOnly: it carries a display preference only, and the
 * client has to be able to write it for the switch to be instant.
 */
export function applyThemePreference(next: ThemePreference): void {
  document.cookie = `${THEME_COOKIE}=${next}; path=/; max-age=${THEME_COOKIE_MAX_AGE}; samesite=lax`;

  const attribute = themeAttribute(next);
  if (attribute) {
    document.documentElement.setAttribute("data-theme", attribute);
  } else {
    // "system" removes the attribute so the prefers-color-scheme rule takes over again.
    document.documentElement.removeAttribute("data-theme");
  }
}
