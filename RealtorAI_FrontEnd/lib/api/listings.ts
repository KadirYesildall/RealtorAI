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
