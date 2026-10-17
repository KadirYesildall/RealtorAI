"use server";

import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import {
  acknowledgeFinding,
  applyFix,
  listFindings,
  runCompliance,
  type ComplianceFinding,
  type ContentDraft,
  type PipelineStatus,
} from "@/lib/api/listings";
import { AcknowledgeReasonSchema } from "@/lib/validation/listing";

/**
 * T136–T137 — Server Actions for the compliance review.
 *
 * The checks and the rule that a blocking finding can never be dismissed are the API's (FR-022);
 * these validate, forward with the session's token, and turn answers into states the panel
 * states in the agent's language.
 */

/** Keys under `listings.compliance.errors`. */
export type ComplianceErrorCode =
  | "running"
  | "changed"
  | "noFix"
  | "blocking"
  | "reasonTooShort"
  | "forbidden"
  | "notFound"
  | "unexpected";

export type RunComplianceResult = { status: "started"; pipeline: PipelineStatus } | { status: "error"; code: ComplianceErrorCode };

export type ApplyFixResult = { status: "saved"; draft: ContentDraft } | { status: "error"; code: ComplianceErrorCode };

export type AcknowledgeResult = { status: "saved"; finding: ComplianceFinding } | { status: "error"; code: ComplianceErrorCode };

const IdSchema = z.uuid();

function codeFor(error: unknown): ComplianceErrorCode {
  if (!(error instanceof ApiError)) return "unexpected";

  switch (error.status) {
    case 403:
      return "forbidden";
    case 404:
      return "notFound";
    case 409:
      return "changed";
    default:
      return "unexpected";
  }
}

export async function runComplianceAction(listingId: string): Promise<RunComplianceResult> {
  if (!IdSchema.safeParse(listingId).success) return { status: "error", code: "notFound" };

  try {
    return { status: "started", pipeline: await runCompliance(listingId) };
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) return { status: "error", code: "running" };

    const code = codeFor(error);
    if (code === "unexpected") console.error("Starting the compliance check failed", error);
    return { status: "error", code };
  }
}

/** The findings as stored — what the panel re-reads after a run settles. */
export async function findingsAction(listingId: string): Promise<ComplianceFinding[] | null> {
  if (!IdSchema.safeParse(listingId).success) return null;

  try {
    return await listFindings(listingId);
  } catch (error) {
    console.error("Reading compliance findings failed", error);
    return null;
  }
}

export async function applyFixAction(listingId: string, findingId: string): Promise<ApplyFixResult> {
  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(findingId).success) return { status: "error", code: "notFound" };

  try {
    return { status: "saved", draft: await applyFix(listingId, findingId) };
  } catch (error) {
    if (error instanceof ApiError && error.status === 422) return { status: "error", code: "noFix" };

    const code = codeFor(error);
    if (code === "unexpected") console.error("Applying the suggested wording failed", error);
    return { status: "error", code };
  }
}

export async function acknowledgeAction(listingId: string, findingId: string, reason: string): Promise<AcknowledgeResult> {
  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(findingId).success) return { status: "error", code: "notFound" };

  const parsed = AcknowledgeReasonSchema.safeParse(reason);

  if (!parsed.success) return { status: "error", code: "reasonTooShort" };

  try {
    return { status: "saved", finding: await acknowledgeFinding(listingId, findingId, parsed.data) };
  } catch (error) {
    // 422 here is the API refusing a blocking finding (FR-022); the reason was checked above.
    if (error instanceof ApiError && error.status === 422) {
      return { status: "error", code: error.problem?.errors?.reason ? "reasonTooShort" : "blocking" };
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Acknowledging the warning failed", error);
    return { status: "error", code };
  }
}
