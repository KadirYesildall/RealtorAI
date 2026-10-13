import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { Alert } from "@/components/ui/Alert";
import { AuthShell } from "@/components/auth/AuthShell";
import { LoginForm } from "@/components/auth/LoginForm";

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ error?: string }>;
}) {
  const [locale, params, themePreference] = await Promise.all([
    getLocale(),
    searchParams,
    getThemePreference(),
  ]);
  const dict = await getDictionary(locale);
  const t = dict.auth.login;

  const oauthMessage =
    params.error === "oauth_cancelled"
      ? t.oauthCancelled
      : params.error === "oauth_failed"
        ? t.oauthFailed
        : null;

  return (
    <AuthShell
      dict={dict}
      themePreference={themePreference}
      title={t.title}
      subtitle={t.subtitle}
      footer={
        oauthMessage && (
          // Present on first paint rather than announced after an interaction, so this is
          // role="status" — see components/ui/Alert.tsx.
          <Alert role="status" className="mb-4">
            {oauthMessage}
          </Alert>
        )
      }
    >
      <LoginForm dict={dict} />
    </AuthShell>
  );
}
