import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { Card } from "@/components/ui/Card";

export default async function CheckEmailPage() {
  const locale = await getLocale();
  const dict = await getDictionary(locale);
  const t = dict.auth.register;

  return (
    <main className="flex min-h-screen items-center justify-center bg-[var(--color-surface-muted)] px-4 py-12">
      <div className="w-full max-w-md text-center">
        <Card>
          <h1 className="text-xl font-semibold text-[var(--color-text)]">
            {t.checkEmailTitle}
          </h1>
          <p className="mt-3 text-sm text-[var(--color-text-muted)]">{t.checkEmailBody}</p>
          <a
            href="/login"
            className="mt-6 inline-block text-sm text-[var(--color-accent)] hover:underline"
          >
            {dict.auth.login.title}
          </a>
        </Card>
      </div>
    </main>
  );
}
