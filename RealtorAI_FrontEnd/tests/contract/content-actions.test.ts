import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";
import { segment } from "@/components/listing/ClaimHighlighter";

/**
 * T113–T114 — the content Server Actions, and how claims become highlighted segments.
 *
 * The API is mocked: these cover validation before the round trip and how each answer becomes a
 * state — above all that every refusal to generate reads as the manual path (SC-010), and that a
 * conflicting edit comes back with the draft as it now is (FR-066). The rules themselves are
 * tested against the real API in ContentRegenerateTests, ContentEditTests and ContentCreateTests.
 */

const api = vi.hoisted(() => ({
  createContent: vi.fn(),
  editContent: vi.fn(),
  regenerateContent: vi.fn(),
}));

vi.mock("@/lib/api/listings", () => api);

const { regenerateContentAction, createContentAction, editContentAction } = await import("@/app/actions/content");

const LISTING = "99990000-0000-4000-8000-000000000001";
const DRAFT = "01a14502-1173-75dd-9268-b36bc50c2a41";

beforeEach(() => vi.clearAllMocks());

describe("regenerateContentAction", () => {
  it("returns the run when generation starts, in the tone asked for", async () => {
    api.regenerateContent.mockResolvedValue({ listingId: LISTING, steps: [{ name: "content_generation", state: "queued" }] });

    const result = await regenerateContentAction(LISTING, "public_remarks", "warm");

    expect(result).toMatchObject({ status: "started" });
    expect(api.regenerateContent).toHaveBeenCalledWith(LISTING, "public_remarks", "warm");
  });

  it.each([
    [503, undefined, "noModel"],
    [409, "mls_rule_profile_incomplete", "profileIncomplete"],
    [409, "budget_exceeded", "budget"],
    [409, "pipeline_running", "running"],
  ])("reads %i (%s) as the manual path (%s), not a failure", async (status, reason, expected) => {
    api.regenerateContent.mockRejectedValue(new ApiError(status, { manualCompletionAvailable: true, reason }, String(status)));

    expect(await regenerateContentAction(LISTING, "headline")).toEqual({ status: "manual", reason: expected });
  });

  it("refuses a kind or tone the contract does not have, without calling the API", async () => {
    // @ts-expect-error — a value the type does not allow, as a crafted request would send.
    expect(await regenerateContentAction(LISTING, "brochure")).toEqual({ status: "error", code: "validation" });
    // @ts-expect-error — likewise.
    expect(await regenerateContentAction(LISTING, "headline", "shouty")).toEqual({ status: "error", code: "validation" });
    expect(api.regenerateContent).not.toHaveBeenCalled();
  });
});

describe("createContentAction", () => {
  it("forwards the agent's words", async () => {
    api.createContent.mockResolvedValue({ id: DRAFT, kind: "headline", body: "Light-filled home" });

    const result = await createContentAction(LISTING, "headline", "Light-filled home", "concise");

    expect(result).toEqual({ status: "saved", draft: { id: DRAFT, kind: "headline", body: "Light-filled home" } });
    expect(api.createContent).toHaveBeenCalledWith(LISTING, { kind: "headline", body: "Light-filled home", tone: "concise" });
  });

  it("refuses empty text before the round trip", async () => {
    expect(await createContentAction(LISTING, "headline", "   ")).toEqual({ status: "error", code: "validation" });
    expect(api.createContent).not.toHaveBeenCalled();
  });

  it("reads 409 as content that already exists", async () => {
    api.createContent.mockRejectedValue(new ApiError(409, {}, "409"));

    expect(await createContentAction(LISTING, "headline", "Words")).toEqual({ status: "error", code: "exists" });
  });
});

describe("editContentAction", () => {
  it("sends the edit with the draft's row version", async () => {
    api.editContent.mockResolvedValue({ id: DRAFT, body: "New words", rowVersion: 3 });

    await editContentAction(LISTING, DRAFT, 2, { body: "New words" });

    expect(api.editContent).toHaveBeenCalledWith(LISTING, DRAFT, { body: "New words" }, 2);
  });

  it("returns the draft as it now is on a version conflict (FR-066)", async () => {
    const current = { id: DRAFT, body: "Another tab's words", rowVersion: 3 };
    api.editContent.mockRejectedValue(new ApiError(409, { currentRowVersion: 3, current }, "409"));

    expect(await editContentAction(LISTING, DRAFT, 2, { body: "Mine" })).toEqual({ status: "conflict", current });
  });

  it("reads a 409 without a current draft as a superseded draft", async () => {
    api.editContent.mockRejectedValue(new ApiError(409, {}, "409"));

    expect(await editContentAction(LISTING, DRAFT, 2, { selected: true })).toEqual({ status: "error", code: "superseded" });
  });

  it("refuses an empty change, or fields the endpoint does not take", async () => {
    expect(await editContentAction(LISTING, DRAFT, 2, {})).toEqual({ status: "error", code: "validation" });
    // @ts-expect-error — not an editable field.
    expect(await editContentAction(LISTING, DRAFT, 2, { status: "approved" })).toEqual({ status: "error", code: "validation" });
    expect(api.editContent).not.toHaveBeenCalled();
  });
});

describe("segment", () => {
  const label = (id: string) => ({ a: "Interior features", b: "View" })[id] ?? "?";

  it("splits the body at each claim, naming every fact a span rests on", () => {
    const body = "Welcome home. Vaulted ceilings open up the living room. Enjoy the view.";
    const start = body.indexOf("Vaulted");
    const end = body.indexOf(" Enjoy");

    expect(
      segment(
        body,
        [
          { spanStart: start, spanEnd: end, fieldId: "a" },
          { spanStart: start, spanEnd: end, fieldId: "b" },
        ],
        label
      )
    ).toEqual([
      { text: "Welcome home. ", facts: null },
      { text: "Vaulted ceilings open up the living room.", facts: ["Interior features", "View"] },
      { text: " Enjoy the view.", facts: null },
    ]);
  });

  it("skips spans outside the body or overlapping one already taken", () => {
    expect(
      segment(
        "Short text.",
        [
          { spanStart: 0, spanEnd: 5, fieldId: "a" },
          { spanStart: 3, spanEnd: 8, fieldId: "b" },
          { spanStart: 4, spanEnd: 99, fieldId: "b" },
        ],
        label
      )
    ).toEqual([
      { text: "Short", facts: ["Interior features"] },
      { text: " text.", facts: null },
    ]);
  });
});
