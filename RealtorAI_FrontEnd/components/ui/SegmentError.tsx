"use client";

import { useEffect, useState } from "react";
import { Button } from "@/components/ui/Button";

type Copy = Record<"en" | "tr", { body: string; retry: string }>;

/**
 * The body of a route segment's error.tsx (T163). Error boundaries are Client Components and
 * cannot call the server-only getDictionary(), so each segment passes its own copy — which
 * mirrors keys in lib/i18n/dictionaries/{en,tr}.json — and the locale is read from the same cookie
 * the dictionaries are chosen by.
 */
export function SegmentError({ error, retry, copy }: { error: Error & { digest?: string }; retry: () => void; copy: Copy }) {
  const [locale] = useState<"en" | "tr">(() => {
    const match = document.cookie.match(/(?:^|; )NEXT_LOCALE=(en|tr)/);
    return (match?.[1] as "en" | "tr" | undefined) ?? "en";
  });

  useEffect(() => {
    console.error(error);
  }, [error]);

  const t = copy[locale];

  return (
    <div className="flex min-h-[50vh] flex-col items-center justify-center gap-3 text-center">
      <p role="alert" className="text-sm text-[var(--color-text-muted)]">
        {t.body}
      </p>
      <Button onClick={() => retry()}>{t.retry}</Button>
    </div>
  );
}
