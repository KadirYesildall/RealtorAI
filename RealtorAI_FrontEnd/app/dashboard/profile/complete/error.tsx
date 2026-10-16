"use client"; // Error boundaries must be Client Components

import { useEffect, useState } from "react";
import { Button } from "@/components/ui/Button";

// error.tsx cannot use the server-only getDictionary(); this inline copy mirrors
// profileCompletion.errors.loadFailed and common.tryAgain in lib/i18n/dictionaries/{en,tr}.json.
const COPY = {
  en: { body: "We couldn't load your profile right now. Please try again.", retry: "Try again" },
  tr: { body: "Profilinizi şu anda yükleyemedik. Lütfen tekrar deneyin.", retry: "Tekrar deneyin" },
};

/**
 * Next 16.3 passes `retry` (and `reset`) to error components — see
 * node_modules/next/dist/client/components/error-boundary.d.ts.
 */
export default function ProfileCompletionError({
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
      <p role="alert" className="text-sm text-[var(--color-text-muted)]">
        {t.body}
      </p>
      <Button onClick={() => retry()}>{t.retry}</Button>
    </div>
  );
}
