import { type ReactNode } from "react";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { Sidebar } from "@/components/dashboard/Sidebar";
import { LanguageSwitcher } from "@/components/dashboard/LanguageSwitcher";

export default async function DashboardLayout({ children }: { children: ReactNode }) {
  // Auth guard: redirects to /login if unauthenticated (spec US3 AC1).
  const profile = await getAuthenticatedProfile();

  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);

  return (
    <div className="flex min-h-screen bg-[var(--color-surface-muted)]">
      <Sidebar dict={dict} />
      <div className="flex-1">
        <header className="flex justify-end border-b border-[var(--color-border)] bg-[var(--color-surface)] px-6 py-3">
          <LanguageSwitcher dict={dict} currentLocale={locale} />
        </header>
        <main className="p-6">{children}</main>
      </div>
    </div>
  );
}
