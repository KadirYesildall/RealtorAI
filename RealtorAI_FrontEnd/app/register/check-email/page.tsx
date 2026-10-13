import { getLocale } from "@/lib/i18n/getLocale";
import { getDictionary } from "@/lib/i18n/getDictionary";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { cn } from "@/lib/utils/cn";
import { AuthShell } from "@/components/auth/AuthShell";
import { buttonVariants } from "@/components/ui/Button";
import { MailIcon } from "@/components/ui/icons";

export default async function CheckEmailPage() {
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
      title={t.checkEmailTitle}
      subtitle={t.checkEmailBody}
    >
      <div className="flex flex-col items-center gap-6 text-center">
        <span className="flex h-14 w-14 items-center justify-center rounded-full bg-[var(--color-accent-soft)] text-[var(--color-accent)]">
          <MailIcon className="h-6 w-6" />
        </span>
        {/* Styled as a button but kept an <a> — a <button> inside an <a> is a nested
            interactive element and is announced twice by screen readers. */}
        <a
          href="/login"
          className={cn(buttonVariants({ variant: "secondary", size: "lg" }), "w-full")}
        >
          {dict.auth.login.title}
        </a>
      </div>
    </AuthShell>
  );
}
