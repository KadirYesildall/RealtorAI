import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { Card } from "@/components/ui/Card";
import { RegisterForm } from "@/components/auth/RegisterForm";

export default async function RegisterPage() {
  const locale = await getLocale();
  const dict = await getDictionary(locale);
  const t = dict.auth.register;

  return (
    <main className="flex min-h-screen items-center justify-center bg-[var(--color-surface-muted)] px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-6 text-center">
          <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.title}</h1>
          <p className="mt-2 text-sm text-[var(--color-text-muted)]">{t.subtitle}</p>
        </div>
        <Card>
          <RegisterForm dict={dict} />
        </Card>
      </div>
    </main>
  );
}
