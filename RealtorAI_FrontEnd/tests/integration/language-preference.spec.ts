import { test, expect } from "@playwright/test";
import { uniqueEmail, getConfirmationLink } from "./helpers";

test.describe("User Story 4 — Language preference", () => {
  test("defaults to English for an unrecognized browser locale", async ({ browser }) => {
    const context = await browser.newContext({ locale: "de-DE" });
    const page = await context.newPage();
    await page.goto("/login");
    await expect(
      page.getByRole("heading", { name: "Sign in to RealtorAI" })
    ).toBeVisible();
    await context.close();
  });

  test("defaults to Turkish for a tr-TR browser locale", async ({ browser }) => {
    const context = await browser.newContext({ locale: "tr-TR" });
    const page = await context.newPage();
    await page.goto("/login");
    await expect(
      page.getByRole("heading", { name: "RealtorAI'a giriş yapın" })
    ).toBeVisible();
    await context.close();
  });

  test("switching language on the dashboard persists across a fresh login session", async ({
    browser,
  }) => {
    const email = uniqueEmail("lang");
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto("/register");
    await page.getByLabel("Full name").fill("Dil Test");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByLabel(/Terms of Service/).check();
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page).toHaveURL(/\/register\/check-email/);

    await page.goto(await getConfirmationLink(email));

    await page.goto("/login");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    await page.getByLabel("Language").selectOption("tr");
    await expect(page.getByText("Panel")).toBeVisible();
    await context.close();

    // Fresh session (new context = no cookies, English browser locale) — the login page
    // itself is pre-auth, so it still follows the browser's locale (English, by design: see
    // research.md §4, only post-auth pages use the saved profile preference). What matters
    // for spec US4 AC3 is that the *dashboard*, once logged in, uses the saved Turkish
    // preference rather than this fresh session's English browser default.
    const freshContext = await browser.newContext({ locale: "en-US" });
    const freshPage = await freshContext.newPage();
    await freshPage.goto("/login");
    await expect(
      freshPage.getByRole("heading", { name: "Sign in to RealtorAI" })
    ).toBeVisible();
    await freshPage.getByLabel("Email").fill(email);
    await freshPage.getByLabel("Password", { exact: true }).fill("password123");
    await freshPage.getByRole("button", { name: "Sign in" }).click();
    await expect(freshPage).toHaveURL(/\/dashboard/);
    await expect(freshPage.getByText("Panel")).toBeVisible();
    await freshContext.close();
  });
});
