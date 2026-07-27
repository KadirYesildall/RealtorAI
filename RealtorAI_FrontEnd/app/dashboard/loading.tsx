import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";

export default async function DashboardLoading() {
  // Renders before the page's own data fetch resolves, so the saved profile preference
  // isn't available yet — falls back to the same cookie-based locale pre-auth pages use.
  const locale = await getLocale();
  const dict = await getDictionary(locale);

  return (
    <div className="flex min-h-[50vh] items-center justify-center">
      <div
        role="status"
        aria-label={dict.common.loading}
        className="h-8 w-8 animate-spin rounded-full border-2 border-[var(--color-border)] border-t-[var(--color-accent)]"
      />
    </div>
  );
}
