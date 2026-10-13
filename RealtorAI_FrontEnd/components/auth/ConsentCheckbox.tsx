import { Checkbox } from "@/components/ui/Checkbox";

interface ConsentCheckboxProps {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  error?: string;
  /** Distinct per screen so the label association stays unambiguous if both ever co-exist. */
  id?: string;
}

/** Gates both the primary submit button and "Continue with Google" (spec FR-016, US1 AC5, US2 AC4). */
export function ConsentCheckbox({
  label,
  checked,
  onCheckedChange,
  error,
  id = "acceptedTerms",
}: ConsentCheckboxProps) {
  return (
    <Checkbox
      id={id}
      name="acceptedTerms"
      label={label}
      checked={checked}
      error={error}
      onChange={(event) => onCheckedChange(event.target.checked)}
    />
  );
}
