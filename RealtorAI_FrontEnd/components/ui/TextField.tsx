import { type InputHTMLAttributes, type ReactNode } from "react";
import { Input } from "@/components/ui/Input";
import { AlertIcon } from "@/components/ui/icons";

export interface TextFieldProps extends Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "id"
> {
  /** Required rather than generated: it is what ties the visible <label> to the input. */
  id: string;
  label: string;
  icon?: ReactNode;
  hint?: ReactNode;
  error?: string;
  /** Rendered inside the field's trailing edge, e.g. the password reveal toggle. */
  trailing?: ReactNode;
}

/**
 * Label + field + hint/error, assembled the same way everywhere so every form inherits the
 * same spacing, focus treatment and error semantics. Wraps the Input primitive rather than
 * restyling an input from scratch (constitution Section E: reuse primitives).
 */
export function TextField({
  id,
  label,
  icon,
  hint,
  error,
  trailing,
  ...inputProps
}: TextFieldProps) {
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  const describedBy = [errorId, hintId].filter(Boolean).join(" ") || undefined;

  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium text-[var(--color-text)]">
        {label}
      </label>

      <div className="relative">
        {icon && (
          <span className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-[var(--color-text-muted)] [&>svg]:h-[18px] [&>svg]:w-[18px]">
            {icon}
          </span>
        )}
        <Input
          id={id}
          invalid={!!error}
          withLeadingIcon={!!icon}
          withTrailing={!!trailing}
          aria-describedby={describedBy}
          {...inputProps}
        />
        {trailing && (
          <span className="absolute right-1.5 top-1/2 -translate-y-1/2">{trailing}</span>
        )}
      </div>

      {error && (
        <p
          id={errorId}
          className="flex items-start gap-1.5 text-sm text-[var(--color-danger)]"
        >
          <AlertIcon className="mt-px h-4 w-4 shrink-0" />
          <span>{error}</span>
        </p>
      )}
      {hint && !error && (
        <div id={hintId} className="text-sm text-[var(--color-text-muted)]">
          {hint}
        </div>
      )}
    </div>
  );
}
