import { Card } from "@/components/ui/Card";
import { SparkIcon } from "@/components/ui/icons";
import type { Dictionary } from "@/lib/i18n/getDictionary";

export function UsageSummaryCard({
  dict,
  usageCount,
}: {
  dict: Dictionary;
  usageCount: number;
}) {
  return (
    <Card elevation="elevated" className="max-w-xs">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-sm text-[var(--color-text-muted)]">
            {dict.dashboard.usageLabel}
          </p>
          <p className="mt-2 text-3xl font-semibold tracking-tight text-[var(--color-text)]">
            {usageCount}
          </p>
        </div>
        <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-[var(--color-accent-soft)] text-[var(--color-accent)]">
          <SparkIcon className="h-[18px] w-[18px]" />
        </span>
      </div>
    </Card>
  );
}
