"use server";

import { z } from "zod";
import { ApiError } from "@/lib/api/client";
import {
  deletePhoto,
  getPipeline,
  patchPhoto,
  reorderPhotos,
  startPipeline,
  type PatchPhotoRequest,
  type Photo,
  type PipelineStatus,
} from "@/lib/api/listings";
import { PhotoPatchSchema } from "@/lib/validation/listing";

/**
 * T099–T101 — Server Actions for photo analysis and the photo grid.
 *
 * Authorization is the API's (FR-063); these validate, forward with the session's token, and
 * turn answers into states the UI states in the agent's language. Server Actions run one at a
 * time per client, so a grid edit and a reorder from one tab never race each other.
 */

/** Keys under `listings.analysis.errors`. */
export type PhotoErrorCode = "validation" | "forbidden" | "notFound" | "pairedOriginal" | "unexpected";

export type StartAnalysisResult =
  | { status: "started"; pipeline: PipelineStatus }
  /** SC-010: analysis is unavailable, and everything can be done by hand. */
  | { status: "manual"; reason: "noModel" | "budget" | "noPhotos" }
  | { status: "error"; code: PhotoErrorCode };

export type PhotoEditResult =
  | { status: "saved"; photo: Photo }
  | { status: "error"; code: PhotoErrorCode; fields?: string[] };

export type PhotoOrderResult = { status: "saved" } | { status: "error"; code: PhotoErrorCode };

const IdSchema = z.uuid();

function codeFor(error: unknown): PhotoErrorCode {
  if (!(error instanceof ApiError)) {
    return "unexpected";
  }

  switch (error.status) {
    case 403:
      return "forbidden";
    case 404:
      return "notFound";
    case 409:
      return "pairedOriginal";
    case 422:
      return "validation";
    default:
      return "unexpected";
  }
}

export async function startAnalysisAction(listingId: string): Promise<StartAnalysisResult> {
  if (!IdSchema.safeParse(listingId).success) {
    return { status: "error", code: "notFound" };
  }

  try {
    return { status: "started", pipeline: await startPipeline(listingId) };
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 503) return { status: "manual", reason: "noModel" };
      if (error.status === 409) return { status: "manual", reason: "budget" };
      if (error.status === 422) return { status: "manual", reason: "noPhotos" };
    }

    const code = codeFor(error);
    if (code === "unexpected") console.error("Starting analysis failed", error);
    return { status: "error", code };
  }
}

/** The persisted status — what a SignalR hint, or the polling fallback, re-reads. */
export async function pipelineStatusAction(listingId: string): Promise<PipelineStatus | null> {
  if (!IdSchema.safeParse(listingId).success) {
    return null;
  }

  try {
    return await getPipeline(listingId);
  } catch (error) {
    console.error("Reading analysis status failed", error);
    return null;
  }
}

export async function patchPhotoAction(listingId: string, photoId: string, patch: PatchPhotoRequest): Promise<PhotoEditResult> {
  const parsed = PhotoPatchSchema.safeParse(patch);

  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(photoId).success) {
    return { status: "error", code: "notFound" };
  }

  if (!parsed.success) {
    return { status: "error", code: "validation", fields: parsed.error.issues.map((issue) => String(issue.path[0])) };
  }

  try {
    return { status: "saved", photo: await patchPhoto(listingId, photoId, parsed.data as PatchPhotoRequest) };
  } catch (error) {
    const code = codeFor(error);
    if (code === "unexpected") console.error("Photo update failed", error);
    return {
      status: "error",
      code,
      fields: error instanceof ApiError && error.problem?.errors ? Object.keys(error.problem.errors) : undefined,
    };
  }
}

export async function reorderPhotosAction(listingId: string, orderedPhotoIds: string[], coverPhotoId?: string): Promise<PhotoOrderResult> {
  if (
    !IdSchema.safeParse(listingId).success ||
    !z.array(z.uuid()).min(1).safeParse(orderedPhotoIds).success ||
    (coverPhotoId !== undefined && !IdSchema.safeParse(coverPhotoId).success)
  ) {
    return { status: "error", code: "validation" };
  }

  try {
    await reorderPhotos(listingId, orderedPhotoIds, coverPhotoId);
    return { status: "saved" };
  } catch (error) {
    const code = codeFor(error);
    if (code === "unexpected") console.error("Photo reorder failed", error);
    return { status: "error", code };
  }
}

export async function deletePhotoAction(listingId: string, photoId: string): Promise<PhotoOrderResult> {
  if (!IdSchema.safeParse(listingId).success || !IdSchema.safeParse(photoId).success) {
    return { status: "error", code: "notFound" };
  }

  try {
    await deletePhoto(listingId, photoId);
    return { status: "saved" };
  } catch (error) {
    const code = codeFor(error);
    if (code === "unexpected") console.error("Photo removal failed", error);
    return { status: "error", code };
  }
}
