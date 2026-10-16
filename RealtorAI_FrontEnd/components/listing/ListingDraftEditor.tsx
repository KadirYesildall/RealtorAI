"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { autosaveListingAction, type BasicsField } from "@/app/actions/listing";
import type { Listing, ListingDetail } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { ListingBasicsSchema, PUBLIC_RECORD_FIELDS, type ListingBasicsInput } from "@/lib/validation/listing";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { ListingBasicsFields, type BasicsFormValues } from "@/components/listing/ListingBasicsFields";
import { PhotoUploader } from "@/components/listing/PhotoUploader";
import { RequiredFieldsSection } from "@/components/listing/RequiredFieldsSection";

/** How long the agent must pause before an edit is saved. Short enough that leaving loses nothing. */
const AUTOSAVE_DELAY_MS = 800;

export type SaveActivity = "idle" | "saving" | "saved" | "failed" | "invalid" | "conflict";

function toFormValues(listing: Pick<Listing, keyof BasicsFormValues>): BasicsFormValues {
  const text = (value: string | number | null | undefined) => (value === null || value === undefined ? "" : String(value));

  return {
    addressLine1: text(listing.addressLine1),
    city: text(listing.city),
    state: text(listing.state),
    postalCode: text(listing.postalCode),
    propertyType: text(listing.propertyType),
    listPrice: text(listing.listPrice),
    bedrooms: text(listing.bedrooms),
    bathrooms: text(listing.bathrooms),
    agentNotes: text(listing.agentNotes),
  };
}

/**
 * T080 steps 1–3 for a draft that exists: the basics (autosaved), the photos, and what is still
 * needed before publishing (Story 1 scenarios 1–6).
 *
 * <b>Autosave.</b> An edit is saved after a short pause, or when the field loses focus, with the
 * row version last saved. Saves never overlap: a change made while one is in flight is saved
 * after it, from the newer version — so the only 409 this can meet is a save made somewhere else
 * (FR-066), which is shown with the choice to load what was saved there, never overwritten.
 */
