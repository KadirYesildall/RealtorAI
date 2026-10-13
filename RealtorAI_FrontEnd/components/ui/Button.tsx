import { type ButtonHTMLAttributes, forwardRef } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils/cn";

/** Exported so link elements can take the same shape without nesting a button inside an <a>. */
export const buttonVariants = cva(
  // The ring offset colour has to be a token, not Tailwind's default white — an offset
  // hard-coded to white draws a bright halo around focused controls in dark mode.
  "relative inline-flex items-center justify-center gap-2 rounded-[var(--radius-md)] text-sm font-medium transition-[background-color,border-color,color,box-shadow,transform] duration-150 disabled:opacity-50 disabled:pointer-events-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--color-surface)]",
  {
    variants: {
      variant: {
        primary:
          "bg-[var(--color-accent)] text-[var(--color-accent-foreground)] shadow-[var(--shadow-card)] hover:bg-[var(--color-accent-hover)] active:translate-y-px",
        secondary:
          "bg-[var(--color-surface)] text-[var(--color-text)] border border-[var(--color-border)] hover:border-[var(--color-border-strong)] hover:bg-[var(--color-surface-muted)] active:translate-y-px",
        ghost: "text-[var(--color-text)] hover:bg-[var(--color-surface-muted)]",
      },
      size: {
        md: "h-11 px-4",
        lg: "h-12 px-5 text-[0.9375rem]",
        sm: "h-9 px-3 text-sm",
      },
    },
    defaultVariants: {
      variant: "primary",
      size: "md",
    },
  }
);

export interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {
  /** Shows a spinner and blocks input. The label stays rendered so the accessible name never changes mid-flight. */
  loading?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, loading, disabled, children, ...props }, ref) => {
    return (
      <button
        ref={ref}
        disabled={disabled || loading}
        aria-busy={loading || undefined}
        className={cn(buttonVariants({ variant, size }), className)}
        {...props}
      >
        {loading && (
          <span
            aria-hidden="true"
            className="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent opacity-70"
          />
        )}
        {children}
      </button>
    );
  }
);
Button.displayName = "Button";
