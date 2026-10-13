import { type ReactNode } from "react";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { Sidebar } from "@/components/dashboard/Sidebar";
import { LanguageSwitcher } from "@/components/dashboard/LanguageSwitcher";
import { ThemeToggle } from "@/components/ui/ThemeToggle";

export default async function DashboardLayout({ children }: { children: ReactNode }) {
  // Auth guard: redirects to /login if unauthenticated (spec US3 AC1).
  const [profile, themePreference] = await Promise.all([
    getAuthenticatedProfile(),
    getThemePreference(),
  ]);

  const locale = isLocale(profile.preferredLanguage)
    ? profile.preferredLanguage
    : defaultLocale;
  const dict = await getDictionary(locale);

  return (
    <div className="flex min-h-screen bg-[var(--color-surface-muted)]">
      <Sidebar dict={dict} />
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-end gap-4 border-b border-[var(--color-border)] bg-[var(--color-surface)] px-6 py-3">
          <LanguageSwitcher dict={dict} currentLocale={locale} />
          <ThemeToggle labels={dict.theme} initial={themePreference} />
        </header>
        <main className="flex-1 p-6 lg:p-8">{children}</main>
      </div>
    </div>
  );
}
