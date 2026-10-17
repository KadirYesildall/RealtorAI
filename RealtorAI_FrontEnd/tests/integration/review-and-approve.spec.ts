import { test, expect, type Page } from "@playwright/test";
import { png, randomColour } from "./helpers";

/**
 * T145 — Stories 1–5 end to end, to an approved listing, on one review screen.
 *
 * Signs in as the seeded Agent E, whose brokerage targets LOCAL-TEST: a verified, test-only MLS
 * profile. Against CRMLS — unverified until T160 — nothing can be approved, by design (research
 * R2), so this persona is the only way to reach "approved" locally.
 *
 * The AI layer: no model can be enabled on the local stack until T159, so the walkthrough takes
 * the manual path the product guarantees (SC-010) — facts entered, description written, the
 * contextual review reported as not having run. The AI steps with deterministic fixtures are
 * covered at the API, where the model is substituted (AiEnabledApiFactory): ContentRegenerateTests,
 * ComplianceRunTests, ApprovalBlockedTests. Recorded in research R37.
 *
 * Archives the listing it creates.
 */

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.e@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

test("a listing goes from draft to approved on one review screen, and an edit sends it back", async ({ page }) => {
  const street = `${Math.floor(1000 + Math.random() * 8999)} Approval Avenue`;
  await signIn(page);

  // ── Story 1: the draft ──
  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);

  // ── The review screen, reached from the listings index ──
  await page.goto("/dashboard/listings");
  await page.getByRole("link", { name: `Review ${street}` }).click();
  await page.waitForURL(/\/dashboard\/listings\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { level: 1, name: street })).toBeVisible();

  // ── Story 2: a photo, large enough for the profile ──
  await page.getByLabel("Choose photos").setInputFiles({ name: "front.png", mimeType: "image/png", buffer: png(800, 600, randomColour()) });
  await expect(page.getByText("1 of 100 photos")).toBeVisible();

  // ── Story 1: the required facts, with their provenance shown ──
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
  await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 10_000 });
  const facts = page.getByRole("region", { name: "Listing facts" });
  await expect(facts.getByRole("row").filter({ hasText: "Year built" }).getByText("Entered by you")).toBeVisible();

  // ── Story 3: the description, by hand ──
  const content = page.getByRole("region", { name: "Listing content" });
  await content.getByRole("button", { name: "Write Description yourself" }).click();
  await content.getByLabel("Description text").fill("A bright home on a quiet street, with a garden and a two-car garage.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(content.getByText("A bright home on a quiet street, with a garden and a two-car garage.")).toBeVisible();

  // ── Story 4: the check ──
  const review = page.getByRole("region", { name: "Compliance review" });
  await review.getByRole("button", { name: "Check compliance" }).click();
  await expect(review.getByRole("list", { name: "Findings" }).getByRole("listitem").filter({ hasText: "contextual" })).toBeVisible({ timeout: 20_000 });

  // ── Story 5: approve each output, then the listing ──
  const approval = page.getByRole("region", { name: "Approval" });
  await approval.getByRole("button", { name: "Approve Description for the MLS" }).click();
  await expect(approval.getByRole("listitem").filter({ hasText: "Description" }).getByText("Approved", { exact: true })).toBeVisible();

  await approval.getByRole("button", { name: "Approve the listing for the MLS" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Listing approved" })).toBeVisible();
  await expect(page.getByText("Approved", { exact: true }).first()).toBeVisible();

  // ── Story 5 scenario 4: an edit returns it to review ──
  await content.getByRole("button", { name: "Edit Description" }).click();
  await content.getByLabel("Description text").fill("A bright home on a quiet street, freshly painted.");
  await content.getByRole("button", { name: "Save" }).click();
  await expect(approval.getByRole("listitem").filter({ hasText: "Description" }).getByText("Not approved yet")).toBeVisible();
  await expect(approval.getByRole("button", { name: "Approve the listing for the MLS" })).toBeDisabled();

  // ── The audit trail answers what was approved ──
  await page.getByRole("link", { name: "Audit trail" }).click();
  await page.waitForURL(/\/audit$/);
  await expect(page.getByRole("row").filter({ hasText: "Approval granted" }).first()).toBeVisible();
  await expect(page.getByRole("row").filter({ hasText: "Approval revoked" }).first()).toBeVisible();

  // ── Leave the persona's working view as found ──
  await page.goto("/dashboard/listings");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
});
