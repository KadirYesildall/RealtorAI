"use client"; // Error boundaries must be Client Components

import { SegmentError } from "@/components/ui/SegmentError";

// Mirrors listings.errors.loadListingFailed and common.tryAgain in lib/i18n/dictionaries/{en,tr}.json.
const COPY = {
  en: { body: "We couldn't load this listing right now. Please try again.", retry: "Try again" },
  tr: { body: "Bu ilanı şu anda yükleyemedik. Lütfen tekrar deneyin.", retry: "Tekrar deneyin" },
};

export default function NewListingError({ error, retry }: { error: Error & { digest?: string }; retry: () => void }) {
  return <SegmentError error={error} retry={retry} copy={COPY} />;
}
