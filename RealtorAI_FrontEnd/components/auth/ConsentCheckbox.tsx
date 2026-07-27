import { cn } from "@/lib/utils/cn";

interface ConsentCheckboxProps {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  error?: string;
}

/** Gates both the primary submit button and "Continue with Google" (spec FR-016, US1 AC5, US2 AC4). */
export function ConsentCheckbox({
  label,
  checked,
  onCheckedChange,
  error,
}: ConsentCheckboxProps) {
  return (
    <div>
      <label className="flex items-start gap-2 text-sm text-[var(--color-text)]">
        <input
          type="checkbox"
          name="acceptedTerms"
          checked={checked}
          onChange={(event) => onCheckedChange(event.target.checked)}
          aria-invalid={!!error}
          className={cn(
            "mt-0.5 h-4 w-4 rounded border-[var(--color-border)] text-[var(--color-accent)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
          )}
        />
        <span>{label}</span>
      </label>
      {error && <p className="mt-1 text-sm text-[var(--color-danger)]">{error}</p>}
    </div>
  );
}
