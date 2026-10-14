import { type SelectHTMLAttributes, forwardRef } from "react";
import { inputVariants } from "@/components/ui/Input";
import { cn } from "@/lib/utils/cn";

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  invalid?: boolean;
}

/**
 * A native select with the Input primitive's treatment. Native on purpose: it is keyboard- and
 * screen-reader-correct on every platform without any script.
 */
export const Select = forwardRef<HTMLSelectElement, SelectProps>(
  ({ className, invalid, "aria-invalid": ariaInvalid, ...props }, ref) => (
    <select
      ref={ref}
      aria-invalid={ariaInvalid ?? invalid ?? undefined}
      className={cn(inputVariants({ invalid: !!invalid }), "appearance-auto pr-8", className)}
      {...props}
    />
  )
);
Select.displayName = "Select";
