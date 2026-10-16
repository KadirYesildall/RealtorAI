import type { Dictionary } from "@/lib/i18n/getDictionary";
import type { ListingStatus } from "@/lib/api/listings";
import { cn } from "@/lib/utils/cn";
import { CheckIcon } from "@/components/ui/icons";

/**
 * A listing's status as a word, never as colour alone (constitution Section E). Approved adds a
 * glyph; archived is muted. Semantic tokens only, so both themes hold contrast. Presentation only.
 */
export function ListingStatusBadge({
  status,
  labels,
}: {
  status: ListingStatus;
  labels: Dictionary["listings"]["status"];
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium",
        status === "approved"
          ? "border-[color-mix(in_srgb,var(--color-accent)_35%,transparent)] bg-[var(--color-accent-soft)] text-[var(--color-accent)]"
          : status === "archived"
            ? "border-[var(--color-border)] bg-[var(--color-surface)] text-[var(--color-text-muted)]"
            : "border-[var(--color-border-strong)] bg-[var(--color-surface-muted)] text-[var(--color-text)]"
      )}
    >
      {status === "approved" && <CheckIcon className="h-3.5 w-3.5" />}
      {labels[status]}
    </span>
  );
}
