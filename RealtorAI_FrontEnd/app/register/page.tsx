import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { AuthShell } from "@/components/auth/AuthShell";
import { RegisterForm } from "@/components/auth/RegisterForm";

export default async function RegisterPage() {
  const [locale, themePreference] = await Promise.all([
    getLocale(),
    getThemePreference(),
  ]);
  const dict = await getDictionary(locale);
  const t = dict.auth.register;

  return (
    <AuthShell
      dict={dict}
      themePreference={themePreference}
      title={t.title}
      subtitle={t.subtitle}
    >
      <RegisterForm dict={dict} />
    </AuthShell>
  );
}
