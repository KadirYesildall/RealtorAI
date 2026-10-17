"use server";

import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import {
  createContent,
  editContent,
  regenerateContent,
  type ContentDraft,
  type ContentKind,
  type PipelineStatus,
  type Tone,
} from "@/lib/api/listings";
import { CONTENT_KINDS, ContentBodySchema, TONES } from "@/lib/validation/listing";

/**
 * T113 — Server Actions for listing content: generate, write by hand, edit, select.
 *
 * Authorization and the MLS's limits are the API's (FR-063, FR-013); these validate the shape,
 * forward with the session's token, and turn answers into states the UI states in the agent's
 * language. Server Actions run one at a time per client, so a 409 on an edit means a save from
 * somewhere else, shown with the draft as it now is (FR-066) — never overwritten.
 */

/** Keys under `listings.content.errors`. Over the limit is checked before sending, against the draft's own limit. */
export type ContentErrorCode = "validation" | "superseded" | "exists" | "forbidden" | "notFound" | "unexpected";

export type RegenerateResult =
  | { status: "started"; pipeline: PipelineStatus }
  /** SC-010: generation is unavailable for this reason, and the content can be written by hand. */
  | { status: "manual"; reason: "noModel" | "profileIncomplete" | "budget" | "running" }
  | { status: "error"; code: ContentErrorCode };

export type ContentSaveResult =
  | { status: "saved"; draft: ContentDraft }
  | { status: "conflict"; current: ContentDraft | null }
  | { status: "error"; code: ContentErrorCode };

const IdSchema = z.uuid();
const KindSchema = z.enum(CONTENT_KINDS);
const ToneSchema = z.enum(TONES);

function codeFor(error: unknown): ContentErrorCode {
  if (!(error instanceof ApiError)) {
    return "unexpected";
  }

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

export async function regenerateContentAction(listingId: string, kind: ContentKind, tone?: Tone): Promise<RegenerateResult> {
  if (!IdSchema.safeParse(listingId).success) {
    return { status: "error", code: "notFound" };
  }

  if (!KindSchema.safeParse(kind).success || (tone !== undefined && !ToneSchema.safeParse(tone).success)) {
    return { status: "error", code: "validation" };
  }

  try {
    return { status: "started", pipeline: await regenerateContent(listingId, kind, tone) };
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 503) return { status: "manual", reason: "noModel" };

      if (error.status === 409) {
        const reason = error.problem?.reason;
        return {
          status: "manual",
          reason: reason === "budget_exceeded" ? "budget" : reason === "pipeline_running" ? "running" : "profileIncomplete",
        };
      }
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Generating content failed", error);
    return { status: "error", code };
  }
}

export async function createContentAction(listingId: string, kind: ContentKind, body: string, tone?: Tone): Promise<ContentSaveResult> {
  if (!IdSchema.safeParse(listingId).success) {
    return { status: "error", code: "notFound" };
  }

  if (
    !KindSchema.safeParse(kind).success ||
    !ContentBodySchema.safeParse(body).success ||
    (tone !== undefined && !ToneSchema.safeParse(tone).success)
  ) {
    return { status: "error", code: "validation" };
  }

  try {
    return { status: "saved", draft: await createContent(listingId, { kind, body, ...(tone ? { tone } : {}) }) };
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      return { status: "error", code: "exists" };
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Saving content failed", error);
    return { status: "error", code };
  }
}

/** An inline edit, a selection, or both, from the draft's `rowVersion` (FR-066). */
export async function editContentAction(
  listingId: string,
  draftId: string,
  rowVersion: number,
  change: { body?: string; selected?: boolean }
): Promise<ContentSaveResult> {
  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(draftId).success || !Number.isInteger(rowVersion) || rowVersion < 1) {
    return { status: "error", code: "notFound" };
  }

  const parsed = z
    .object({ body: ContentBodySchema.optional(), selected: z.boolean().optional() })
    .strict()
    .refine((value) => value.body !== undefined || value.selected !== undefined)
    .safeParse(change);

  if (!parsed.success) {
    return { status: "error", code: "validation" };
  }

  try {
    return { status: "saved", draft: await editContent(listingId, draftId, parsed.data, rowVersion) };
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      const current = error.problem?.current as ContentDraft | undefined;

      // A version conflict carries the draft as it now is; a superseded draft carries nothing.
      return current ? { status: "conflict", current } : { status: "error", code: "superseded" };
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Editing content failed", error);
    return { status: "error", code };
  }
}
