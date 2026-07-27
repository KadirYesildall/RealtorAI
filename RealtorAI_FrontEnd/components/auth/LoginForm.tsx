"use client";

import { useActionState, useState, useTransition } from "react";
import {
  signInAction,
  signInWithGoogleAction,
  resendConfirmationAction,
  type ActionError,
} from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { ConsentCheckbox } from "@/components/auth/ConsentCheckbox";

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
  const showResend = state?.fieldErrors?._resend === "true";

  function handleResend() {
    setResendState("idle");
    startResend(async () => {
      const result = await resendConfirmationAction(email);
      setResendState(result.success ? "sent" : "error");
    });
  }

  return (
    <div className="space-y-6">
      <form action={formAction} className="space-y-4" noValidate>
        <div>
          <label htmlFor="email" className="mb-1 block text-sm font-medium">
            {t.emailLabel}
          </label>
          <Input
            id="email"
            name="email"
            type="email"
            autoComplete="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            invalid={!!state?.fieldErrors?.email}
            required
          />
          {state?.fieldErrors?.email && (
            <p className="mt-1 text-sm text-[var(--color-danger)]">
              {state.fieldErrors.email}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="password" className="mb-1 block text-sm font-medium">
            {t.passwordLabel}
          </label>
          <Input
            id="password"
            name="password"
            type="password"
            autoComplete="current-password"
            invalid={!!state?.fieldErrors?.password}
            required
          />
          {state?.fieldErrors?.password && (
            <p className="mt-1 text-sm text-[var(--color-danger)]">
              {state.fieldErrors.password}
            </p>
          )}
        </div>

        {state?.error && (
          <div className="text-sm text-[var(--color-danger)]">
            <p>{state.error}</p>
            {showResend && (
              <button
                type="button"
                onClick={handleResend}
                disabled={resendPending || resendState === "sent"}
                className="mt-1 text-[var(--color-accent)] hover:underline disabled:no-underline disabled:opacity-60"
              >
                {resendState === "sent" ? t.resendConfirmation + " ✓" : t.resendConfirmation}
              </button>
            )}
          </div>
        )}

        <Button type="submit" className="w-full" disabled={pending}>
          {pending ? t.submitting : t.submit}
        </Button>
      </form>

      {/*
        Consent (FR-016) only applies to *creating* an account — signing in with an
        existing email/password never creates one, so that button above is never gated.
        "Continue with Google" is different: since Supabase can't tell us in advance
        whether this will sign in an existing account or create a new one, it stays
        gated here too, same as on the register page (US2 AC4).
      */}
      <div className="space-y-3">
        <ConsentCheckbox
          label={
            dict.auth.register.consentLabel /* shared consent copy across login/register */
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
            className="w-full"
            disabled={!accepted || googlePending}
          >
            {t.continueWithGoogle}
          </Button>
        </form>
      </div>

      <p className="text-center text-sm text-[var(--color-text-muted)]">
        {t.noAccount}{" "}
        <a href="/register" className="text-[var(--color-accent)] hover:underline">
          {t.signUpLink}
        </a>
      </p>
    </div>
  );
}
