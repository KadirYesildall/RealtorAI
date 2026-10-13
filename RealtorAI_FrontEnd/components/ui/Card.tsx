import { type HTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils/cn";

const cardVariants = cva(
  "rounded-[var(--radius-lg)] border border-[var(--color-border)]",
  {
    variants: {
      padding: {
        md: "p-6",
        lg: "p-7 sm:p-8",
        none: "",
      },
      /**
       * `flat` sits directly on a muted page background; `elevated` lifts the card off it.
       * The shadow is a token so dark mode can swap a soft grey halo (which reads as fog on
       * a near-black surface) for a deeper, higher-opacity one.
       */
      elevation: {
        flat: "bg-[var(--color-surface)]",
        elevated: "bg-[var(--color-surface-elevated)] shadow-[var(--shadow-card)]",
      },
    },
    defaultVariants: {
      padding: "md",
      elevation: "flat",
    },
  }
);

export interface CardProps
  extends HTMLAttributes<HTMLDivElement>, VariantProps<typeof cardVariants> {}

export function Card({ className, padding, elevation, ...props }: CardProps) {
  return (
    <div className={cn(cardVariants({ padding, elevation }), className)} {...props} />
  );
}
