import { test, expect, type Page } from "@playwright/test";

/**
 * Story 3 in the browser, on the manual path (SC-010): generating says why it is unavailable,
 * and every kind of content can be written, edited and kept by hand.
 *
 * The local stack has no enabled model — every registry entry stays disabled until its license
 * and evaluation are recorded (VIII.4, T159) — and the seeded CRMLS profile has no verified
 * limits (T160), so this is the path an agent meets today. Generation itself, variants, limits and
 * grounding are verified at the API by ContentRegenerateTests, RemarksLimitTests and
 * GroundingTests; the highlight's segmentation by content-actions.test.ts.
 *
 * Signs in as the seeded Agent C and archives the listing it creates.
 */

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.c@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

test("content can be written and edited by hand when generation is unavailable", async ({ page }) => {
  const street = `${Math.floor(1000 + Math.random() * 8999)} Content Court`;
  await signIn(page);

  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);
  const draftUrl = page.url();

  const content = page.getByRole("region", { name: "Listing content" });

  // ── Generating meets the manual path ──
  await content.getByLabel("Tone").selectOption("warm");
  await content.getByRole("button", { name: "Generate Description", exact: true }).click();
  await expect(content.getByText("Generating content isn't available right now")).toBeVisible();

  // ── Written by hand ──
  await content.getByRole("button", { name: "Write Description yourself" }).click();
  await content.getByLabel("Description text").fill("A bright home on a quiet street, with a garden made for slow mornings.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(content.getByText("A bright home on a quiet street, with a garden made for slow mornings.")).toBeVisible();

  // ── Edited inline ──
  await content.getByRole("button", { name: "Edit Description" }).click();
  await content.getByLabel("Description text").fill("A bright home on a quiet street.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(content.getByText("A bright home on a quiet street.", { exact: true })).toBeVisible();
  await expect(content.getByText("32 characters")).toBeVisible();

  // ── Kept ──
  await page.goto(draftUrl);
  await expect(page.getByRole("region", { name: "Listing content" }).getByText("A bright home on a quiet street.", { exact: true })).toBeVisible();

  // ── Leave the persona's working view as found ──
  await page.goto("/dashboard/listings");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
});
