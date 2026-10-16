"use server";

import { redirect } from "next/navigation";
import { refresh } from "next/cache";
import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import {
  archiveListing,
  autosaveListing,
  createListing,
  upsertField,
  type Listing,
  type ListingField,
} from "@/lib/api/listings";
import {
  LISTING_BASICS_FIELDS,
  ListingBasicsSchema,
  PUBLIC_RECORD_FIELDS,
  publicRecordValueSchema,
  type ListingBasics,
  type ListingBasicsInput,
  type PublicRecordField,
} from "@/lib/validation/listing";

/**
 * T080/T081 — Server Actions for the listing wizard and index.
 *
 * Authorization is not decided here. Each action forwards the session's token and the .NET API
 * authorizes the call against listing ownership loaded from the database (FR-063); a tampered
 * listing id gains nothing. What these actions own is validation before the round trip, and
 * turning API answers — including FR-066's version conflicts — into states the UI can show in
 * the agent's language.
 */

/** Keys under `listings.errors`. */
export type ListingErrorCode =
  | "validation"
  | "unauthenticated"
  | "forbidden"
  | "notFound"
  | "profileIncomplete"
  | "unexpected";

export type BasicsField = keyof ListingBasics;

export type CreateListingState =
  | { status: "idle" }
  | { status: "error"; code: ListingErrorCode; fieldErrors?: Partial<Record<BasicsField, string>> };

export type AutosaveResult =
  | { status: "saved"; rowVersion: number }
  /** Someone else saved first (FR-066). `current` is what the server now holds. */
  | { status: "conflict"; current: Listing | null }
  | { status: "error"; code: ListingErrorCode; fieldErrors?: Partial<Record<BasicsField, string>> };

export type FieldSaveResult =
  | { status: "saved"; field: ListingField }
  | { status: "conflict"; current: ListingField | null }
  | { status: "error"; code: ListingErrorCode | "fieldInvalid"; message?: string };

const IdSchema = z.uuid();

function codeFor(error: unknown): ListingErrorCode {
  if (!(error instanceof ApiError)) {
    return "unexpected";
  }

  switch (error.status) {
    case 401:
      return "unauthenticated";
    case 403:
      return "forbidden";
    case 404:
      return "notFound";
    case 422:
      return "validation";
    default:
      return "unexpected";
  }
}

function isBasicsField(value: PropertyKey): value is BasicsField {
  return (LISTING_BASICS_FIELDS as PropertyKey[]).includes(value);
}

function fieldErrorsFrom(issues: z.core.$ZodIssue[]): Partial<Record<BasicsField, string>> {
  const fieldErrors: Partial<Record<BasicsField, string>> = {};

  for (const issue of issues) {
    const field = issue.path[0];

    if (field !== undefined && isBasicsField(field) && fieldErrors[field] === undefined) {
      fieldErrors[field] = issue.message;
    }
  }

  return fieldErrors;
}

/** The API's per-field reasons are English prose; the form shows its own message for each field it named. */
function serverFieldErrors(error: unknown): Partial<Record<BasicsField, string>> | undefined {
  if (!(error instanceof ApiError) || !error.problem?.errors) {
    return undefined;
  }

  const fieldErrors: Partial<Record<BasicsField, string>> = {};

  for (const field of Object.keys(error.problem.errors)) {
    if (isBasicsField(field)) {
      fieldErrors[field] = "serverRejected";
    }
  }

  return fieldErrors;
}

function basicsFromForm(formData: FormData): ListingBasicsInput {
  return {
    addressLine1: String(formData.get("addressLine1") ?? ""),
    city: String(formData.get("city") ?? ""),
    state: String(formData.get("state") ?? ""),
    postalCode: String(formData.get("postalCode") ?? ""),
    propertyType: String(formData.get("propertyType") ?? "") as ListingBasicsInput["propertyType"],
    listPrice: String(formData.get("listPrice") ?? ""),
    bedrooms: String(formData.get("bedrooms") ?? ""),
    bathrooms: String(formData.get("bathrooms") ?? ""),
    agentNotes: String(formData.get("agentNotes") ?? ""),
  };
}

// ═════════════════════════════ Create ═════════════════════════════

