import "server-only";
import type { components } from "@/lib/api/schema";
import { createServerApiClient } from "@/lib/api/server";

/**
 * US1 — listing, field and profile endpoints, called from Server Components and Server Actions
 * only, so the session's token never reaches client JavaScript (T012). Shapes come from
 * `schema.d.ts`, generated from the OpenAPI contract (`npm run api:types`).
 *
 * Photo upload is not here: it goes through the Route Handler at
 * `app/api/listings/[listingId]/photos/route.ts`, which streams the browser's multipart body to
 * the API instead of buffering it through a Server Action.
 */

export type Listing = components["schemas"]["Listing"];
export type ListingDetail = components["schemas"]["ListingDetail"];
export type ListingField = components["schemas"]["ListingField"];
export type ListingStatus = components["schemas"]["ListingStatus"];
export type Photo = components["schemas"]["Photo"];
export type PhotoUploadResult = components["schemas"]["PhotoUploadResult"];
export type Provenance = components["schemas"]["Provenance"];
export type CreateListingRequest = components["schemas"]["CreateListingRequest"];
export type Profile = components["schemas"]["Profile"];

const listingPath = (listingId: string) => `/api/listings/${encodeURIComponent(listingId)}`;

export async function createListing(body: CreateListingRequest): Promise<Listing> {
  return createServerApiClient().post<Listing>("/api/listings", body);
}

export async function listListings(includeArchived: boolean): Promise<Listing[]> {
  return createServerApiClient().get<Listing[]>(`/api/listings${includeArchived ? "?includeArchived=true" : ""}`);
}

export async function getListing(listingId: string): Promise<ListingDetail> {
  return createServerApiClient().get<ListingDetail>(listingPath(listingId));
}

/** FR-066: refused with 409 when `rowVersion` is no longer current. */
export async function autosaveListing(listingId: string, body: CreateListingRequest, rowVersion: number): Promise<Listing> {
  return createServerApiClient().patch<Listing>(listingPath(listingId), body, { ifMatch: rowVersion });
}

/** Soft delete (FR-067). Idempotent. */
export async function archiveListing(listingId: string): Promise<void> {
  await createServerApiClient().delete<void>(listingPath(listingId));
}

/** `rowVersion` 0 creates the field; otherwise it is the field's current version. */
export async function upsertField(
  listingId: string,
  body: { resoFieldKey: string; value: unknown; provenance: Provenance; sourceNote?: string | null },
  rowVersion: number
): Promise<ListingField> {
  return createServerApiClient().put<ListingField>(`${listingPath(listingId)}/fields`, body, { ifMatch: rowVersion });
}

export async function completeProfile(body: { licenseNumber: string; licenseState: string }): Promise<Profile> {
  return createServerApiClient().post<Profile>("/api/profile/complete", body);
}

export async function declareMembership(body: {
  brokerageId: string | null;
  brokerageName: string;
  brokerageLicenseNumber: string | null;
  brokerageState: string | null;
}): Promise<components["schemas"]["Membership"]> {
  return createServerApiClient().post<components["schemas"]["Membership"]>("/api/brokerages/memberships", body);
}

// ───────────────────────────── US2: photos and analysis ─────────────────────────────

export type PipelineStatus = components["schemas"]["PipelineStatus"];
export type PatchPhotoRequest = {
  roomType?: components["schemas"]["RoomType"] | null;
  featureTags?: string[] | null;
  caption?: string | null;
  altered?: boolean;
  alterationType?: "virtual_staging" | "other";
  disclosureLabel?: string;
  pairedOriginalId?: string;
};

/** 202 with the run; 503 (no model) and 409 (budget) arrive as ApiError, both with the manual path open. */
export async function startPipeline(listingId: string): Promise<PipelineStatus> {
  return createServerApiClient().post<PipelineStatus>(`${listingPath(listingId)}/pipeline`, {});
}

export async function getPipeline(listingId: string): Promise<PipelineStatus> {
  return createServerApiClient().get<PipelineStatus>(`${listingPath(listingId)}/pipeline`);
}

/** Absent fields are unchanged; roomType or featureTags null returns to the model's value. */
export async function patchPhoto(listingId: string, photoId: string, body: PatchPhotoRequest): Promise<Photo> {
  return createServerApiClient().patch<Photo>(`${listingPath(listingId)}/photos/${encodeURIComponent(photoId)}`, body);
}

export async function reorderPhotos(
  listingId: string,
  orderedPhotoIds: string[],
  coverPhotoId?: string
): Promise<{ orderedPhotoIds?: string[]; coverPhotoId?: string | null }> {
  return createServerApiClient().put(`${listingPath(listingId)}/photos/order`, {
    orderedPhotoIds,
    ...(coverPhotoId ? { coverPhotoId } : {}),
  });
}

/** Detaches the photo; the original stays in the write-once bucket (FR-001). */
export async function deletePhoto(listingId: string, photoId: string): Promise<void> {
  await createServerApiClient().delete<void>(`${listingPath(listingId)}/photos/${encodeURIComponent(photoId)}`);
}

// ───────────────────────────── US3: listing content ─────────────────────────────

export type ContentDraft = components["schemas"]["ContentDraft"];
export type ContentKind = components["schemas"]["ContentKind"];
export type Tone = components["schemas"]["Tone"];

/** Every kind's newest version: the drafts the agent reviews (FR-013, FR-014). */
export async function listContent(listingId: string): Promise<ContentDraft[]> {
  return createServerApiClient().get<ContentDraft[]>(`${listingPath(listingId)}/content`);
}

/** The manual path (SC-010): the agent's own words as the kind's draft, no model involved. */
export async function createContent(listingId: string, body: { kind: ContentKind; body: string; tone?: Tone }): Promise<ContentDraft> {
  return createServerApiClient().post<ContentDraft>(`${listingPath(listingId)}/content`, body);
}

/**
 * 202 with the run; 503 (no model) and 409 (unverified MLS limit, budget, a run in progress)
 * arrive as ApiError with `reason`, each with the manual path open.
 */
export async function regenerateContent(listingId: string, kind: ContentKind, tone?: Tone): Promise<PipelineStatus> {
  return createServerApiClient().post<PipelineStatus>(`${listingPath(listingId)}/content/regenerate`, { kind, ...(tone ? { tone } : {}) });
}

/** FR-066: refused with 409 and the current draft when `rowVersion` is no longer current. */
export async function editContent(
  listingId: string,
  draftId: string,
  body: { body?: string; selected?: boolean },
  rowVersion: number
): Promise<ContentDraft> {
  return createServerApiClient().patch<ContentDraft>(`${listingPath(listingId)}/content/${encodeURIComponent(draftId)}`, body, {
    ifMatch: rowVersion,
  });
}
