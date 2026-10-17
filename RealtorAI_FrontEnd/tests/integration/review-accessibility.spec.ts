import { test, expect, type Page } from "@playwright/test";
import { contrastFailures } from "./helpers";

/**
 * T153 — the review screen and the audit trail in both themes, at mobile width, and by keyboard
 * alone (Story 5 scenario 6; constitution: dark mode is baseline, WCAG AA in both themes).
 *
 * Signs in as the seeded Agent E (verified test-only MLS), so the approval control has a blocker
 * to explain. Archives the listing it creates.
 */

const THEMES = ["light", "dark"] as const;

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.e@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

async function createListing(page: Page, street: string): Promise<string> {
  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("Condominium");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);

  return new URL(page.url()).searchParams.get("id") ?? "";
}

async function archive(page: Page, street: string) {
  await page.goto("/dashboard/listings");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
}

for (const theme of THEMES) {
  test(`the review screen and audit trail meet AA contrast — ${theme}`, async ({ page, context }, testInfo) => {
    await context.addCookies([{ name: "theme", value: theme, url: "http://localhost:3000" }]);
    await signIn(page);
    const street = `${theme} review check ${Date.now()}`;
    const id = await createListing(page, street);

    await page.goto(`/dashboard/listings/${id}`);
    await expect(page.getByRole("region", { name: "Approval" })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
    await page.screenshot({ path: testInfo.outputPath(`review-${theme}.png`), fullPage: true });
    expect(await contrastFailures(page), `review (${theme})`).toEqual([]);

    await page.getByRole("link", { name: "Audit trail" }).click();
    await expect(page.getByRole("heading", { name: "Audit trail" })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`audit-${theme}.png`), fullPage: true });
    expect(await contrastFailures(page), `audit (${theme})`).toEqual([]);

    await archive(page, street);
  });
}

test("the review screen fits a phone, and every control is reachable by keyboard", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page);
  const street = `mobile review check ${Date.now()}`;
  const id = await createListing(page, street);

  await page.goto(`/dashboard/listings/${id}`);
  await expect(page.getByRole("region", { name: "Approval" })).toBeVisible();

  // No sideways scrolling at phone width.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);

  // Keyboard alone reaches the controls of every section, the compliance check among them.
  const reached = new Set<string>();
  for (let step = 0; step < 120; step++) {
    await page.keyboard.press("Tab");
    const name = await page.evaluate(() => {
      const element = document.activeElement as HTMLElement | null;
      return element ? (element.getAttribute("aria-label") ?? element.innerText ?? element.getAttribute("id") ?? "").trim() : "";
    });
    if (name) reached.add(name);
    if (reached.has("Check compliance")) break;
  }

  expect([...reached]).toContain("Audit trail");
  expect([...reached]).toContain("Check compliance");

  // The approve control explains itself rather than sitting disabled and silent.
  await expect(page.getByRole("region", { name: "Approval" }).getByText("Not yet:").first()).toBeVisible();

  await page.setViewportSize({ width: 1280, height: 800 });
  await archive(page, street);
});
