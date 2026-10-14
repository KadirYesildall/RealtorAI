"use server";

import { refresh } from "next/cache";
import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import {
  decideMembership,
  getBrokerageSettings,
  putBrokerageSettings,
  type Tone,
} from "@/lib/api/brokerage";
import {
  BrokerageSettingsFormSchema,
  MembershipDecisionSchema,
} from "@/lib/validation/brokerage";

/**
 * T063 — Server Actions for the brokerage screen.
 *
 * Authorization is not decided here. Each action forwards the session's token and the .NET
 * API authorizes the call against the caller's membership, loaded from the database (FR-063);
 * a hidden form field or a tampered brokerage id gains nothing because the API, not this file,
 * decides. What these actions do own is input validation before the round trip, and turning
 * API failures into dictionary keys the form shows in the agent's language.
 */

/** Keys under `brokerage.errors`. */
export type BrokerageErrorCode =
  | "validation"
  | "unauthenticated"
  | "forbidden"
  | "notFound"
  | "alreadyDecided"
  | "unexpected";

export type SettingsField =
  | "name"
  | "licenseNumber"
  | "state"
  | "requiredDisclaimers"
  | "targetMlsId"
  | "defaultTone"
  | "aiBudgetTokens"
  | "aiBudgetImages";

export type SettingsActionState =
  | { status: "idle" }
  | { status: "saved" }
  | {
      status: "error";
      code: BrokerageErrorCode;
      /** Field name → key under `brokerage.errors`. */
      fieldErrors?: Partial<Record<SettingsField, string>>;
    };

export type DecisionActionState =
  | { status: "idle" }
  | { status: "decided"; decision: "approved" | "rejected" }
  | { status: "error"; code: BrokerageErrorCode };

const IdSchema = z.uuid();

const SETTINGS_FIELDS: readonly SettingsField[] = [
  "name",
  "licenseNumber",
  "state",
  "requiredDisclaimers",
  "targetMlsId",
  "defaultTone",
  "aiBudgetTokens",
  "aiBudgetImages",
];

function isSettingsField(value: PropertyKey): value is SettingsField {
  return (SETTINGS_FIELDS as readonly PropertyKey[]).includes(value);
}

function codeFor(error: unknown): BrokerageErrorCode {
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
    case 409:
      return "alreadyDecided";
    case 422:
      return "validation";
    default:
      return "unexpected";
  }
}

export async function updateBrokerageSettingsAction(
  brokerageId: string,
  _previous: SettingsActionState,
  formData: FormData
): Promise<SettingsActionState> {
  if (!IdSchema.safeParse(brokerageId).success) {
    return { status: "error", code: "notFound" };
  }

  const parsed = BrokerageSettingsFormSchema.safeParse({
    name: formData.get("name") ?? "",
    licenseNumber: formData.get("licenseNumber") ?? "",
    state: formData.get("state") ?? "",
    requiredDisclaimers: formData.get("requiredDisclaimers") ?? "",
    targetMlsId: formData.get("targetMlsId") ?? "",
    defaultTone: formData.get("defaultTone") ?? "",
    aiBudgetTokens: formData.get("aiBudgetTokens") ?? "",
    aiBudgetImages: formData.get("aiBudgetImages") ?? "",
  });

  if (!parsed.success) {
    const fieldErrors: Partial<Record<SettingsField, string>> = {};

    for (const issue of parsed.error.issues) {
      const field = issue.path[0];

      if (field !== undefined && isSettingsField(field) && fieldErrors[field] === undefined) {
        fieldErrors[field] = issue.message;
      }
    }

    return { status: "error", code: "validation", fieldErrors };
  }

  try {
    // Read first so fields this form does not edit — the logo — go back exactly as stored.
    // The API refuses any change to the logo key from this endpoint (research R32).
    const current = await getBrokerageSettings(brokerageId);

    await putBrokerageSettings(brokerageId, {
      ...current,
      ...parsed.data,
      targetMlsId: parsed.data.targetMlsId ?? undefined,
      defaultTone: parsed.data.defaultTone satisfies Tone,
    });
  } catch (error) {
    const code = codeFor(error);

    if (code === "validation" && error instanceof ApiError && error.problem?.errors) {
      // The server's reasons are English prose; the form shows its own localized message
      // against each field the server named.
      const fieldErrors: Partial<Record<SettingsField, string>> = {};

      for (const field of Object.keys(error.problem.errors)) {
        if (isSettingsField(field)) {
          fieldErrors[field] = "serverRejected";
        }
      }

      return { status: "error", code, fieldErrors };
    }

    if (code === "unexpected") {
      console.error("Brokerage settings update failed", error);
    }

    return { status: "error", code };
  }

  refresh();

  return { status: "saved" };
}

export async function decideMembershipAction(
  brokerageId: string,
  membershipId: string,
  _previous: DecisionActionState,
  formData: FormData
): Promise<DecisionActionState> {
  if (!IdSchema.safeParse(brokerageId).success || !IdSchema.safeParse(membershipId).success) {
    return { status: "error", code: "notFound" };
  }

  const decision = MembershipDecisionSchema.safeParse(formData.get("decision"));

  if (!decision.success) {
    return { status: "error", code: "validation" };
  }

  try {
    await decideMembership(brokerageId, membershipId, decision.data);
  } catch (error) {
    const code = codeFor(error);

    if (code === "unexpected") {
      console.error("Membership decision failed", error);
    }

    return { status: "error", code };
  }

  refresh();

  return { status: "decided", decision: decision.data };
}
