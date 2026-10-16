"use client";

import { useActionState, useState } from "react";
import { createListingAction, type CreateListingState } from "@/app/actions/listing";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { EMPTY_BASICS, ListingBasicsFields, type BasicsFormValues } from "@/components/listing/ListingBasicsFields";

const INITIAL: CreateListingState = { status: "idle" };

/**
 * T080 step 1 — the basics, submitted once to create the draft. From then on the draft editor
 * autosaves; this form exists because there is nothing to autosave into until the listing does.
 *
 * Controlled, although it submits as a plain form: React resets uncontrolled fields when a form
 * action completes, which would wipe what the agent typed whenever validation sends it back.
 */
export function CreateListingForm({ dict }: { dict: Dictionary["listings"] }) {
  const [state, formAction, pending] = useActionState(createListingAction, INITIAL);
  const [values, setValues] = useState<BasicsFormValues>(EMPTY_BASICS);
  const errors = dict.errors as Record<string, string>;

  const fieldErrors =
    state.status === "error" && state.fieldErrors
      ? Object.fromEntries(
          Object.entries(state.fieldErrors).map(([field, key]) => [field, errors[key ?? ""] ?? errors.serverRejected])
        )
      : {};

  return (
    <form action={formAction} className="space-y-6" noValidate>
      {state.status === "error" && <Alert>{errors[state.code] ?? errors.unexpected}</Alert>}

      <ListingBasicsFields
        dict={dict.basics}
        propertyTypes={dict.propertyTypes}
        errors={fieldErrors}
        values={values}
        onChange={(field, value) => setValues((current) => ({ ...current, [field]: value }))}
      />

      <Button type="submit" loading={pending}>
        {dict.basics.create}
      </Button>
    </form>
  );
}
