import { test, expect } from "@playwright/test";
import { uniqueEmail, getConfirmationLink } from "./helpers";

async function registerAndLogin(page: import("@playwright/test").Page, name: string) {
  const email = uniqueEmail(name.replace(/\s+/g, "-").toLowerCase());
  await page.goto("/register");
  await page.getByLabel("Full name").fill(name);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByLabel(/Terms of Service/).check();
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page).toHaveURL(/\/register\/check-email/);

  const link = await getConfirmationLink(email);
  await page.goto(link);

  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/\/dashboard/);

  return email;
}

test.describe("User Story 3 — Protected, personalized dashboard", () => {
  test("redirects an unauthenticated visitor from /dashboard to /login", async ({ page }) => {
    await page.goto("/dashboard");
    await expect(page).toHaveURL(/\/login/);
  });

  test("each user's dashboard shows only their own name", async ({ browser }) => {
    const contextA = await browser.newContext();
    const pageA = await contextA.newPage();
    await registerAndLogin(pageA, "Agent Alpha");
    await expect(pageA.getByRole("heading", { name: /Agent Alpha/ })).toBeVisible();

    const contextB = await browser.newContext();
    const pageB = await contextB.newPage();
    await registerAndLogin(pageB, "Agent Beta");
    await expect(pageB.getByRole("heading", { name: /Agent Beta/ })).toBeVisible();
    await expect(pageB.getByText("Agent Alpha")).toHaveCount(0);

    await contextA.close();
    await contextB.close();
  });
});
