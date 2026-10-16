"use client";

import { useActionState, useState } from "react";
import {
  completeProfileAction,
  type ProfileCompletionField,
  type ProfileCompletionState,
} from "@/app/actions/profile";
import type { MembershipStatus } from "@/lib/api/brokerage";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";

const INITIAL: ProfileCompletionState = { status: "idle" };

/**
 * T079 — the profile completion form. It submits with a plain `<form action>`, so it works
 * before hydration; the action and then the API validate everything again.
 *
 * The inputs are controlled because React resets uncontrolled fields when a form action
 * completes, which would wipe what the agent typed whenever validation sends it back.
 */
export function ProfileCompletionForm({
  next,
  dict,
  statusLabels,
  defaults,
  currentMembership,
}: {
  next: string;
  dict: Dictionary["profileCompletion"];
  statusLabels: Dictionary["brokerage"]["status"];
  defaults: { licenseNumber: string; licenseState: string };
  currentMembership: { brokerageName: string; status: MembershipStatus } | null;
}) {
  const [state, formAction, pending] = useActionState(completeProfileAction.bind(null, next), INITIAL);
  const errors = dict.errors as Record<string, string>;
  const [values, setValues] = useState({
    licenseNumber: defaults.licenseNumber,
    licenseState: defaults.licenseState,
    brokerageName: "",
    brokerageId: "",
    brokerageLicenseNumber: "",
    brokerageState: "",
  });

  const bind = (field: keyof typeof values) => ({
    name: field,
    value: values[field],
    onChange: (event: { currentTarget: { value: string } }) => {
      const value = event.currentTarget.value;
      setValues((current) => ({ ...current, [field]: value }));
    },
  });

  const fieldError = (field: ProfileCompletionField): string | undefined => {
    if (state.status !== "error") {
      return undefined;
    }

    const key = state.fieldErrors?.[field];

    return key ? (errors[key] ?? errors.serverRejected) : undefined;
  };

  return (
    <form action={formAction} className="space-y-8" noValidate>
      {state.status === "error" && <Alert>{errors[state.code] ?? errors.unexpected}</Alert>}

      <fieldset className="space-y-5">
        <legend className="text-lg font-semibold text-[var(--color-text)]">{dict.license.title}</legend>
        <div className="grid gap-5 sm:grid-cols-2">
          <TextField
            id="profile-license-number"
            {...bind("licenseNumber")}
            label={dict.license.number}
            error={fieldError("licenseNumber")}
            required
          />
          <TextField
            id="profile-license-state"
            {...bind("licenseState")}
            label={dict.license.state}
            hint={dict.license.stateHint}
            error={fieldError("licenseState")}
            maxLength={2}
            required
            autoComplete="address-level1"
          />
        </div>
      </fieldset>

      <fieldset className="space-y-5">
        <legend className="text-lg font-semibold text-[var(--color-text)]">{dict.brokerage.title}</legend>

        {currentMembership ? (
          <>
            <input type="hidden" name="declaresBrokerage" value="false" />
            <p className="text-sm text-[var(--color-text)]">
              {format(dict.brokerage.current, {
                brokerage: currentMembership.brokerageName,
                status: statusLabels[currentMembership.status],
              })}
            </p>
          </>
        ) : (
          <>
            <input type="hidden" name="declaresBrokerage" value="true" />
            <p className="text-sm text-[var(--color-text-muted)]">{dict.brokerage.body}</p>
            <TextField
              id="profile-brokerage-name"
              {...bind("brokerageName")}
              label={dict.brokerage.name}
              error={fieldError("brokerageName")}
              required
              autoComplete="organization"
            />
            <TextField
              id="profile-brokerage-id"
              {...bind("brokerageId")}
              label={dict.brokerage.id}
              hint={dict.brokerage.idHint}
              error={fieldError("brokerageId")}
              spellCheck={false}
            />
            <div className="grid gap-5 sm:grid-cols-2">
              <TextField
                id="profile-brokerage-license"
                {...bind("brokerageLicenseNumber")}
                label={dict.brokerage.licenseNumber}
                error={fieldError("brokerageLicenseNumber")}
              />
              <TextField
                id="profile-brokerage-state"
                {...bind("brokerageState")}
                label={dict.brokerage.state}
                error={fieldError("brokerageState")}
                maxLength={2}
              />
            </div>
          </>
        )}
      </fieldset>

      <Button type="submit" loading={pending}>
        {dict.submit}
      </Button>
    </form>
  );
}
