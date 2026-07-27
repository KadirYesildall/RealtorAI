import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { Card } from "@/components/ui/Card";
import { LoginForm } from "@/components/auth/LoginForm";

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  const [locale, params] = await Promise.all([getLocale(), searchParams]);
  const dict = await getDictionary(locale);
  const t = dict.auth.login;

  const oauthMessage =
    params.error === "oauth_cancelled"
      ? t.oauthCancelled
      : params.error === "oauth_failed"
        ? t.oauthFailed
        : null;

  return (
    <main className="flex min-h-screen items-center justify-center bg-[var(--color-surface-muted)] px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 text-center">
          <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.title}</h1>
          <p className="mt-2 text-sm text-[var(--color-text-muted)]">{t.subtitle}</p>
        </div>
        {oauthMessage && (
          <p className="mb-4 rounded-[var(--radius-sm)] border border-[var(--color-border)] bg-[var(--color-surface)] p-3 text-center text-sm text-[var(--color-danger)]">
            {oauthMessage}
          </p>
        )}
        <Card>
          <LoginForm dict={dict} />
        </Card>
      </div>
    </main>
  );
}
