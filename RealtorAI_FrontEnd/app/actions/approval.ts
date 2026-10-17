"use server";

import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import { approve, confirmField, type Approval, type ApprovalBlocker, type ListingField } from "@/lib/api/listings";

/**
 * T151 — Server Actions for the approve control and the facts' confirm action.
 *
 * The gate is the API's (FR-026, FR-066): these forward the hash the screen rendered and turn
 * a refusal into the blockers the control states in the agent's language. An approval is the
 * agent's own act, so it is only ever sent from a button they pressed (VIII.7).
 */

/** Keys under `listings.approval.errors`. */
export type ApprovalErrorCode = "validation" | "forbidden" | "notFound" | "unexpected";

export type ApproveResult =
  | { status: "approved"; approval: Approval }
  /** The gate refused it; `changed` means the screen was showing content that is no longer there. */
  | { status: "blocked"; blockers: ApprovalBlocker[]; changed: boolean }
  | { status: "error"; code: ApprovalErrorCode };

export type ConfirmResult = { status: "confirmed"; field: ListingField } | { status: "error"; code: ApprovalErrorCode };

const IdSchema = z.uuid();
const HashSchema = z.string().regex(/^[0-9a-f]{64}$/);

function codeFor(error: unknown): ApprovalErrorCode {
  if (!(error instanceof ApiError)) return "unexpected";

  switch (error.status) {
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

export async function approveAction(
  listingId: string,
  targetType: "listing" | "content_draft",
  targetId: string,
  reviewedContentHash: string
): Promise<ApproveResult> {
  if (
    !IdSchema.safeParse(listingId).success ||
    !IdSchema.safeParse(targetId).success ||
    (targetType !== "listing" && targetType !== "content_draft") ||
    !HashSchema.safeParse(reviewedContentHash).success
  ) {
    return { status: "error", code: "validation" };
  }

  try {
    return { status: "approved", approval: await approve(listingId, { targetType, targetId, reviewedContentHash }) };
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      const blockers = (error.problem?.blockers as ApprovalBlocker[] | undefined) ?? [];
      return { status: "blocked", blockers, changed: blockers.some((blocker) => blocker.code === "content_changed_since_review") };
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Approving failed", error);
    return { status: "error", code };
  }
}

export async function confirmFieldAction(listingId: string, fieldId: string): Promise<ConfirmResult> {
  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(fieldId).success) {
    return { status: "error", code: "notFound" };
  }

  try {
    return { status: "confirmed", field: await confirmField(listingId, fieldId) };
  } catch (error) {
    const code = codeFor(error);
    if (code === "unexpected") console.error("Confirming the field failed", error);
    return { status: "error", code };
  }
}
