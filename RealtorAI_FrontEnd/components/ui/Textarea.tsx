import { type TextareaHTMLAttributes, forwardRef } from "react";
import { inputVariants } from "@/components/ui/Input";
import { cn } from "@/lib/utils/cn";

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  invalid?: boolean;
}

/** A multi-line field with the Input primitive's border, focus and error treatment. */
export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(
  ({ className, invalid, "aria-invalid": ariaInvalid, ...props }, ref) => (
    <textarea
      ref={ref}
      aria-invalid={ariaInvalid ?? invalid ?? undefined}
      className={cn(inputVariants({ invalid: !!invalid }), "h-auto min-h-24 py-2.5", className)}
      {...props}
    />
  )
);
Textarea.displayName = "Textarea";
