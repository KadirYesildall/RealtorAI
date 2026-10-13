import { type InputHTMLAttributes, type ReactNode } from "react";
import { cn } from "@/lib/utils/cn";
import { AlertIcon, CheckIcon } from "@/components/ui/icons";

export interface CheckboxProps extends Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "type" | "id"
> {
  id: string;
  label: ReactNode;
  error?: string;
}

/**
 * A real `<input type="checkbox">` restyled with `appearance-none`, rather than a div
 * pretending to be one: it keeps native keyboard behaviour, form participation and the
 * label association that both screen readers and the Playwright specs rely on. The tick
 * is an overlaid icon (pointer-events-none) because `::before` on a replaced element is
 * not reliably stylable across browsers.
 */
export function Checkbox({ id, label, error, className, ...props }: CheckboxProps) {
  const errorId = error ? `${id}-error` : undefined;

  return (
    <div>
      <div className="flex items-start gap-3">
        <span className="relative flex h-5 w-5 shrink-0 items-center justify-center">
          <input
            id={id}
            type="checkbox"
            aria-invalid={!!error || undefined}
            aria-describedby={errorId}
            className={cn(
              "peer h-5 w-5 cursor-pointer appearance-none rounded-[6px] border bg-[var(--color-surface)] transition-colors",
              "border-[var(--color-border-strong)] hover:border-[var(--color-text-muted)]",
              "checked:border-[var(--color-accent)] checked:bg-[var(--color-accent)]",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--color-surface)]",
              "aria-[invalid=true]:border-[var(--color-danger)]",
              className
            )}
            {...props}
          />
          <CheckIcon className="pointer-events-none absolute h-3 w-3 text-[var(--color-accent-foreground)] opacity-0 peer-checked:opacity-100" />
        </span>
        <label
          htmlFor={id}
          className="cursor-pointer select-none text-sm leading-5 text-[var(--color-text)]"
        >
          {label}
        </label>
      </div>
      {error && (
        <p
          id={errorId}
          className="mt-1.5 flex items-start gap-1.5 text-sm text-[var(--color-danger)]"
        >
          <AlertIcon className="mt-px h-4 w-4 shrink-0" />
          <span>{error}</span>
        </p>
      )}
    </div>
  );
}
