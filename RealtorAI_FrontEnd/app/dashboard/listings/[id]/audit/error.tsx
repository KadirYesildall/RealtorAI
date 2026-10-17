"use client"; // Error boundaries must be Client Components

import { SegmentError } from "@/components/ui/SegmentError";

// Mirrors listings.errors.loadAuditFailed and common.tryAgain in lib/i18n/dictionaries/{en,tr}.json.
const COPY = {
  en: { body: "We couldn't load this audit trail right now. Please try again.", retry: "Try again" },
  tr: { body: "Bu denetim kaydını şu anda yükleyemedik. Lütfen tekrar deneyin.", retry: "Tekrar deneyin" },
};

export default function AuditError({ error, retry }: { error: Error & { digest?: string }; retry: () => void }) {
  return <SegmentError error={error} retry={retry} copy={COPY} />;
}
