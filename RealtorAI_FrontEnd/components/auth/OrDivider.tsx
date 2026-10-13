/** Separates the email/password form from the OAuth alternative on both auth screens. */
export function OrDivider({ label }: { label: string }) {
  return (
    <div className="flex items-center gap-3" aria-hidden="true">
      <span className="h-px flex-1 bg-[var(--color-border)]" />
      <span className="text-xs font-medium uppercase tracking-wider text-[var(--color-text-muted)]">
        {label}
      </span>
      <span className="h-px flex-1 bg-[var(--color-border)]" />
    </div>
  );
}
