import type { Dictionary } from "@/lib/i18n/getDictionary";
import type { MembershipStatus } from "@/lib/api/brokerage";
import { cn } from "@/lib/utils/cn";
import { AlertIcon, CheckIcon } from "@/components/ui/icons";

/**
 * A membership's status, distinguishable without colour (constitution Section E): every state
 * carries its word, and approved/ended states add a distinct glyph, so the badge reads the
 * same to someone who cannot tell the tints apart. Presentation only.
 */
export function MembershipStatusBadge({
  status,
  labels,
}: {
  status: MembershipStatus;
  labels: Dictionary["brokerage"]["status"];
}) {
  const ended = status === "rejected" || status === "revoked";

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium",
        status === "approved" &&
          "border-[color-mix(in_srgb,var(--color-accent)_35%,transparent)] bg-[var(--color-accent-soft)] text-[var(--color-accent)]",
        status === "pending" &&
          "border-[var(--color-border-strong)] bg-[var(--color-surface-muted)] text-[var(--color-text)]",
        ended &&
          "border-[color-mix(in_srgb,var(--color-danger)_35%,transparent)] bg-[var(--color-danger-soft)] text-[var(--color-danger)]"
      )}
    >
      {status === "approved" && <CheckIcon className="h-3.5 w-3.5" />}
      {ended && <AlertIcon className="h-3.5 w-3.5" />}
      {labels[status]}
    </span>
  );
}
