import { describe, it, expect, vi } from "vitest";

// See auth-actions.test.ts for why next/headers is mocked here. The full authenticated
// "language actually changes" path needs a confirmed session (email confirmation via the
// local email testing inbox), which is covered by the Playwright integration test
// (tests/integration/language-preference.spec.ts) instead of duplicated here.
vi.mock("next/headers", () => ({
  cookies: async () => ({
    getAll: () => [],
    set: () => {},
  }),
}));

const { updateLanguagePreferenceAction } = await import("@/app/actions/profile");

describe("updateLanguagePreferenceAction", () => {
  it("rejects an unsupported language value", async () => {
    const result = await updateLanguagePreferenceAction("fr");
    expect(result.success).toBe(false);
  });

  it("requires an authenticated session", async () => {
    const result = await updateLanguagePreferenceAction("tr");
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(result.error).toBe("You must be signed in.");
    }
  });
});
