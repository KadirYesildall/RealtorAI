"use client";

import { useActionState, useState, useTransition } from "react";
import {
  signInAction,
  signInWithGoogleAction,
  resendConfirmationAction,
  type ActionError,
} from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { PasswordField } from "@/components/ui/PasswordField";
import { GoogleIcon, MailIcon } from "@/components/ui/icons";
import { ConsentCheckbox } from "@/components/auth/ConsentCheckbox";
import { OrDivider } from "@/components/auth/OrDivider";

export function LoginForm({ dict }: { dict: Dictionary }) {
  const [state, formAction, pending] = useActionState<ActionError | null, FormData>(
    signInAction,
    null
  );
  const [accepted, setAccepted] = useState(false);
  const [googlePending, setGooglePending] = useState(false);
  const [email, setEmail] = useState("");
  const [resendState, setResendState] = useState<"idle" | "sent" | "error">("idle");
  const [resendPending, startResend] = useTransition();

  const t = dict.auth.login;
  const common = dict.auth.common;
  const showResend = state?.fieldErrors?._resend === "true";

  function handleResend() {
    setResendState("idle");
    startResend(async () => {
      const result = await resendConfirmationAction(email);
      setResendState(result.success ? "sent" : "error");
    });
  }

  return (
    <div className="space-y-5">
      <form action={formAction} className="space-y-4" noValidate>
        <TextField
          id="email"
          name="email"
          type="email"
          label={t.emailLabel}
          icon={<MailIcon />}
          autoComplete="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          error={state?.fieldErrors?.email}
          required
        />

        <PasswordField
          id="password"
          name="password"
          label={t.passwordLabel}
          autoComplete="current-password"
          showLabel={common.showPassword}
          hideLabel={common.hidePassword}
          error={state?.fieldErrors?.password}
          required
        />

        {state?.error && (
          <Alert>
            <p>{state.error}</p>
            {showResend && (
              <button
                type="button"
                onClick={handleResend}
                disabled={resendPending || resendState === "sent"}
                className="mt-1 font-medium underline underline-offset-2 hover:no-underline disabled:no-underline disabled:opacity-70"
              >
                {resendState === "sent" ? t.resendConfirmationSent : t.resendConfirmation}
              </button>
            )}
          </Alert>
        )}

        <Button type="submit" size="lg" className="w-full" loading={pending}>
          {pending ? t.submitting : t.submit}
        </Button>
      </form>

      <OrDivider label={common.or} />

      {/*
        Consent (FR-016) only applies to *creating* an account — signing in with an
        existing email/password never creates one, so that button above is never gated.
        "Continue with Google" is different: since Supabase can't tell us in advance
        whether this will sign in an existing account or create a new one, it stays
        gated here too, same as on the register page (US2 AC4).
      */}
      <div className="space-y-3">
        <ConsentCheckbox
          id="login-acceptedTerms"
          label={
            dict.auth.register
              .consentLabel /* shared consent copy across login/register */
          }
          checked={accepted}
          onCheckedChange={setAccepted}
        />
        <form
          action={async () => {
            setGooglePending(true);
            await signInWithGoogleAction();
            setGooglePending(false);
          }}
        >
          <Button
            type="submit"
            variant="secondary"
            size="lg"
            className="w-full"
            disabled={!accepted}
            loading={googlePending}
          >
            {!googlePending && <GoogleIcon className="h-[18px] w-[18px]" />}
            {t.continueWithGoogle}
          </Button>
        </form>
      </div>

      <p className="text-center text-sm text-[var(--color-text-muted)]">
        {t.noAccount}{" "}
        <a
          href="/register"
          className="font-medium text-[var(--color-accent)] underline-offset-2 hover:underline"
        >
          {t.signUpLink}
        </a>
      </p>
    </div>
  );
}
