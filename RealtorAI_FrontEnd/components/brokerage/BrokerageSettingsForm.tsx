"use client";

import { useActionState } from "react";
import {
  updateBrokerageSettingsAction,
  type SettingsActionState,
  type SettingsField,
} from "@/app/actions/brokerage";
import type { BrokerageSettings } from "@/lib/api/brokerage";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { TONES } from "@/lib/validation/brokerage";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { Select } from "@/components/ui/Select";
import { TextField } from "@/components/ui/TextField";
import { Textarea } from "@/components/ui/Textarea";

const INITIAL: SettingsActionState = { status: "idle" };

/**
 * T063 — the broker admin's settings form (FR-057).
 *
 * A Client Component only because it shows the action's result in place (`useActionState`).
 * It submits with a plain `<form action>`, so it works before hydration as well. Every value is
 * checked twice — by the Zod schema in the action, and again by the API, which also decides
 * whether the caller may change these settings at all.
 */
export function BrokerageSettingsForm({
  brokerageId,
  settings,
  dict,
}: {
  brokerageId: string;
  settings: BrokerageSettings;
  dict: Dictionary["brokerage"];
}) {
  const [state, formAction, pending] = useActionState(
    updateBrokerageSettingsAction.bind(null, brokerageId),
    INITIAL
  );

  const t = dict.settings;
  const errors = dict.errors as Record<string, string>;

  const fieldError = (field: SettingsField): string | undefined => {
    if (state.status !== "error") {
      return undefined;
    }

    const key = state.fieldErrors?.[field];

    return key ? (errors[key] ?? errors.serverRejected) : undefined;
  };

  return (
    <form action={formAction} className="space-y-5" noValidate>
      {state.status === "error" && <Alert>{errors[state.code] ?? errors.unexpected}</Alert>}
      {state.status === "saved" && (
        <Alert tone="accent" role="status">
          {t.saved}
        </Alert>
      )}

      <div className="grid gap-5 sm:grid-cols-2">
        <TextField
          id="brokerage-name"
          name="name"
          label={t.name}
          defaultValue={settings.name ?? ""}
          error={fieldError("name")}
          required
          autoComplete="organization"
        />
        <TextField
          id="brokerage-license"
          name="licenseNumber"
          label={t.licenseNumber}
          defaultValue={settings.licenseNumber ?? ""}
          error={fieldError("licenseNumber")}
          required
        />
        <TextField
          id="brokerage-state"
          name="state"
          label={t.state}
          hint={t.stateHint}
          defaultValue={settings.state ?? ""}
          error={fieldError("state")}
          maxLength={2}
          required
          autoComplete="address-level1"
        />
        <TextField
          id="brokerage-mls"
          name="targetMlsId"
          label={t.targetMlsId}
          hint={t.targetMlsIdHint}
          defaultValue={settings.targetMlsId ?? ""}
          error={fieldError("targetMlsId")}
        />
      </div>

      <FormField
        id="brokerage-disclaimers"
        label={t.requiredDisclaimers}
        hint={t.requiredDisclaimersHint}
        error={fieldError("requiredDisclaimers")}
      >
        {(describedBy) => (
          <Textarea
            id="brokerage-disclaimers"
            name="requiredDisclaimers"
            rows={4}
            defaultValue={(settings.requiredDisclaimers ?? []).join("\n")}
            invalid={!!fieldError("requiredDisclaimers")}
            aria-describedby={describedBy}
          />
        )}
      </FormField>

      <div className="grid gap-5 sm:grid-cols-3">
        <FormField id="brokerage-tone" label={t.defaultTone} error={fieldError("defaultTone")}>
          {(describedBy) => (
            <Select
              id="brokerage-tone"
              name="defaultTone"
              defaultValue={settings.defaultTone ?? "professional"}
              invalid={!!fieldError("defaultTone")}
              aria-describedby={describedBy}
            >
              {TONES.map((tone) => (
                <option key={tone} value={tone}>
                  {t.tones[tone]}
                </option>
              ))}
            </Select>
          )}
        </FormField>
        <TextField
          id="brokerage-budget-tokens"
          name="aiBudgetTokens"
          type="number"
          inputMode="numeric"
          min={1}
          step={1}
          label={t.aiBudgetTokens}
          defaultValue={settings.aiBudgetTokens}
          error={fieldError("aiBudgetTokens")}
        />
        <TextField
          id="brokerage-budget-images"
          name="aiBudgetImages"
          type="number"
          inputMode="numeric"
          min={1}
          step={1}
          label={t.aiBudgetImages}
          defaultValue={settings.aiBudgetImages}
          error={fieldError("aiBudgetImages")}
        />
      </div>
      <p className="text-sm text-[var(--color-text-muted)]">{t.budgetHint}</p>

      <Button type="submit" loading={pending}>
        {t.save}
      </Button>
    </form>
  );
}
