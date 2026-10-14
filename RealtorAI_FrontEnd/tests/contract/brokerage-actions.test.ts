import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";
import { BrokerageSettingsFormSchema } from "@/lib/validation/brokerage";

/**
 * T063 — the brokerage Server Actions and their form schema.
 *
 * The API is mocked: these tests cover what the actions own — validation before the round
 * trip, what is sent, and how each API failure becomes a dictionary key. Whether a caller is
 * allowed to do any of it is the API's decision, tested against the real database in
 * RealtorAI.Api/tests/contract/BrokerageMembershipTests.cs.
 */

const api = vi.hoisted(() => ({
  getBrokerageSettings: vi.fn(),
  putBrokerageSettings: vi.fn(),
  decideMembership: vi.fn(),
}));

vi.mock("@/lib/api/brokerage", () => api);
vi.mock("next/cache", () => ({ refresh: vi.fn() }));

const { updateBrokerageSettingsAction, decideMembershipAction } = await import("@/app/actions/brokerage");

const BROKERAGE = "11110000-0000-4000-8000-000000000001";
const MEMBERSHIP = "22220000-0000-4000-8000-000000000002";

function settingsForm(overrides: Record<string, string> = {}): FormData {
  const form = new FormData();
  const values: Record<string, string> = {
    name: "Pacific Crest Realty",
    licenseNumber: "CA-DRE-01999001",
    state: "ca",
    requiredDisclaimers: "Pacific Crest Realty, DRE #01999001.\n\n  Second line.  \n",
    targetMlsId: "CRMLS",
    defaultTone: "warm",
    aiBudgetTokens: "150000",
    aiBudgetImages: "90",
    ...overrides,
  };

  for (const [key, value] of Object.entries(values)) {
    form.set(key, value);
  }

  return form;
}

function decisionForm(decision: string): FormData {
  const form = new FormData();
  form.set("decision", decision);
  return form;
}

beforeEach(() => {
  vi.clearAllMocks();
  api.getBrokerageSettings.mockResolvedValue({
    brokerageId: BROKERAGE,
    name: "Old",
    licenseNumber: "OLD",
    state: "CA",
    logoStorageKey: "11110000-0000-4000-8000-000000000001/logo.png",
    requiredDisclaimers: [],
    targetMlsId: "CRMLS",
    defaultTone: "professional",
    aiBudgetTokens: 200000,
    aiBudgetImages: 120,
  });
  api.putBrokerageSettings.mockResolvedValue({});
  api.decideMembership.mockResolvedValue({});
});

describe("BrokerageSettingsFormSchema", () => {
  it("normalises the state and splits disclaimers into trimmed, non-empty lines", () => {
    const parsed = BrokerageSettingsFormSchema.parse(Object.fromEntries(settingsForm()));

    expect(parsed.state).toBe("CA");
    expect(parsed.requiredDisclaimers).toEqual(["Pacific Crest Realty, DRE #01999001.", "Second line."]);
    expect(parsed.aiBudgetTokens).toBe(150000);
  });

  it("treats an empty target MLS as none", () => {
    const parsed = BrokerageSettingsFormSchema.parse(Object.fromEntries(settingsForm({ targetMlsId: "  " })));

    expect(parsed.targetMlsId).toBeNull();
  });

  it.each([
    ["state", "California", "stateFormat"],
    ["name", "  ", "nameRequired"],
    ["licenseNumber", "", "licenseRequired"],
    ["defaultTone", "sarcastic", "toneInvalid"],
    ["aiBudgetTokens", "0", "budgetPositive"],
    ["aiBudgetImages", "2.5", "budgetInteger"],
  ])("rejects %s=%j with the %s key", (field, value, key) => {
    const result = BrokerageSettingsFormSchema.safeParse(Object.fromEntries(settingsForm({ [field]: value })));

    expect(result.success).toBe(false);
    expect(result.error?.issues.find((issue) => issue.path[0] === field)?.message).toBe(key);
  });
});

