import { test, expect, type Page } from "@playwright/test";

/**
 * T063 — the brokerage screen and the pending-membership banner, end to end against the local
 * stack: Next.js → .NET API → Supabase. Requires the API to be running (`dotnet run` in
 * RealtorAI_BackEnd/RealtorAI.Api) as well as `supabase start`.
 *
 * Uses the seeded personas (supabase/seed.sql, password "password123") and only READS: no
 * test here presses approve or reject, because changing Agent B's pending membership would
 * break every other suite that relies on it. The decision flow itself is covered by the API
 * contract suite and the Server Action tests.
 */

async function signIn(page: Page, email: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

test.describe("brokerage", () => {
  test("an approved broker admin sees the settings form and the pending applicant", async ({ page }) => {
    await signIn(page, "agent.a@example.com");

    await page.getByRole("link", { name: "Brokerage" }).click();
    await page.waitForURL("**/dashboard/brokerage");

    await expect(page.getByRole("heading", { name: "Brokerage settings" })).toBeVisible();
    await expect(page.getByLabel("Brokerage name")).toHaveValue("Pacific Crest Realty");
    await expect(page.getByLabel("Default tone")).toHaveValue("professional");

    // Agent B is pending at Pacific Crest; each action names whom it acts on.
    const waiting = page.getByRole("region", { name: "Waiting for your decision" });
    await expect(waiting.getByRole("button", { name: /^Approve / }).first()).toBeVisible();
    await expect(waiting.getByRole("button", { name: /^Reject / }).first()).toBeVisible();

    // No pending banner for an approved member.
    await expect(page.getByText(/is pending approval/)).toHaveCount(0);
  });

  test("a pending agent sees the banner on the dashboard and no settings", async ({ page }) => {
    await signIn(page, "agent.b@example.com");

    await expect(page.getByText("Your membership at Pacific Crest Realty is pending approval")).toBeVisible();

    await page.getByRole("link", { name: "View status" }).click();
    await page.waitForURL("**/dashboard/brokerage");

    await expect(page.getByRole("heading", { name: "Waiting for Pacific Crest Realty to approve you" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Brokerage settings" })).toHaveCount(0);
  });

  test("an approved non-admin member sees the settings read-only", async ({ page }) => {
    await signIn(page, "agent.c@example.com");

    await page.goto("/dashboard/brokerage");

    await expect(page.getByRole("heading", { name: "Brokerage settings" })).toBeVisible();
    await expect(page.getByText("Only a broker admin can change these settings.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Save settings" })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Members" })).toHaveCount(0);
  });

  test("the platform administrator, who has no brokerage, is told so", async ({ page }) => {
    await signIn(page, "platform.admin@example.com");

    await page.goto("/dashboard/brokerage");

    await expect(page.getByRole("heading", { name: "You haven't declared a brokerage" })).toBeVisible();
  });
});
