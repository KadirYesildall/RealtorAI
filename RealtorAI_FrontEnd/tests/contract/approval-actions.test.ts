import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";

/**
 * T151 — the approve and confirm Server Actions.
 *
 * The API is mocked: these cover validation before the round trip and how a refusal becomes the
 * blockers the control states — above all that a hash mismatch is reported as "changed", so the
 * screen reloads instead of offering to approve again what the agent did not see (SC-011). The
 * gate itself is tested against the real API in ApprovalTests and ApprovalBlockedTests.
 */

const api = vi.hoisted(() => ({ approve: vi.fn(), confirmField: vi.fn() }));

vi.mock("@/lib/api/listings", () => api);

const { approveAction, confirmFieldAction } = await import("@/app/actions/approval");

const LISTING = "99990000-0000-4000-8000-000000000001";
const DRAFT = "01a14502-1173-75dd-9268-b36bc50c2a41";
const HASH = "a".repeat(64);

beforeEach(() => vi.clearAllMocks());

describe("approveAction", () => {
  it("sends the hash the screen rendered, for the mls channel", async () => {
    api.approve.mockResolvedValue({ id: "x", targetId: DRAFT });

    expect(await approveAction(LISTING, "content_draft", DRAFT, HASH)).toMatchObject({ status: "approved" });
    expect(api.approve).toHaveBeenCalledWith(LISTING, { targetType: "content_draft", targetId: DRAFT, reviewedContentHash: HASH });
  });

  it("returns the blockers of a refusal", async () => {
    const blockers = [{ code: "compliance_not_run", detail: "Not checked." }];
    api.approve.mockRejectedValue(new ApiError(409, { blockers }, "409"));

    expect(await approveAction(LISTING, "listing", LISTING, HASH)).toEqual({ status: "blocked", blockers, changed: false });
  });

  it("reads a hash mismatch as content that changed since review", async () => {
    api.approve.mockRejectedValue(new ApiError(409, { blockers: [{ code: "content_changed_since_review", detail: "Changed." }] }, "409"));

    expect(await approveAction(LISTING, "content_draft", DRAFT, HASH)).toMatchObject({ status: "blocked", changed: true });
  });

  it("refuses a malformed hash or target type before the round trip", async () => {
    expect(await approveAction(LISTING, "content_draft", DRAFT, "not-a-hash")).toEqual({ status: "error", code: "validation" });
    // @ts-expect-error — photos are approved with the listing.
    expect(await approveAction(LISTING, "photo", DRAFT, HASH)).toEqual({ status: "error", code: "validation" });
    expect(api.approve).not.toHaveBeenCalled();
  });
});

describe("confirmFieldAction", () => {
  it("confirms the field", async () => {
    api.confirmField.mockResolvedValue({ id: DRAFT, confirmed: true });

    expect(await confirmFieldAction(LISTING, DRAFT)).toEqual({ status: "confirmed", field: { id: DRAFT, confirmed: true } });
  });

  it("reads 403 as forbidden", async () => {
    api.confirmField.mockRejectedValue(new ApiError(403, {}, "403"));

    expect(await confirmFieldAction(LISTING, DRAFT)).toEqual({ status: "error", code: "forbidden" });
  });
});
