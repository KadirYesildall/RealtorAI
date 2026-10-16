import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  ListingBasicsSchema,
  ProfileCompletionSchema,
  publicRecordValueSchema,
  safeNextPath,
} from "@/lib/validation/listing";

/**
 * T079–T082 — the listing and profile-completion Server Actions and their schemas.
 *
 * The API is mocked: these cover what the actions own — validation before the round trip, what
 * is sent, and how each API answer (including FR-066's version conflict) becomes a state the UI
 * shows. Whether the caller may do any of it is the API's decision, tested against the real
 * database in RealtorAI.Api/tests/contract (CreateListingTests, ListingAutosaveTests,
 * FieldUpsertTests, ProfileCompleteTests).
 */

const api = vi.hoisted(() => ({
  createListing: vi.fn(),
  autosaveListing: vi.fn(),
  archiveListing: vi.fn(),
  upsertField: vi.fn(),
  completeProfile: vi.fn(),
  declareMembership: vi.fn(),
}));

const navigation = vi.hoisted(() => ({
  redirect: vi.fn((path: string) => {
    throw Object.assign(new Error(`NEXT_REDIRECT ${path}`), { path });
  }),
}));

vi.mock("@/lib/api/listings", () => api);
vi.mock("next/navigation", () => navigation);
vi.mock("next/cache", () => ({ refresh: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: vi.fn() }));
vi.mock("@/lib/supabase/server", () => ({ createClient: vi.fn() }));

const { createListingAction, autosaveListingAction, saveListingFieldAction, archiveListingAction } = await import(
  "@/app/actions/listing"
);
const { completeProfileAction } = await import("@/app/actions/profile");

const LISTING = "99990000-0000-4000-8000-000000000001";

const BASICS = {
  addressLine1: " 1420 Crestline Drive ",
  city: "Irvine",
  state: "ca",
  postalCode: "92618",
  propertyType: "SingleFamilyResidence",
  listPrice: "1485000",
  bedrooms: "4",
  bathrooms: "3.5",
  agentNotes: "",
} as const;

function form(values: Record<string, string>): FormData {
  const data = new FormData();

  for (const [key, value] of Object.entries(values)) {
    data.set(key, value);
  }

  return data;
}

async function redirectedTo(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (error) {
    return (error as { path: string }).path;
  }

  throw new Error("Expected a redirect");
}

beforeEach(() => {
  vi.clearAllMocks();
});

// ═════════════════════════════ Schemas ═════════════════════════════

describe("ListingBasicsSchema", () => {
  it("trims text, upper-cases the state, and reads empty numbers as not given", () => {
    const parsed = ListingBasicsSchema.parse({ ...BASICS, listPrice: "", bedrooms: "", bathrooms: "" });

    expect(parsed.addressLine1).toBe("1420 Crestline Drive");
    expect(parsed.state).toBe("CA");
    expect(parsed.listPrice).toBeNull();
    expect(parsed.bedrooms).toBeNull();
    expect(parsed.agentNotes).toBeNull();
  });

  it.each([
    ["bathrooms", "2.3", "bathroomsFormat"],
    ["bathrooms", "100", "bathroomsFormat"],
    ["bedrooms", "2.5", "bedroomsFormat"],
    ["listPrice", "100.125", "priceFormat"],
    ["listPrice", "-1", "priceFormat"],
    ["postalCode", "9261", "postalCodeFormat"],
    ["propertyType", "Castle", "propertyTypeInvalid"],
  ])("refuses %s = %s with %s", (field, value, message) => {
    const result = ListingBasicsSchema.safeParse({ ...BASICS, [field]: value });

    expect(result.success).toBe(false);
    expect(result.error?.issues.find((issue) => issue.path[0] === field)?.message).toBe(message);
  });
});

describe("publicRecordValueSchema", () => {
  it("bounds the year built by the current year", () => {
    const schema = publicRecordValueSchema("YearBuilt", 2026);

    expect(schema.safeParse("1998").success).toBe(true);
    expect(schema.safeParse("2030").success).toBe(false);
    expect(schema.safeParse("1998.5").success).toBe(false);
  });

  it("accepts a non-negative measurement and refuses anything else", () => {
    const schema = publicRecordValueSchema("LivingArea", 2026);

    expect(schema.safeParse("2840").success).toBe(true);
    expect(schema.safeParse("-1").success).toBe(false);
    expect(schema.safeParse("large").success).toBe(false);
  });
});

