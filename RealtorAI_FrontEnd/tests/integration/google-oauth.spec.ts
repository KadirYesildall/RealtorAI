import { test, expect } from "@playwright/test";

// Full Google consent completion requires real Google Cloud OAuth credentials (see
// quickstart.md's Prerequisites) and isn't something this suite can automate — that step is
// a manual verification once credentials are configured. What IS covered here, against the
// real local Supabase stack, is everything on this app's side of that boundary.
test.describe("User Story 2 — Google OAuth sign-in", () => {
  test('"Continue with Google" is disabled until consent is checked, then navigates to Supabase\'s Google authorize URL', async ({
    page,
  }) => {
    await page.goto("/login");
    const googleButton = page.getByRole("button", { name: "Continue with Google" });
    await expect(googleButton).toBeDisabled();

    await page.getByLabel(/Terms of Service/).check();
    await expect(googleButton).toBeEnabled();

    const navigation = page.waitForURL(/auth\/v1\/authorize\?.*provider=google/, {
      timeout: 10_000,
    });
    await googleButton.click();
    await navigation;
  });

  test("a denied/cancelled Google consent shows a clear message on the login page", async ({
    page,
  }) => {
    // Simulates what app/auth/callback/route.ts does when Google reports `error=access_denied`
    // (contracts/oauth-callback.md) — the part of the cancellation flow this app controls.
    await page.goto("/auth/callback?error=access_denied");
    await expect(page).toHaveURL(/\/login\?error=oauth_cancelled/);
    await expect(page.getByText("Sign-in was cancelled.")).toBeVisible();
  });
});
