"use client";

import { useTransition } from "react";
import { useRouter } from "next/navigation";
import { updateLanguagePreferenceAction } from "@/app/actions/profile";
import type { Dictionary, Locale } from "@/lib/i18n/getDictionary";

export function LanguageSwitcher({
  dict,
  currentLocale,
}: {
  dict: Dictionary;
  currentLocale: Locale;
}) {
  const router = useRouter();
  const [pending, startTransition] = useTransition();
  const t = dict.dashboard.language;

  function handleChange(next: Locale) {
    startTransition(async () => {
      const result = await updateLanguagePreferenceAction(next);
      if (result.success) {
        router.refresh();
      }
    });
  }

  return (
    <label className="flex items-center gap-2 text-sm text-[var(--color-text-muted)]">
      {t.label}
      {/* `color-scheme` (globals.css) is what makes the native dropdown panel itself
          render dark — styling the closed <select> alone would leave a white popup. */}
      <select
        value={currentLocale}
        disabled={pending}
        onChange={(event) => handleChange(event.target.value as Locale)}
        className="h-8 rounded-[var(--radius-sm)] border border-[var(--color-border-strong)] bg-[var(--color-surface)] px-2 text-sm text-[var(--color-text)] transition-colors hover:border-[var(--color-text-muted)] focus-visible:outline-none focus-visible:border-[var(--color-accent)] focus-visible:ring-2 focus-visible:ring-[color-mix(in_srgb,var(--color-accent)_28%,transparent)] disabled:opacity-60"
      >
        <option value="en">{t.en}</option>
        <option value="tr">{t.tr}</option>
      </select>
    </label>
  );
}
