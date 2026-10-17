import { type ReactNode } from "react";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { Sidebar } from "@/components/dashboard/Sidebar";
import { LanguageSwitcher } from "@/components/dashboard/LanguageSwitcher";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { PendingMembershipBanner } from "@/components/brokerage/PendingMembershipBanner";
import { getOwnMembershipForChrome } from "@/lib/api/brokerage";

export default async function DashboardLayout({ children }: { children: ReactNode }) {
  // Auth guard: redirects to /login if unauthenticated (spec US3 AC1).
  const [profile, themePreference] = await Promise.all([
    getAuthenticatedProfile(),
    getThemePreference(),
  ]);

  // After the auth guard, not alongside it: run in parallel, an anonymous visitor's request
  // reached the API with no token (a pointless 401) before the guard redirected. Never throws —
  // the banner is not worth taking the dashboard down when the API is.
  const membership = await getOwnMembershipForChrome();

  const locale = isLocale(profile.preferredLanguage)
    ? profile.preferredLanguage
    : defaultLocale;
  const dict = await getDictionary(locale);

  return (
    <div className="flex min-h-screen bg-[var(--color-surface-muted)]">
      <Sidebar dict={dict} />
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-end gap-4 border-b border-[var(--color-border)] bg-[var(--color-surface)] px-4 py-3 md:px-6">
          <LanguageSwitcher dict={dict} currentLocale={locale} />
          <ThemeToggle labels={dict.theme} initial={themePreference} />
        </header>
        <main className="min-w-0 flex-1 p-4 md:p-6 lg:p-8">
          {membership?.status === "pending" && (
            <PendingMembershipBanner
              dict={dict.brokerage.banner}
              brokerageName={membership.brokerageName ?? ""}
            />
          )}
          {children}
        </main>
      </div>
    </div>
  );
}