describe("ProfileCompletionSchema", () => {
  const base = {
    licenseNumber: "DRE-01234567",
    licenseState: "ca",
    declaresBrokerage: true,
    brokerageId: "",
    brokerageName: "Newcomer Realty",
    brokerageLicenseNumber: "CA-DRE-01",
    brokerageState: "ca",
  };

  it("needs a license and state for a new brokerage, but only a name to join one", () => {
    expect(ProfileCompletionSchema.safeParse({ ...base, brokerageLicenseNumber: "", brokerageState: "" }).success).toBe(false);
    expect(
      ProfileCompletionSchema.safeParse({
        ...base,
        brokerageId: "11110000-0000-4000-8000-000000000001",
        brokerageLicenseNumber: "",
        brokerageState: "",
      }).success
    ).toBe(true);
  });

  it("asks nothing about a brokerage from an agent who already has one", () => {
    const parsed = ProfileCompletionSchema.parse({
      ...base,
      declaresBrokerage: false,
      brokerageName: "",
      brokerageLicenseNumber: "",
      brokerageState: "",
    });

    expect(parsed.licenseState).toBe("CA");
  });
});

describe("safeNextPath", () => {
  it.each([
    ["/dashboard/listings/new", "/dashboard/listings/new"],
    ["/dashboard/brokerage", "/dashboard/brokerage"],
    ["https://evil.example/dashboard", "/dashboard/listings/new"],
    ["//evil.example", "/dashboard/listings/new"],
    ["/login", "/dashboard/listings/new"],
    [undefined, "/dashboard/listings/new"],
  ])("sends %s to %s", (input, expected) => {
    expect(safeNextPath(input)).toBe(expected);
  });
});

// ═════════════════════════════ Create ═════════════════════════════

describe("createListingAction", () => {
  it("sends the validated basics and goes to the new draft", async () => {
    api.createListing.mockResolvedValue({ id: LISTING });

    const path = await redirectedTo(createListingAction({ status: "idle" }, form(BASICS)));

    expect(path).toBe(`/dashboard/listings/new?id=${LISTING}`);
    expect(api.createListing).toHaveBeenCalledWith(
      expect.objectContaining({ addressLine1: "1420 Crestline Drive", state: "CA", listPrice: 1485000, bathrooms: 3.5 })
    );
  });

  it("points at the broken fields without calling the API", async () => {
    const state = await createListingAction({ status: "idle" }, form({ ...BASICS, city: "", postalCode: "x" }));

    expect(state).toEqual({
      status: "error",
      code: "validation",
      fieldErrors: { city: "cityRequired", postalCode: "postalCodeFormat" },
    });
    expect(api.createListing).not.toHaveBeenCalled();
  });

  it("routes an agent without a license to profile completion (FR-058)", async () => {
    api.createListing.mockRejectedValue(new ApiError(403, undefined, "403"));

    const path = await redirectedTo(createListingAction({ status: "idle" }, form(BASICS)));

    expect(path).toBe("/dashboard/profile/complete?next=/dashboard/listings/new");
  });
});

// ═════════════════════════════ Autosave ═════════════════════════════

describe("autosaveListingAction", () => {
  it("saves at the version given and reports the new one", async () => {
    api.autosaveListing.mockResolvedValue({ rowVersion: 4 });

    const result = await autosaveListingAction(LISTING, 3, BASICS);

    expect(result).toEqual({ status: "saved", rowVersion: 4 });
    expect(api.autosaveListing).toHaveBeenCalledWith(LISTING, expect.objectContaining({ city: "Irvine" }), 3);
  });

  it("reports a version conflict with the server's current state (FR-066)", async () => {
    const current = { id: LISTING, rowVersion: 5, addressLine1: "Saved elsewhere" };
    api.autosaveListing.mockRejectedValue(
      new ApiError(409, { type: "https://realtorai.app/problems/version-conflict", currentRowVersion: 5, current }, "409")
    );

    expect(await autosaveListingAction(LISTING, 3, BASICS)).toEqual({ status: "conflict", current });
  });

  it("refuses a listing id that is not one", async () => {
    expect(await autosaveListingAction("../admin", 1, BASICS)).toEqual({ status: "error", code: "notFound" });
    expect(api.autosaveListing).not.toHaveBeenCalled();
  });
});

