import { cn } from "@/lib/utils/cn";

/**
 * Brand mark + wordmark. `tone="onBrand"` is for placement on the dark teal auth panel,
 * where the surrounding surface is dark in *both* themes and the normal text tokens would
 * disappear into it.
 */
export function Logo({
  tone = "default",
  className,
}: {
  tone?: "default" | "onBrand";
  className?: string;
}) {
  const onBrand = tone === "onBrand";

  return (
    <span className={cn("inline-flex items-center gap-2.5", className)}>
      <span
        className={cn(
          "flex h-8 w-8 items-center justify-center rounded-[10px]",
          onBrand
            ? "bg-[var(--color-brand-text)] text-[var(--light-brand-from)]"
            : "bg-[var(--color-accent)] text-[var(--color-accent-foreground)]"
        )}
      >
        {/* Abstract roofline over a window — real-estate shorthand that stays legible at 16px. */}
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
          className="h-[18px] w-[18px]"
        >
          <path d="M3.5 10.5 12 4l8.5 6.5" />
          <path d="M6 12v7.5h12V12" />
          <path d="M10.5 19.5v-4h3v4" />
        </svg>
      </span>
      <span
        className={cn(
          "text-[0.975rem] font-semibold tracking-tight",
          onBrand ? "text-[var(--color-brand-text)]" : "text-[var(--color-text)]"
        )}
      >
        Realtor
        <span className={onBrand ? "opacity-70" : "text-[var(--color-accent)]"}>AI</span>
      </span>
    </span>
  );
}
