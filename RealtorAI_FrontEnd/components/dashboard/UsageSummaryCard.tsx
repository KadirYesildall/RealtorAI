import { Card } from "@/components/ui/Card";
import type { Dictionary } from "@/lib/i18n/getDictionary";

export function UsageSummaryCard({
  dict,
  usageCount,
}: {
  dict: Dictionary;
  usageCount: number;
}) {
  return (
    <Card className="max-w-xs">
      <p className="text-sm text-[var(--color-text-muted)]">
        {dict.dashboard.usageLabel}
      </p>
      <p className="mt-2 text-3xl font-semibold text-[var(--color-text)]">{usageCount}</p>
    </Card>
  );
}
