"use client";

import { useActionState, useState } from "react";
import { signUpAction, signInWithGoogleAction, type ActionError } from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { ConsentCheckbox } from "@/components/auth/ConsentCheckbox";

export function RegisterForm({ dict }: { dict: Dictionary }) {
  const [state, formAction, pending] = useActionState<ActionError | null, FormData>(
    signUpAction,
    null
  );
  const [accepted, setAccepted] = useState(false);
  const [googlePending, setGooglePending] = useState(false);

  const t = dict.auth.register;

  return (
    <div className="space-y-6">
      <form action={formAction} className="space-y-4" noValidate>
        <div>
          <label htmlFor="fullName" className="mb-1 block text-sm font-medium">
            {t.fullNameLabel}
          </label>
          <Input
            id="fullName"
            name="fullName"
            autoComplete="name"
            invalid={!!state?.fieldErrors?.fullName}
            required
          />
          {state?.fieldErrors?.fullName && (
            <p className="mt-1 text-sm text-[var(--color-danger)]">
              {state.fieldErrors.fullName}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="email" className="mb-1 block text-sm font-medium">
            {t.emailLabel}
          </label>
          <Input
            id="email"
            name="email"
            type="email"
            autoComplete="email"
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
            autoComplete="new-password"
            invalid={!!state?.fieldErrors?.password}
            required
          />
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.passwordHint}</p>
          {state?.fieldErrors?.password && (
            <p className="mt-1 text-sm text-[var(--color-danger)]">
              {state.fieldErrors.password}
            </p>
          )}
        </div>

        <ConsentCheckbox
          label={t.consentLabel}
          checked={accepted}
          onCheckedChange={setAccepted}
          error={state?.fieldErrors?.acceptedTerms}
        />

        {state?.error && !state.fieldErrors && (
          <p className="text-sm text-[var(--color-danger)]">{state.error}</p>
        )}

        <Button type="submit" className="w-full" disabled={pending || !accepted}>
          {pending ? t.submitting : t.submit}
        </Button>
      </form>

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

      <p className="text-center text-sm text-[var(--color-text-muted)]">
        {t.haveAccount}{" "}
        <a href="/login" className="text-[var(--color-accent)] hover:underline">
          {t.signInLink}
        </a>
      </p>
    </div>
  );
}
