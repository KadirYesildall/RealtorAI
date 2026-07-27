import { type InputHTMLAttributes, forwardRef } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils/cn";

const inputVariants = cva(
  "block w-full rounded-[var(--radius-sm)] border bg-[var(--color-surface)] px-3 h-11 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)] disabled:opacity-50",
  {
    variants: {
      invalid: {
        true: "border-[var(--color-danger)]",
        false: "border-[var(--color-border)]",
      },
    },
    defaultVariants: {
      invalid: false,
    },
  }
);

export interface InputProps
  extends InputHTMLAttributes<HTMLInputElement>,
    VariantProps<typeof inputVariants> {}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ className, invalid, "aria-invalid": ariaInvalid, ...props }, ref) => {
    return (
      <input
        ref={ref}
        aria-invalid={ariaInvalid ?? invalid ?? undefined}
        className={cn(inputVariants({ invalid }), className)}
        {...props}
      />
    );
  }
);
Input.displayName = "Input";