export function ListingDraftEditor({ listing, dict }: { listing: ListingDetail; dict: Dictionary["listings"] }) {
  const listingId = listing.id ?? "";
  const errorText = dict.errors as Record<string, string>;

  const [values, setValues] = useState<BasicsFormValues>(() => toFormValues(listing));
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<BasicsField, string>>>({});
  const [basicsActivity, setBasicsActivity] = useState<SaveActivity>("idle");
  const [fieldsActivity, setFieldsActivity] = useState<SaveActivity>("idle");
  const [conflict, setConflict] = useState<Listing | null>(null);

  const versionRef = useRef(listing.rowVersion ?? 1);
  const valuesRef = useRef(values);
  const dirtyRef = useRef(false);
  const savingRef = useRef(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const save = useCallback(async () => {
    if (timerRef.current) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }

    // One save at a time. The one in flight picks up any edit made meanwhile, below.
    if (savingRef.current) {
      return;
    }

    while (dirtyRef.current) {
      const parsed = ListingBasicsSchema.safeParse(valuesRef.current);

      if (!parsed.success) {
        const errors: Partial<Record<BasicsField, string>> = {};

        for (const issue of parsed.error.issues) {
          const field = issue.path[0] as BasicsField | undefined;

          if (field && errors[field] === undefined) {
            errors[field] = errorText[issue.message] ?? errorText.serverRejected;
          }
        }

        setFieldErrors(errors);
        setBasicsActivity("invalid");
        return;
      }

      setFieldErrors({});
      dirtyRef.current = false;
      savingRef.current = true;
      setBasicsActivity("saving");

      const result = await autosaveListingAction(listingId, versionRef.current, valuesRef.current as ListingBasicsInput);

      savingRef.current = false;

      if (result.status === "conflict") {
        setConflict(result.current);
        setBasicsActivity("conflict");
        return;
      }

      if (result.status === "error") {
        dirtyRef.current = true;
        setFieldErrors(
          Object.fromEntries(
            Object.entries(result.fieldErrors ?? {}).map(([field, key]) => [field, errorText[key ?? ""] ?? errorText.serverRejected])
          )
        );
        setBasicsActivity(result.code === "validation" ? "invalid" : "failed");
        return;
      }

      versionRef.current = result.rowVersion;
      setBasicsActivity("saved");
    }
  }, [errorText, listingId]);

  const change = (field: keyof BasicsFormValues, value: string) => {
    const next = { ...valuesRef.current, [field]: value };
    valuesRef.current = next;
    dirtyRef.current = true;
    setValues(next);

    if (timerRef.current) {
      clearTimeout(timerRef.current);
    }

    timerRef.current = setTimeout(() => void save(), AUTOSAVE_DELAY_MS);
  };

  // A pending edit is saved before the page goes away, not dropped with the timer.
  useEffect(
    () => () => {
      if (dirtyRef.current) {
        void save();
      }
    },
    [save]
  );

  const loadLatest = () => {
    if (conflict) {
      const latest = toFormValues(conflict);
      valuesRef.current = latest;
      versionRef.current = conflict.rowVersion ?? versionRef.current;
      setValues(latest);
    }

    dirtyRef.current = false;
    setConflict(null);
    setBasicsActivity("idle");
  };

  const missingCount = (listing.missingRequiredFieldKeys ?? []).filter((key) =>
    (PUBLIC_RECORD_FIELDS as readonly string[]).includes(key)
  ).length;
  const [requiredMissing, setRequiredMissing] = useState(missingCount);
  const photoCount = (listing.photos ?? []).length;

  return (
    <div className="space-y-6">
      <SaveIndicator activity={combine(basicsActivity, fieldsActivity)} dict={dict.save} />

      {basicsActivity === "conflict" && (
        <Alert>
          <p className="font-medium">{dict.save.conflictTitle}</p>
          <p className="mt-1">{dict.save.conflictBody}</p>
          <Button type="button" variant="secondary" size="sm" className="mt-3" onClick={loadLatest}>
            {dict.save.loadLatest}
          </Button>
        </Alert>
      )}

      <Card id="basics">
        <h2 className="mb-5 text-lg font-semibold text-[var(--color-text)]">{dict.basics.title}</h2>
        <ListingBasicsFields
          dict={dict.basics}
          propertyTypes={dict.propertyTypes}
          errors={fieldErrors}
          values={values}
          onChange={change}
          onBlur={() => void save()}
        />
      </Card>

      <RequiredFieldsSection
        listingId={listingId}
        fields={listing.fields ?? []}
        missingKeys={listing.missingRequiredFieldKeys ?? []}
        dict={dict}
        onActivity={setFieldsActivity}
        onMissingCountChange={setRequiredMissing}
      />

      <PhotoUploader listingId={listingId} photos={listing.photos ?? []} dict={dict} />

      <Card id="progress">
        <h2 className="text-lg font-semibold text-[var(--color-text)]">{dict.progress.title}</h2>
        <ul className="mt-4 space-y-2 text-sm text-[var(--color-text)]">
          <li>{photoCount === 0 ? dict.progress.photosNone : format(dict.progress.photosCount, { count: String(photoCount) })}</li>
          <li>
            {requiredMissing === 0
              ? dict.progress.requiredDone
              : format(dict.progress.requiredMissing, { count: String(requiredMissing) })}
          </li>
        </ul>
        <p className="mt-4 text-sm text-[var(--color-text-muted)]">{dict.progress.aiNote}</p>
      </Card>
    </div>
  );
}

/** One indicator for every autosave on the page: the least settled state wins. */
function combine(...activities: SaveActivity[]): SaveActivity {
  const order: SaveActivity[] = ["conflict", "failed", "invalid", "saving", "saved", "idle"];

  return order.find((state) => activities.includes(state)) ?? "idle";
}

function SaveIndicator({ activity, dict }: { activity: SaveActivity; dict: Dictionary["listings"]["save"] }) {
  const text =
    activity === "saving"
      ? dict.saving
      : activity === "saved"
        ? dict.saved
        : activity === "failed"
          ? dict.failed
          : activity === "invalid"
            ? dict.invalid
            : "";

  return (
    <p
      role="status"
      aria-live="polite"
      className={
        activity === "failed" || activity === "invalid"
          ? "min-h-5 text-sm text-[var(--color-danger)]"
          : "min-h-5 text-sm text-[var(--color-text-muted)]"
      }
    >
      {text}
    </p>
  );
}
