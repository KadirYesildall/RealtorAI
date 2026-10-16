import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";

export default async function SectionLoading() {
  // Same cookie-based locale as the dashboard's loading state: the profile is not loaded yet.
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