// ═════════════════════════════ Required fields ═════════════════════════════

describe("saveListingFieldAction", () => {
  it("always saves a hand-entered public-record field as agent_entered (FR-007)", async () => {
    api.upsertField.mockResolvedValue({ id: "f", rowVersion: 1 });

    await saveListingFieldAction(LISTING, "LivingArea", " 2840 ", 0);

    expect(api.upsertField).toHaveBeenCalledWith(
      LISTING,
      { resoFieldKey: "LivingArea", value: 2840, provenance: "agent_entered" },
      0
    );
  });

  it("refuses a key outside the public-record set", async () => {
    // @ts-expect-error — a key the type does not allow, as a crafted request would send.
    expect(await saveListingFieldAction(LISTING, "InteriorFeatures", "x", 0)).toEqual({ status: "error", code: "notFound" });
  });

  it("reports an implausible year without calling the API", async () => {
    const result = await saveListingFieldAction(LISTING, "YearBuilt", "19988", 0);

    expect(result).toEqual({ status: "error", code: "fieldInvalid", message: "yearFormat" });
    expect(api.upsertField).not.toHaveBeenCalled();
  });
});

// ═════════════════════════════ Archive ═════════════════════════════

describe("archiveListingAction", () => {
  it("archives through the API", async () => {
    api.archiveListing.mockResolvedValue(undefined);

    expect(await archiveListingAction(LISTING)).toEqual({ status: "idle" });
    expect(api.archiveListing).toHaveBeenCalledWith(LISTING);
  });

  it("says so when the API refuses", async () => {
    api.archiveListing.mockRejectedValue(new ApiError(403, undefined, "403"));

    expect(await archiveListingAction(LISTING)).toEqual({ status: "error", code: "forbidden" });
  });
});

// ═════════════════════════════ Profile completion ═════════════════════════════

describe("completeProfileAction", () => {
  const values = {
    licenseNumber: "DRE-01234567",
    licenseState: "ca",
    declaresBrokerage: "true",
    brokerageId: "",
    brokerageName: "Newcomer Realty",
    brokerageLicenseNumber: "CA-DRE-01",
    brokerageState: "ca",
  };

  it("records the license, declares the brokerage, and returns the agent to where they were going", async () => {
    api.completeProfile.mockResolvedValue({});
    api.declareMembership.mockResolvedValue({});

    const path = await redirectedTo(completeProfileAction("/dashboard/listings/new", { status: "idle" }, form(values)));

    expect(path).toBe("/dashboard/listings/new");
    expect(api.completeProfile).toHaveBeenCalledWith({ licenseNumber: "DRE-01234567", licenseState: "CA" });
    expect(api.declareMembership).toHaveBeenCalledWith({
      brokerageId: null,
      brokerageName: "Newcomer Realty",
      brokerageLicenseNumber: "CA-DRE-01",
      brokerageState: "CA",
    });
  });

  it("does not declare a brokerage for an agent who already has one", async () => {
    api.completeProfile.mockResolvedValue({});

    await redirectedTo(completeProfileAction("/dashboard", { status: "idle" }, form({ ...values, declaresBrokerage: "false" })));

    expect(api.declareMembership).not.toHaveBeenCalled();
  });

  it("never redirects outside the dashboard", async () => {
    api.completeProfile.mockResolvedValue({});
    api.declareMembership.mockResolvedValue({});

    const path = await redirectedTo(completeProfileAction("https://evil.example", { status: "idle" }, form(values)));

    expect(path).toBe("/dashboard/listings/new");
  });

  it("maps the API's field rejections onto the form", async () => {
    api.completeProfile.mockRejectedValue(new ApiError(422, { errors: { licenseState: ["bad"] } }, "422"));

    const state = await completeProfileAction("/dashboard", { status: "idle" }, form(values));

    expect(state).toEqual({ status: "error", code: "validation", fieldErrors: { licenseState: "serverRejected" } });
  });
});
