import { type InputHTMLAttributes, forwardRef } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils/cn";

const inputVariants = cva(
  "block w-full rounded-[var(--radius-md)] border bg-[var(--color-surface)] px-3.5 h-11 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] transition-[border-color,box-shadow] focus-visible:outline-none focus-visible:border-[var(--color-accent)] focus-visible:ring-2 focus-visible:ring-[color-mix(in_srgb,var(--color-accent)_28%,transparent)] disabled:opacity-50",
  {
    variants: {
      invalid: {
        true: "border-[var(--color-danger)]",
        false:
          "border-[var(--color-border-strong)] hover:border-[var(--color-text-muted)]",
      },
      /** Reserves room for the icon TextField paints inside the field's leading edge. */
      withLeadingIcon: {
        true: "pl-11",
        false: "",
      },
      /** Reserves room for a trailing control, e.g. the password reveal toggle. */
      withTrailing: {
        true: "pr-11",
        false: "",
      },
    },
    defaultVariants: {
      invalid: false,
      withLeadingIcon: false,
      withTrailing: false,
    },
  }
);

export interface InputProps
  extends InputHTMLAttributes<HTMLInputElement>, VariantProps<typeof inputVariants> {}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  (
    {
      className,
      invalid,
      withLeadingIcon,
      withTrailing,
      "aria-invalid": ariaInvalid,
      ...props
    },
    ref
  ) => {
    return (
      <input
        ref={ref}
        aria-invalid={ariaInvalid ?? invalid ?? undefined}
        className={cn(
          inputVariants({ invalid, withLeadingIcon, withTrailing }),
          className
        )}
        {...props}
      />
    );
  }
);
Input.displayName = "Input";
