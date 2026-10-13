import { type ReactNode } from "react";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import type { ThemePreference } from "@/lib/theme/theme";
import { Card } from "@/components/ui/Card";
import { Logo } from "@/components/ui/Logo";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { FilmIcon, MegaphoneIcon, SparkIcon } from "@/components/ui/icons";

const BENEFIT_ICONS = [SparkIcon, FilmIcon, MegaphoneIcon];

/**
 * Shared chrome for /login, /register and /register/check-email: a brand column on the
 * left and the form on the right.
 *
 * The brand column is `hidden lg:flex` rather than reflowed — on a phone it would push the
 * form below the fold, and the form is the only reason anyone is on these pages. Its
 * content is marketing copy, so it is intentionally NOT marked up as headings: the page's
 * own <h1> (passed in as `title`) stays the single top-level heading on every screen.
 */
export function AuthShell({
  dict,
  themePreference,
  title,
  subtitle,
  children,
  footer,
}: {
  dict: Dictionary;
  themePreference: ThemePreference;
  title: string;
  subtitle?: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  const brand = dict.auth.brand;

  return (
    <div className="grid min-h-screen bg-[var(--color-surface-muted)] lg:grid-cols-[1.05fr_1fr]">
      <aside className="relative hidden overflow-hidden bg-[linear-gradient(155deg,var(--color-brand-from)_0%,var(--color-brand-to)_100%)] p-12 lg:flex lg:flex-col lg:justify-between">
        {/* Decorative only: a soft off-canvas light source plus a faint architectural grid.
            Both are fixed white alphas — they sit on the brand gradient, which is dark in
            either theme, so they are not theme tokens. */}
        <div
          aria-hidden="true"
          className="pointer-events-none absolute -left-32 -top-32 h-[28rem] w-[28rem] rounded-full bg-white/10 blur-3xl"
        />
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 opacity-[0.06]"
          style={{
            backgroundImage:
              "linear-gradient(to right, #fff 1px, transparent 1px), linear-gradient(to bottom, #fff 1px, transparent 1px)",
            backgroundSize: "44px 44px",
          }}
        />

        <div className="relative">
          <Logo tone="onBrand" />
        </div>

        <div className="relative max-w-md">
          <p className="text-xs font-medium uppercase tracking-[0.14em] text-[var(--color-brand-text-muted)]">
            {brand.tagline}
          </p>
          <p className="mt-4 text-[2rem] font-semibold leading-[1.15] tracking-tight text-[var(--color-brand-text)]">
            {brand.headline}
          </p>

          <ul className="mt-10 space-y-6">
            {brand.benefits.map((benefit, index) => {
              const BenefitIcon = BENEFIT_ICONS[index] ?? SparkIcon;
              return (
                <li key={benefit.title} className="flex gap-4">
                  <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-white/10 text-[var(--color-brand-text)] ring-1 ring-inset ring-white/15">
                    <BenefitIcon className="h-[18px] w-[18px]" />
                  </span>
                  <div>
                    <p className="text-sm font-medium text-[var(--color-brand-text)]">
                      {benefit.title}
                    </p>
                    <p className="mt-1 text-sm leading-relaxed text-[var(--color-brand-text-muted)]">
                      {benefit.body}
                    </p>
                  </div>
                </li>
              );
            })}
          </ul>
        </div>

        <p className="relative max-w-sm text-sm text-[var(--color-brand-text-muted)]">
          {brand.footnote}
        </p>
      </aside>

      <main className="relative flex flex-col px-4 py-6 sm:px-8 sm:py-8">
        <div className="flex items-center justify-between gap-4">
          <Logo className="lg:invisible" />
          <ThemeToggle labels={dict.theme} initial={themePreference} />
        </div>

        <div className="flex flex-1 items-center justify-center py-10">
          <div className="w-full max-w-[25rem]">
            <div className="mb-6">
              <h1 className="text-[1.75rem] font-semibold leading-tight tracking-tight text-[var(--color-text)]">
                {title}
              </h1>
              {subtitle && (
                <p className="mt-2 text-sm leading-relaxed text-[var(--color-text-muted)]">
                  {subtitle}
                </p>
              )}
            </div>

            {footer}

            <Card padding="lg" elevation="elevated">
              {children}
            </Card>
          </div>
        </div>
      </main>
    </div>
  );
}