describe("updateBrokerageSettingsAction", () => {
  it("sends the validated values, keeping the stored logo key unchanged", async () => {
    const result = await updateBrokerageSettingsAction(BROKERAGE, { status: "idle" }, settingsForm());

    expect(result).toEqual({ status: "saved" });
    expect(api.putBrokerageSettings).toHaveBeenCalledWith(
      BROKERAGE,
      expect.objectContaining({
        name: "Pacific Crest Realty",
        state: "CA",
        defaultTone: "warm",
        requiredDisclaimers: ["Pacific Crest Realty, DRE #01999001.", "Second line."],
        logoStorageKey: "11110000-0000-4000-8000-000000000001/logo.png",
      })
    );
  });

  it("returns field errors without calling the API when the form is invalid", async () => {
    const result = await updateBrokerageSettingsAction(BROKERAGE, { status: "idle" }, settingsForm({ state: "XYZ" }));

    expect(result).toEqual({ status: "error", code: "validation", fieldErrors: { state: "stateFormat" } });
    expect(api.putBrokerageSettings).not.toHaveBeenCalled();
  });

  it("rejects a brokerage id that is not a UUID before any request", async () => {
    const result = await updateBrokerageSettingsAction("../../admin", { status: "idle" }, settingsForm());

    expect(result).toEqual({ status: "error", code: "notFound" });
    expect(api.getBrokerageSettings).not.toHaveBeenCalled();
  });

  it.each([
    [401, "unauthenticated"],
    [403, "forbidden"],
    [404, "notFound"],
    [500, "unexpected"],
  ])("maps an API %i to %s", async (status, code) => {
    api.putBrokerageSettings.mockRejectedValue(new ApiError(status, undefined, "failed"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    const result = await updateBrokerageSettingsAction(BROKERAGE, { status: "idle" }, settingsForm());

    expect(result).toEqual({ status: "error", code });
  });

  it("points a server-side 422 at the fields the API named", async () => {
    api.putBrokerageSettings.mockRejectedValue(
      new ApiError(422, { errors: { targetMlsId: ["not an MLS"], logoStorageKey: ["read-only"] } }, "invalid")
    );

    const result = await updateBrokerageSettingsAction(BROKERAGE, { status: "idle" }, settingsForm());

    expect(result).toEqual({ status: "error", code: "validation", fieldErrors: { targetMlsId: "serverRejected" } });
  });
});

describe("decideMembershipAction", () => {
  it.each(["approved", "rejected"] as const)("submits %s", async (decision) => {
    const result = await decideMembershipAction(BROKERAGE, MEMBERSHIP, { status: "idle" }, decisionForm(decision));

    expect(result).toEqual({ status: "decided", decision });
    expect(api.decideMembership).toHaveBeenCalledWith(BROKERAGE, MEMBERSHIP, decision);
  });

  it.each(["pending", "revoked", ""])("refuses %j as a decision without calling the API", async (decision) => {
    const result = await decideMembershipAction(BROKERAGE, MEMBERSHIP, { status: "idle" }, decisionForm(decision));

    expect(result).toEqual({ status: "error", code: "validation" });
    expect(api.decideMembership).not.toHaveBeenCalled();
  });

  it("reports an already-decided request (409)", async () => {
    api.decideMembership.mockRejectedValue(new ApiError(409, undefined, "conflict"));

    const result = await decideMembershipAction(BROKERAGE, MEMBERSHIP, { status: "idle" }, decisionForm("approved"));

    expect(result).toEqual({ status: "error", code: "alreadyDecided" });
  });

  it("reports a 403 as forbidden", async () => {
    api.decideMembership.mockRejectedValue(new ApiError(403, undefined, "forbidden"));

    const result = await decideMembershipAction(BROKERAGE, MEMBERSHIP, { status: "idle" }, decisionForm("approved"));

    expect(result).toEqual({ status: "error", code: "forbidden" });
  });
});
