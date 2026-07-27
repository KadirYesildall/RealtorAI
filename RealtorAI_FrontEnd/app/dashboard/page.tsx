import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { UsageSummaryCard } from "@/components/dashboard/UsageSummaryCard";

export default async function DashboardPage() {
  const profile = await getAuthenticatedProfile();
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);

  const displayName = profile.fullName ?? profile.companyName ?? "";
  const welcome = dict.dashboard.welcome.replace("{name}", displayName);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-[var(--color-text)]">{welcome}</h1>
        {profile.companyName && profile.fullName && (
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">
            {profile.companyName}
          </p>
        )}
      </div>
      <UsageSummaryCard dict={dict} usageCount={profile.apiUsageCount} />
    </div>
  );
}
