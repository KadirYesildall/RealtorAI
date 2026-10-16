import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";
import { PhotoPatchSchema, parseTags } from "@/lib/validation/listing";

/**
 * T099–T101 — the photo and analysis Server Actions.
 *
 * The API is mocked: these cover validation before the round trip and how each answer becomes a
 * state — above all that 503 and 409 from starting analysis read as the manual path (SC-010),
 * not as failures. Authorization and the rules themselves are tested against the real API in
 * PipelineStartTests, PhotoPatchTests and PhotoOrderTests.
 */

const api = vi.hoisted(() => ({
  startPipeline: vi.fn(),
  getPipeline: vi.fn(),
  patchPhoto: vi.fn(),
  reorderPhotos: vi.fn(),
  deletePhoto: vi.fn(),
}));

vi.mock("@/lib/api/listings", () => api);

const { startAnalysisAction, patchPhotoAction, reorderPhotosAction, deletePhotoAction } = await import("@/app/actions/photos");

const LISTING = "99990000-0000-4000-8000-000000000001";
const PHOTO = "01a14502-1173-75dd-9268-b36bc50c2a41";
const OTHER = "01a14502-113a-725c-876a-ee51e1f6d2ca";

beforeEach(() => vi.clearAllMocks());

describe("startAnalysisAction", () => {
  it("returns the run when analysis starts", async () => {
    api.startPipeline.mockResolvedValue({ listingId: LISTING, steps: [] });

    expect(await startAnalysisAction(LISTING)).toEqual({ status: "started", pipeline: { listingId: LISTING, steps: [] } });
  });

  it.each([
    [503, "noModel"],
    [409, "budget"],
    [422, "noPhotos"],
  ])("reads %i as the manual path (%s), not a failure", async (status, reason) => {
    api.startPipeline.mockRejectedValue(new ApiError(status, { manualCompletionAvailable: true }, String(status)));

    expect(await startAnalysisAction(LISTING)).toEqual({ status: "manual", reason });
  });

  it("refuses a listing id that is not one", async () => {
    expect(await startAnalysisAction("../x")).toEqual({ status: "error", code: "notFound" });
    expect(api.startPipeline).not.toHaveBeenCalled();
  });
});

describe("patchPhotoAction", () => {
  it("forwards a valid override", async () => {
    api.patchPhoto.mockResolvedValue({ id: PHOTO, roomType: "kitchen" });

    const result = await patchPhotoAction(LISTING, PHOTO, { roomType: "kitchen" });

    expect(result).toEqual({ status: "saved", photo: { id: PHOTO, roomType: "kitchen" } });
    expect(api.patchPhoto).toHaveBeenCalledWith(LISTING, PHOTO, { roomType: "kitchen" });
  });

  it("refuses a room type the contract does not have, without calling the API", async () => {
    // @ts-expect-error — a value the type does not allow, as a crafted request would send.
    const result = await patchPhotoAction(LISTING, PHOTO, { roomType: "ballroom" });

    expect(result).toMatchObject({ status: "error", code: "validation", fields: ["roomType"] });
    expect(api.patchPhoto).not.toHaveBeenCalled();
  });

  it("refuses fields the endpoint does not take", async () => {
    // @ts-expect-error — not a photo field.
    const result = await patchPhotoAction(LISTING, PHOTO, { checksumSha256: "x" });

    expect(result).toMatchObject({ status: "error", code: "validation" });
  });

  it("names the fields the API rejected", async () => {
    api.patchPhoto.mockRejectedValue(new ApiError(422, { errors: { disclosureLabel: ["no"] } }, "422"));

    const result = await patchPhotoAction(LISTING, PHOTO, {
      altered: true,
      alterationType: "virtual_staging",
      disclosureLabel: "x",
      pairedOriginalId: OTHER,
    });

    expect(result).toEqual({ status: "error", code: "validation", fields: ["disclosureLabel"] });
  });
});

describe("reorderPhotosAction and deletePhotoAction", () => {
  it("sends the order and the cover", async () => {
    api.reorderPhotos.mockResolvedValue({});

    expect(await reorderPhotosAction(LISTING, [OTHER, PHOTO], PHOTO)).toEqual({ status: "saved" });
    expect(api.reorderPhotos).toHaveBeenCalledWith(LISTING, [OTHER, PHOTO], PHOTO);
  });

  it("refuses an empty order", async () => {
    expect(await reorderPhotosAction(LISTING, [])).toEqual({ status: "error", code: "validation" });
  });

  it("explains a refused removal of a paired original", async () => {
    api.deletePhoto.mockRejectedValue(new ApiError(409, undefined, "409"));

    expect(await deletePhotoAction(LISTING, PHOTO)).toEqual({ status: "error", code: "pairedOriginal" });
  });
});

describe("photo schemas", () => {
  it("splits tags, trimming and dropping blanks and repeats", () => {
    expect(parseTags(" Island, pendant lights ,, island ,Pendant Lights")).toEqual(["Island", "pendant lights"]);
  });

  it("allows clearing an override with null", () => {
    expect(PhotoPatchSchema.safeParse({ roomType: null, featureTags: null }).success).toBe(true);
  });
});
