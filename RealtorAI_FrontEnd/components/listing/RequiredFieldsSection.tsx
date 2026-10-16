"use client";

import { useRef, useState } from "react";
import { saveListingFieldAction } from "@/app/actions/listing";
import type { ListingField } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { PUBLIC_RECORD_FIELDS, type PublicRecordField } from "@/lib/validation/listing";
import { Card } from "@/components/ui/Card";
import { TextField } from "@/components/ui/TextField";
import type { SaveActivity } from "@/components/listing/ListingDraftEditor";

const AUTOSAVE_DELAY_MS = 800;

type FieldState = {
  value: string;
  /** 0 until the field exists — the API's "create" precondition (research R33). */
  rowVersion: number;
  provenance: ListingField["provenance"] | null;
  sourceNote: string | null;
  error?: string;
};

/**
 * FR-008's fallback, made visible: with no public-record source connected, square footage, lot
 * size, year built and tax are empty and marked required, and the agent enters them here.
 *
 * Whatever is typed here is saved as `agent_entered` — these are FR-007 fields, which only the
 * agent or a public record may supply. A value that came from a public record shows its source.
 */
export function RequiredFieldsSection({
  listingId,
  fields,
  missingKeys,
  dict,
  onActivity,
  onMissingCountChange,
}: {
  listingId: string;
  fields: ListingField[];
  missingKeys: string[];
  dict: Dictionary["listings"];
  onActivity: (activity: SaveActivity) => void;
  onMissingCountChange: (count: number) => void;
}) {
  const t = dict.required;
  const errorText = dict.errors as Record<string, string>;

  const [state, setState] = useState<Record<PublicRecordField, FieldState>>(() => {
    const initial = {} as Record<PublicRecordField, FieldState>;

    for (const key of PUBLIC_RECORD_FIELDS) {
      const field = fields.find((candidate) => candidate.resoFieldKey === key);
      initial[key] = {
        value: field?.value === undefined || field.value === null ? "" : String(field.value),
        rowVersion: field?.rowVersion ?? 0,
        provenance: field?.provenance ?? null,
        sourceNote: field?.sourceNote ?? null,
      };
    }

    return initial;
  });

  const stateRef = useRef(state);
  const savedValues = useRef<Record<string, string>>(
    Object.fromEntries(PUBLIC_RECORD_FIELDS.map((key) => [key, state[key].value]))
  );
  const timers = useRef<Partial<Record<PublicRecordField, ReturnType<typeof setTimeout>>>>({});
  const inFlight = useRef(0);

  const update = (key: PublicRecordField, patch: Partial<FieldState>) => {
    const next = { ...stateRef.current, [key]: { ...stateRef.current[key], ...patch } };
    stateRef.current = next;
    setState(next);
  };

  const missing = (current: Record<PublicRecordField, FieldState>) =>
    PUBLIC_RECORD_FIELDS.filter((key) => missingKeys.includes(key) && current[key].rowVersion === 0).length;

  const save = async (key: PublicRecordField) => {
    const timer = timers.current[key];
    if (timer) {
      clearTimeout(timer);
      delete timers.current[key];
    }

    const current = stateRef.current[key];
    const value = current.value.trim();

    // Nothing typed, or nothing changed since the last save. A field cannot be emptied once
    // entered: a required fact is corrected, not withdrawn.
    if (value.length === 0 || value === savedValues.current[key]) {
      return;
    }

    inFlight.current += 1;
    onActivity("saving");

    const result = await saveListingFieldAction(listingId, key, value, current.rowVersion);

    inFlight.current -= 1;

    if (result.status === "saved") {
      savedValues.current[key] = value;
      update(key, {
        rowVersion: result.field.rowVersion ?? current.rowVersion + 1,
        provenance: result.field.provenance ?? "agent_entered",
        sourceNote: result.field.sourceNote ?? null,
        error: undefined,
      });
      onMissingCountChange(missing(stateRef.current));

      if (inFlight.current === 0) {
        onActivity("saved");
      }

      return;
    }

    if (result.status === "conflict") {
      // Saved elsewhere meanwhile: show what is stored now, at its version.
      const latest = result.current;
      savedValues.current[key] = latest?.value === undefined || latest.value === null ? "" : String(latest.value);
      update(key, {
        value: savedValues.current[key],
        rowVersion: latest?.rowVersion ?? current.rowVersion,
        provenance: latest?.provenance ?? current.provenance,
        sourceNote: latest?.sourceNote ?? null,
        error: undefined,
      });
      onActivity("conflict");
      return;
    }

    const message =
      result.code === "fieldInvalid" ? (errorText[result.message ?? ""] ?? errorText.serverRejected) : errorText[result.code];
    update(key, { error: message ?? errorText.unexpected });
    onActivity(result.code === "fieldInvalid" ? "invalid" : "failed");
  };

  const change = (key: PublicRecordField, value: string) => {
    update(key, { value, error: undefined });

    const timer = timers.current[key];
    if (timer) {
      clearTimeout(timer);
    }

    timers.current[key] = setTimeout(() => void save(key), AUTOSAVE_DELAY_MS);
  };

  return (
    <Card id="required">
      <section aria-labelledby="required-title">
        <h2 id="required-title" className="text-lg font-semibold text-[var(--color-text)]">
          {t.title}
        </h2>
        <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>

        <div className="mt-5 grid gap-5 sm:grid-cols-2">
          {PUBLIC_RECORD_FIELDS.map((key) => {
            const field = state[key];
            const empty = field.rowVersion === 0;
            const hint = empty ? (
              <span className="inline-flex items-center rounded-full border border-[var(--color-border-strong)] px-2 py-0.5 text-xs font-medium text-[var(--color-text)]">
                {t.badge}
              </span>
            ) : field.provenance === "public_record" && field.sourceNote ? (
              format(t.publicRecord, { source: field.sourceNote })
            ) : (
              t.entered
            );

            return (
              <TextField
                key={key}
                id={`required-${key}`}
                name={key}
                label={t.fields[key]}
                hint={hint}
                error={field.error}
                inputMode={key === "YearBuilt" ? "numeric" : "decimal"}
                value={field.value}
                aria-required={empty || undefined}
                onChange={(event) => change(key, event.currentTarget.value)}
                onBlur={() => void save(key)}
              />
            );
          })}
        </div>
      </section>
    </Card>
  );
}
