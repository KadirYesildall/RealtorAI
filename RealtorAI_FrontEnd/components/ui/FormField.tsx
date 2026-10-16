import { type ReactNode } from "react";
import { AlertIcon } from "@/components/ui/icons";

/**
 * Label, hint and error around any control — the same shell TextField draws, for controls
 * that are not a single-line input (Textarea, Select). The control is rendered by the caller
 * with the ids this passes back, so the label, hint and error stay wired to it.
 */
export function FormField({
  id,
  label,
  hint,
  error,
  children,
}: {
  id: string;
  label: string;
  hint?: ReactNode;
  error?: string;
  children: (describedBy: string | undefined) => ReactNode;
}) {
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  const describedBy = [errorId, hintId].filter(Boolean).join(" ") || undefined;

  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium text-[var(--color-text)]">
        {label}
      </label>

      {children(describedBy)}

      {error && (
        <p id={errorId} className="flex items-start gap-1.5 text-sm text-[var(--color-danger)]">
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
