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
      <select
        value={currentLocale}
        disabled={pending}
        onChange={(event) => handleChange(event.target.value as Locale)}
        className="rounded-[var(--radius-sm)] border border-[var(--color-border)] bg-[var(--color-surface)] px-2 py-1 text-sm text-[var(--color-text)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
      >
        <option value="en">{t.en}</option>
        <option value="tr">{t.tr}</option>
      </select>
    </label>
  );
}
