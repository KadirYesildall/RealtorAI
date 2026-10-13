"use client";

import { useState } from "react";
import { themePreferences, type ThemePreference } from "@/lib/theme/theme";
import { applyThemePreference } from "@/lib/theme/applyThemePreference";
import { MonitorIcon, MoonIcon, SunIcon } from "@/components/ui/icons";

const ICONS = {
  light: SunIcon,
  dark: MoonIcon,
  system: MonitorIcon,
} as const;

export type ThemeLabels = {
  label: string;
  light: string;
  dark: string;
  system: string;
};

/**
 * Three-state theme control (light / dark / follow-system).
 *
 * Switching is done entirely on the client — the cookie is written with `document.cookie`
 * and `data-theme` is set straight on <html> — so the flip is instant with no server
 * round-trip. The cookie is read back by app/layout.tsx on the next request, which is what
 * keeps server-rendered HTML in agreement with what the user chose. That also means the
 * cookie must stay non-httpOnly; it holds a display preference, nothing sensitive.
 *
 * Built on real radio inputs rather than buttons so the group gets correct semantics and
 * arrow-key navigation from the browser instead of hand-rolled keyboard handling.
 */
export function ThemeToggle({
  labels,
  initial,
}: {
  labels: ThemeLabels;
  /** Server-read cookie value — passed in (not read from document) to avoid a hydration mismatch. */
  initial: ThemePreference;
}) {
  const [preference, setPreference] = useState<ThemePreference>(initial);

  function apply(next: ThemePreference) {
    setPreference(next);
    applyThemePreference(next);
  }

  return (
    <fieldset className="inline-flex items-center gap-0.5 rounded-full border border-[var(--color-border)] bg-[var(--color-surface)] p-0.5">
      <legend className="sr-only">{labels.label}</legend>
      {themePreferences.map((option) => {
        const OptionIcon = ICONS[option];
        return (
          <label
            key={option}
            className="relative flex h-7 w-7 cursor-pointer items-center justify-center rounded-full text-[var(--color-text-muted)] transition-colors hover:text-[var(--color-text)] has-[:checked]:bg-[var(--color-accent-soft)] has-[:checked]:text-[var(--color-accent)] has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-[var(--color-accent)] has-[:focus-visible]:ring-offset-2 has-[:focus-visible]:ring-offset-[var(--color-surface)]"
          >
            <input
              type="radio"
              name="theme-preference"
              value={option}
              checked={preference === option}
              onChange={() => apply(option)}
              className="sr-only"
            />
            <OptionIcon className="h-4 w-4" />
            <span className="sr-only">{labels[option]}</span>
          </label>
        );
      })}
    </fieldset>
  );
}
