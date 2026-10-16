import "server-only";
import type { components } from "@/lib/api/schema";
import { createServerApiClient } from "@/lib/api/server";

/**
 * T063 — brokerage endpoints, called from Server Components and Server Actions only.
 *
 * Every call runs on the server with the session's access token, so the token never reaches
 * client JavaScript and the browser never talks to the .NET API directly. Shapes come from
 * `schema.d.ts`, generated from the OpenAPI contract (`npm run api:types`).
 */

export type Membership = components["schemas"]["Membership"];
export type MembershipStatus = components["schemas"]["MembershipStatus"];
export type BrokerageSettings = components["schemas"]["BrokerageSettings"];
export type Tone = components["schemas"]["Tone"];

/** The caller's own membership, or null when they have declared no brokerage. */
export async function getOwnMembership(): Promise<Membership | null> {
  return createServerApiClient().get<Membership | null>("/api/brokerages/memberships");
}

/** A brokerage's memberships. The API answers 403 unless the caller is its approved admin. */
export async function listMemberships(
  brokerageId: string,
  status?: MembershipStatus
): Promise<Membership[]> {
  const query = status ? `?status=${encodeURIComponent(status)}` : "";

  return createServerApiClient().get<Membership[]>(
    `/api/brokerages/${encodeURIComponent(brokerageId)}/memberships${query}`
  );
}

export async function getBrokerageSettings(brokerageId: string): Promise<BrokerageSettings> {
  return createServerApiClient().get<BrokerageSettings>(
    `/api/brokerages/${encodeURIComponent(brokerageId)}/settings`
  );
}

export async function putBrokerageSettings(
  brokerageId: string,
  settings: BrokerageSettings
): Promise<BrokerageSettings> {
  return createServerApiClient().put<BrokerageSettings>(
    `/api/brokerages/${encodeURIComponent(brokerageId)}/settings`,
    settings
  );
}

export async function decideMembership(
  brokerageId: string,
  membershipId: string,
  decision: "approved" | "rejected"
): Promise<Membership> {
  return createServerApiClient().post<Membership>(
    `/api/brokerages/${encodeURIComponent(brokerageId)}/memberships/${encodeURIComponent(membershipId)}/decision`,
    { decision }
  );
}

/**
 * The caller's membership for the dashboard chrome (the pending banner).
 *
 * Unlike the brokerage page, the layout must render even when the API is down — a banner is
 * not worth taking the whole dashboard offline for — so a failure here is logged and treated
 * as "nothing to show".
 */
export async function getOwnMembershipForChrome(): Promise<Membership | null> {
  try {
    return await getOwnMembership();
  } catch (error) {
    console.error("Could not load brokerage membership for the dashboard banner", error);
    return null;
  }
}
