import { type ReactNode } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils/cn";
import { AlertIcon } from "@/components/ui/icons";

const alertVariants = cva(
  "flex items-start gap-2.5 rounded-[var(--radius-md)] border p-3 text-sm",
  {
    variants: {
      tone: {
        danger:
          "border-[color-mix(in_srgb,var(--color-danger)_35%,transparent)] bg-[var(--color-danger-soft)] text-[var(--color-danger)]",
        accent:
          "border-[color-mix(in_srgb,var(--color-accent)_35%,transparent)] bg-[var(--color-accent-soft)] text-[var(--color-accent)]",
      },
    },
    defaultVariants: { tone: "danger" },
  }
);

export interface AlertProps extends VariantProps<typeof alertVariants> {
  children: ReactNode;
  className?: string;
  /**
   * Form submission errors appear after an interaction, so they default to `role="alert"`
   * to be announced. Errors rendered on first paint (e.g. the OAuth-cancelled banner read
   * off the query string) pass `role="status"` instead — nothing was interrupted.
   */
  role?: "alert" | "status";
}

export function Alert({ children, className, tone, role = "alert" }: AlertProps) {
  return (
    <div role={role} className={cn(alertVariants({ tone }), className)}>
      <AlertIcon className="mt-0.5 h-4 w-4 shrink-0" />
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  );
}
