import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";

/**
 * T136–T137 — the compliance Server Actions.
 *
 * The API is mocked: these cover validation before the round trip and how each answer becomes a
 * state — above all that a blocking finding refused at the API reads as "blocking", never as a
 * generic failure the agent might retry (FR-022). The rules are tested against the real API in
 * ComplianceRunTests, ApplyFixTests and AcknowledgeTests.
 */

const api = vi.hoisted(() => ({
  runCompliance: vi.fn(),
  listFindings: vi.fn(),
  applyFix: vi.fn(),
  acknowledgeFinding: vi.fn(),
}));

vi.mock("@/lib/api/listings", () => api);

const { runComplianceAction, applyFixAction, acknowledgeAction } = await import("@/app/actions/compliance");

const LISTING = "99990000-0000-4000-8000-000000000001";
const FINDING = "01a14502-1173-75dd-9268-b36bc50c2a41";

beforeEach(() => vi.clearAllMocks());

describe("runComplianceAction", () => {
  it("returns the run when the check starts", async () => {
    api.runCompliance.mockResolvedValue({ listingId: LISTING, steps: [{ name: "compliance", state: "queued" }] });

    expect(await runComplianceAction(LISTING)).toMatchObject({ status: "started" });
  });

  it("reads 409 as a run already in progress", async () => {
    api.runCompliance.mockRejectedValue(new ApiError(409, { reason: "pipeline_running" }, "409"));

    expect(await runComplianceAction(LISTING)).toEqual({ status: "error", code: "running" });
  });
});

describe("applyFixAction", () => {
  it("returns the re-checked draft", async () => {
    api.applyFix.mockResolvedValue({ id: "d", body: "Room to grow." });

    expect(await applyFixAction(LISTING, FINDING)).toEqual({ status: "saved", draft: { id: "d", body: "Room to grow." } });
  });

  it.each([
    [409, "changed"],
    [422, "noFix"],
    [404, "notFound"],
  ])("reads %i as %s", async (status, code) => {
    api.applyFix.mockRejectedValue(new ApiError(status, {}, String(status)));

    expect(await applyFixAction(LISTING, FINDING)).toEqual({ status: "error", code });
  });
});

describe("acknowledgeAction", () => {
  it("sends the trimmed reason", async () => {
    api.acknowledgeFinding.mockResolvedValue({ id: FINDING, status: "acknowledged" });

    await acknowledgeAction(LISTING, FINDING, "  A landmark buyers ask about.  ");

    expect(api.acknowledgeFinding).toHaveBeenCalledWith(LISTING, FINDING, "A landmark buyers ask about.");
  });

  it("refuses a reason under ten characters before the round trip", async () => {
    expect(await acknowledgeAction(LISTING, FINDING, "   too short   ".slice(0, 12))).toEqual({ status: "error", code: "reasonTooShort" });
    expect(api.acknowledgeFinding).not.toHaveBeenCalled();
  });

  it("reads the API refusing a blocking finding as blocking (FR-022)", async () => {
    api.acknowledgeFinding.mockRejectedValue(new ApiError(422, { errors: { finding: ["A blocking finding cannot be acknowledged."] } }, "422"));

    expect(await acknowledgeAction(LISTING, FINDING, "I have checked this carefully.")).toEqual({ status: "error", code: "blocking" });
  });
});
