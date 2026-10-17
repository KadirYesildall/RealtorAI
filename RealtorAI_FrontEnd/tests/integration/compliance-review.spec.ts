import { test, expect, type Page } from "@playwright/test";

/**
 * T126 — Story 4 in the browser: seeded violations surface with their spans and rewrites, a
 * rewrite applies in one click, a warning is acknowledged with a reason, and a blocking finding
 * offers no way to dismiss it (FR-017, FR-022, FR-023).
 *
 * Runs on the local stack, where no model is enabled (T159) and CRMLS's profile is unverified
 * (T160): the lexicon and the deterministic checks run, the contextual review reports that it did
 * not, and the listing carries the fail-closed profile finding. That is the state an agent meets
 * today. The judge's own findings are verified at the API by ComplianceRunTests.
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

test("violations surface with spans and rewrites; warnings are acknowledged, blocking findings are not", async ({ page }) => {
  const street = `${Math.floor(1000 + Math.random() * 8999)} Compliance Way`;
  await signIn(page);

  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);

  // ── Copy with known violations, written by hand ──
  const content = page.getByRole("region", { name: "Listing content" });
  await content.getByRole("button", { name: "Write Description yourself" }).click();
  await content.getByLabel("Description text").fill("Perfect for young families. Walking distance to church. Call (949) 555-0123.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(content.getByText("Walking distance to church")).toBeVisible();

  // ── Run the check ──
  const review = page.getByRole("region", { name: "Compliance review" });
  await review.getByRole("button", { name: "Check compliance" }).click();
  const findings = review.getByRole("list", { name: "Findings" });
  await expect(findings.getByRole("listitem").filter({ hasText: "Perfect for young families" })).toBeVisible({ timeout: 20_000 });

  // Severity is stated in words, not colour alone, and each finding names its layer.
  const families = findings.getByRole("listitem").filter({ hasText: "Perfect for young families" });
  await expect(families.getByText("Blocking", { exact: true })).toBeVisible();
  await expect(families.getByText("Phrase list", { exact: true })).toBeVisible();
  await expect(families.getByRole("button", { name: "Acknowledge" })).toHaveCount(0);

  const phone = findings.getByRole("listitem").filter({ hasText: "(949) 555-0123" });
  await expect(phone.getByText("Blocking", { exact: true })).toBeVisible();

  // The fail-closed profile finding and the contextual review that did not run.
  await expect(findings.getByRole("listitem").filter({ hasText: "MLS rule profile" })).toBeVisible();
  await expect(findings.getByRole("listitem").filter({ hasText: "contextual" })).toBeVisible();

  // ── Jump to the span ──
  await families.getByRole("button", { name: "Show in text" }).click();
  await expect(content.locator("mark", { hasText: "Perfect for young families" })).toBeVisible();

  // ── One-click rewrite ──
  await families.getByRole("button", { name: "Use the suggested wording" }).click();
  await expect(findings.getByRole("listitem").filter({ hasText: "Perfect for young families" })).toHaveCount(0);
  await expect(content.getByText("Perfect for young families")).toHaveCount(0);

  // ── Acknowledge a warning, with a reason ──
  const church = findings.getByRole("listitem").filter({ hasText: "Walking distance to church" });
  await expect(church.getByText("Warning", { exact: true })).toBeVisible();
  await church.getByRole("button", { name: "Acknowledge" }).click();
  const dialog = page.getByRole("dialog", { name: "Acknowledge a warning" });
  await dialog.getByLabel("Reason").fill("The church is a landmark buyers ask about.");
  await dialog.getByRole("button", { name: "Acknowledge warning" }).click();
  await expect(findings.getByRole("listitem").filter({ hasText: "Walking distance to church" }).getByText("Acknowledged")).toBeVisible();

  // ── Leave the persona's working view as found ──
  await page.goto("/dashboard/listings");
  page.once("dialog", (confirm) => confirm.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
});
