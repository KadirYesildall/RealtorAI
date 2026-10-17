import { test, expect, type Page } from "@playwright/test";
import { png, randomColour } from "./helpers";

/**
 * T166 — Slice A's two time budgets, measured end to end on the local stack.
 *
 * SC-001: from photo upload to an approved, launch-ready listing in under 15 minutes for a
 * 30-photo listing. Walked as the seeded Agent E (verified test-only MLS); with no model enabled
 * locally (T159) it takes the manual path, which is the slowest the product allows — so passing
 * here bounds the AI path too, apart from model time.
 *
 * SC-005: photo analysis of 30 photos in under 2 minutes at p90. It needs an enabled
 * `photo.analysis` model; until T159 enables one, starting analysis meets the manual path and
 * the measurement is skipped with that reason, never passed vacuously.
 *
 * Each measurement is attached to the report. Archives the listings it creates.
 */

const PHOTOS = 30;

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.e@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

async function draft(page: Page, street: string): Promise<string> {
  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);

  return new URL(page.url()).searchParams.get("id") ?? "";
}

function thirtyPhotos() {
  return Array.from({ length: PHOTOS }, (_, index) => ({
    name: `photo-${index + 1}.png`,
    mimeType: "image/png",
    buffer: png(800, 600, randomColour()),
  }));
}

async function archive(page: Page, street: string) {
  await page.goto("/dashboard/listings");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
}

test("SC-001: thirty photos to an approved listing in under 15 minutes", async ({ page }, testInfo) => {
  test.setTimeout(16 * 60_000);
  await signIn(page);
  const street = `${Math.floor(1000 + Math.random() * 8999)} Stopwatch Street`;
  const id = await draft(page, street);
  await page.goto(`/dashboard/listings/${id}`);
  // A full page load: the uploader must be hydrated before files are chosen, or the choice is lost.
  await page.waitForLoadState("networkidle");

  const started = Date.now();

  await page.getByLabel("Choose photos").setInputFiles(thirtyPhotos());
  await expect(page.getByText(`${PHOTOS} of 100 photos`)).toBeVisible({ timeout: 5 * 60_000 });

  const required = page.getByRole("region", { name: "Required before publishing" });
  for (const [label, value] of [
    ["Living area (sq ft)", "2840"],
    ["Lot size (sq ft)", "7200"],
    ["Year built", "1998"],
    ["Annual property tax (USD)", "14250"],
  ]) {
    await required.getByLabel(label).fill(value);
    await required.getByLabel(label).blur();
  }
  await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 30_000 });

  const content = page.getByRole("region", { name: "Listing content" });
  await content.getByRole("button", { name: "Write Description yourself" }).click();
  await content.getByLabel("Description text").fill("A bright home on a quiet street, with a garden and a two-car garage.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(content.getByText("A bright home on a quiet street, with a garden and a two-car garage.")).toBeVisible();

  const review = page.getByRole("region", { name: "Compliance review" });
  await review.getByRole("button", { name: "Check compliance" }).click();
  await expect(review.getByRole("list", { name: "Findings" }).getByRole("listitem").first()).toBeVisible({ timeout: 2 * 60_000 });

  const approval = page.getByRole("region", { name: "Approval" });
  await approval.getByRole("button", { name: "Approve Description for the MLS" }).click();
  await expect(approval.getByRole("listitem").filter({ hasText: "Description" }).getByText("Approved", { exact: true })).toBeVisible();
  await approval.getByRole("button", { name: "Approve the listing for the MLS" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Listing approved" })).toBeVisible({ timeout: 30_000 });

  const elapsed = (Date.now() - started) / 1000;
  await testInfo.attach("sc-001", { body: `Upload to approved, ${PHOTOS} photos: ${elapsed.toFixed(1)} s (budget 900 s)`, contentType: "text/plain" });
  expect(elapsed).toBeLessThan(15 * 60);

  await archive(page, street);
});

test("SC-005: analysis of thirty photos in under 2 minutes", async ({ page }, testInfo) => {
  test.setTimeout(6 * 60_000);
  await signIn(page);
  const street = `${Math.floor(1000 + Math.random() * 8999)} Analysis Avenue`;
  const id = await draft(page, street);

  await page.getByLabel("Choose photos").setInputFiles(thirtyPhotos());
  await expect(page.getByText(`${PHOTOS} of 100 photos`)).toBeVisible({ timeout: 5 * 60_000 });

  const started = Date.now();
  await page.getByRole("button", { name: "Analyze photos" }).click();

  const manual = page.getByText("Photo analysis isn't available right now");
  const running = page.getByRole("list", { name: "Analysis steps" });
  await expect(manual.or(running)).toBeVisible({ timeout: 30_000 });

  if (await manual.isVisible()) {
    await archive(page, street);
    test.skip(true, "SC-005 needs an enabled photo.analysis model; none is enabled until T159.");
  }

  // Settled when no step is still waiting or in progress.
  await expect(running.getByText(/^(Waiting|In progress)$/)).toHaveCount(0, { timeout: 2 * 60_000 });

  const elapsed = (Date.now() - started) / 1000;
  await testInfo.attach("sc-005", { body: `Analysis of ${PHOTOS} photos on listing ${id}: ${elapsed.toFixed(1)} s (budget 120 s)`, contentType: "text/plain" });
  expect(elapsed).toBeLessThan(120);

  await archive(page, street);
});