export async function createListingAction(
  _previous: CreateListingState,
  formData: FormData
): Promise<CreateListingState> {
  const parsed = ListingBasicsSchema.safeParse(basicsFromForm(formData));

  if (!parsed.success) {
    return { status: "error", code: "validation", fieldErrors: fieldErrorsFrom(parsed.error.issues) };
  }

  let created: Listing;

  try {
    created = await createListing(parsed.data);
  } catch (error) {
    if (error instanceof ApiError && error.status === 403) {
      // FR-058: the API refuses a listing before the license is recorded.
      redirect("/dashboard/profile/complete?next=/dashboard/listings/new");
    }

    const code = codeFor(error);

    if (code === "unexpected") {
      console.error("Listing creation failed", error);
    }

    return { status: "error", code, fieldErrors: serverFieldErrors(error) };
  }

  // Outside the try: redirect() signals by throwing, and a catch would swallow it.
  redirect(`/dashboard/listings/new?id=${encodeURIComponent(created.id ?? "")}`);
}

// ═════════════════════════════ Autosave ═════════════════════════════

/**
 * Story 1 scenario 6. Called by the wizard on its own, debounced, with the row version it last
 * saw. Server Actions run one at a time per client, so two saves from one tab can never race
 * each other; a 409 therefore means a save from somewhere else — another tab, another device.
 */
export async function autosaveListingAction(
  listingId: string,
  rowVersion: number,
  values: ListingBasicsInput
): Promise<AutosaveResult> {
  if (!IdSchema.safeParse(listingId).success || !Number.isInteger(rowVersion) || rowVersion < 1) {
    return { status: "error", code: "notFound" };
  }

  const parsed = ListingBasicsSchema.safeParse(values);

  if (!parsed.success) {
    return { status: "error", code: "validation", fieldErrors: fieldErrorsFrom(parsed.error.issues) };
  }

  try {
    const saved = await autosaveListing(listingId, parsed.data, rowVersion);

    return { status: "saved", rowVersion: saved.rowVersion ?? rowVersion + 1 };
  } catch (error) {
    if (error instanceof ApiError && error.isVersionConflict) {
      return { status: "conflict", current: (error.problem?.current as Listing | undefined) ?? null };
    }

    const code = codeFor(error);

    if (code === "unexpected") {
      console.error("Listing autosave failed", error);
    }

    return { status: "error", code, fieldErrors: serverFieldErrors(error) };
  }
}

// ═════════════════════════════ Required public-record fields ═════════════════════════════

/**
 * FR-008's fallback: the agent enters a public-record field by hand. Always `agent_entered` —
 * these are FR-007 fields, so neither the UI nor anything else may claim another source for a
 * value the agent typed.
 */
export async function saveListingFieldAction(
  listingId: string,
  field: PublicRecordField,
  rawValue: string,
  rowVersion: number
): Promise<FieldSaveResult> {
  if (
    !IdSchema.safeParse(listingId).success ||
    !(PUBLIC_RECORD_FIELDS as readonly string[]).includes(field) ||
    !Number.isInteger(rowVersion) ||
    rowVersion < 0
  ) {
    return { status: "error", code: "notFound" };
  }

  const parsed = publicRecordValueSchema(field, new Date().getUTCFullYear()).safeParse(rawValue.trim());

  if (!parsed.success) {
    return { status: "error", code: "fieldInvalid", message: parsed.error.issues[0]?.message };
  }

  try {
    const saved = await upsertField(
      listingId,
      { resoFieldKey: field, value: parsed.data, provenance: "agent_entered" },
      rowVersion
    );

    return { status: "saved", field: saved };
  } catch (error) {
    if (error instanceof ApiError && error.isVersionConflict) {
      return { status: "conflict", current: (error.problem?.current as ListingField | undefined) ?? null };
    }

    if (error instanceof ApiError && error.status === 422) {
      return { status: "error", code: "fieldInvalid", message: "serverRejected" };
    }

    const code = codeFor(error);

    if (code === "unexpected") {
      console.error("Listing field save failed", error);
    }

    return { status: "error", code };
  }
}

// ═════════════════════════════ Archive ═════════════════════════════

export type ArchiveState = { status: "idle" } | { status: "error"; code: ListingErrorCode };

/** FR-067: soft delete. The listing leaves the working view; its history stays. */
export async function archiveListingAction(listingId: string): Promise<ArchiveState> {
  if (!IdSchema.safeParse(listingId).success) {
    return { status: "error", code: "notFound" };
  }

  try {
    await archiveListing(listingId);
  } catch (error) {
    const code = codeFor(error);

    if (code === "unexpected") {
      console.error("Listing archive failed", error);
    }

    return { status: "error", code };
  }

  refresh();

  return { status: "idle" };
}
