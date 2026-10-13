"use client";

import { useActionState, useState } from "react";
import {
  signUpAction,
  signInWithGoogleAction,
  type ActionError,
} from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { PasswordField } from "@/components/ui/PasswordField";
import { CheckIcon, GoogleIcon, MailIcon, UserIcon } from "@/components/ui/icons";
import { ConsentCheckbox } from "@/components/auth/ConsentCheckbox";
import { OrDivider } from "@/components/auth/OrDivider";

/** Mirrors SignUpSchema's minimum in lib/validation/auth.ts — the server stays the authority. */
const MIN_PASSWORD_LENGTH = 8;

export function RegisterForm({ dict }: { dict: Dictionary }) {
  const [state, formAction, pending] = useActionState<ActionError | null, FormData>(
    signUpAction,
    null
  );
  const [accepted, setAccepted] = useState(false);
  const [googlePending, setGooglePending] = useState(false);
  const [passwordLength, setPasswordLength] = useState(0);

  const t = dict.auth.register;
  const common = dict.auth.common;
  const lengthMet = passwordLength >= MIN_PASSWORD_LENGTH;

  return (
    <div className="space-y-5">
      <form action={formAction} className="space-y-4" noValidate>
        <TextField
          id="fullName"
          name="fullName"
          label={t.fullNameLabel}
          icon={<UserIcon />}
          autoComplete="name"
          error={state?.fieldErrors?.fullName}
          required
        />

        <TextField
          id="email"
          name="email"
          type="email"
          label={t.emailLabel}
          icon={<MailIcon />}
          autoComplete="email"
          error={state?.fieldErrors?.email}
          required
        />

        <PasswordField
          id="password"
          name="password"
          label={t.passwordLabel}
          autoComplete="new-password"
          showLabel={common.showPassword}
          hideLabel={common.hidePassword}
          error={state?.fieldErrors?.password}
          onChange={(event) => setPasswordLength(event.target.value.length)}
          hint={
            // Live feedback on the one rule this form enforces, so the requirement is
            // answered while typing rather than after a failed submit.
            <span
              className={
                lengthMet
                  ? "flex items-center gap-1.5 text-[var(--color-accent)]"
                  : "flex items-center gap-1.5"
              }
            >
              <CheckIcon
                className={lengthMet ? "h-3.5 w-3.5" : "h-3.5 w-3.5 opacity-30"}
              />
              {t.passwordHint}
            </span>
          }
          required
        />

        <ConsentCheckbox
          id="register-acceptedTerms"
          label={t.consentLabel}
          checked={accepted}
          onCheckedChange={setAccepted}
          error={state?.fieldErrors?.acceptedTerms}
        />

        {state?.error && !state.fieldErrors && <Alert>{state.error}</Alert>}

        <Button
          type="submit"
          size="lg"
          className="w-full"
          disabled={!accepted}
          loading={pending}
        >
          {pending ? t.submitting : t.submit}
        </Button>
      </form>

      <OrDivider label={common.or} />

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

      <p className="text-center text-sm text-[var(--color-text-muted)]">
        {t.haveAccount}{" "}
        <a
          href="/login"
          className="font-medium text-[var(--color-accent)] underline-offset-2 hover:underline"
        >
          {t.signInLink}
        </a>
      </p>
    </div>
  );
}
