"use client"; // Error boundaries must be Client Components

import { useEffect, useState } from "react";
import { Button } from "@/components/ui/Button";

// error.tsx cannot use the server-only getDictionary(); this small inline copy mirrors
// dashboard.error in lib/i18n/dictionaries/{en,tr}.json for the two locales this project
// supports (constitution VI) without pulling server-only code into a Client Component.
const COPY = {
  en: { title: "We couldn't load your dashboard", body: "Something went wrong on our end. Please try again.", retry: "Try again" },
  tr: { title: "Panelinizi yükleyemedik", body: "Bizden kaynaklanan bir sorun oluştu. Lütfen tekrar deneyin.", retry: "Tekrar deneyin" },
};

export default function DashboardError({
  error,
  retry,
}: {
  error: Error & { digest?: string };
  retry: () => void;
}) {
  const [locale] = useState<"en" | "tr">(() => {
    const match = document.cookie.match(/(?:^|; )NEXT_LOCALE=(en|tr)/);
    return (match?.[1] as "en" | "tr" | undefined) ?? "en";
  });

  useEffect(() => {
    console.error(error);
  }, [error]);

  const t = COPY[locale];

  return (
    <div className="flex min-h-[50vh] flex-col items-center justify-center gap-3 text-center">
      <h2 className="text-lg font-semibold text-[var(--color-text)]">{t.title}</h2>
      <p className="text-sm text-[var(--color-text-muted)]">{t.body}</p>
      <Button onClick={() => retry()}>{t.retry}</Button>
    </div>
  );
}
