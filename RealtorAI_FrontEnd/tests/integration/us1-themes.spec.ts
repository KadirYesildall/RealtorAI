import { test, expect, type Page } from "@playwright/test";
import { contrastFailures, png, randomColour } from "./helpers";

/**
 * T083 — every US1 screen in both themes (constitution: dark mode is baseline, WCAG AA in both),
 * extended with the US2 photo screens, the US3 content card (T115) and the US4 compliance review
 * and acknowledge dialog (T137).
 *
 * Two checks per screen and theme. A computed-contrast check over every text element that
 * renders, against the background actually painted behind it — the failure this catches is a
 * hard-coded colour that reads in one theme and vanishes in the other. And a full-page
 * screenshot, attached to the report, for the review the automated check cannot do.
 *
 * Uses the seeded Agent C (approved, profile complete). The draft it opens is created and then
 * archived here, so the persona's working view is left as found.
 */

const THEMES = ["light", "dark"] as const;

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.c@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

for (const theme of THEMES) {
  test.describe(`US1 screens — ${theme} theme`, () => {
    test.beforeEach(async ({ context }) => {
      await context.addCookies([{ name: "theme", value: theme, url: "http://localhost:3000" }]);
    });

    test("listing index, wizard, draft editor and profile completion meet AA contrast", async ({ page }, testInfo) => {
      await signIn(page);
      // Unique per run, so a listing left by an interrupted run is never mistaken for this one.
      const street = `${theme} theme check ${Date.now()}`;

      // `ready` is something only the loaded screen shows: a check made while loading.tsx is
      // still up would pass against a spinner and prove nothing about the screen itself.
      const check = async (name: string, ready: ReturnType<Page["getByRole"]>) => {
        await expect(ready).toBeVisible();
        await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
        // Written to the test's output folder as well as attached, so a passing run leaves the
        // screenshots for review too.
        const path = testInfo.outputPath(`${name}-${theme}.png`);
        await page.screenshot({ path, fullPage: true });
        await testInfo.attach(`${name}-${theme}`, { path, contentType: "image/png" });
        expect(await contrastFailures(page), `${name} (${theme})`).toEqual([]);
      };

      await page.goto("/dashboard/listings");
      await check("listings-index", page.getByRole("heading", { name: "Listings" }));

      await page.goto("/dashboard/listings/new");
      await page.getByRole("button", { name: "Create draft" }).click();
      await expect(page.getByText("Enter the street address.")).toBeVisible();
      await check("wizard-basics-with-errors", page.getByRole("button", { name: "Create draft" }));

      await page.getByLabel("Street address").fill(street);
      await page.getByLabel("City").fill("Irvine");
      await page.getByLabel("State", { exact: true }).fill("CA");
      await page.getByLabel("ZIP code").fill("92618");
      await page.getByLabel("Property type").selectOption("Townhouse");
      await page.getByRole("button", { name: "Create draft" }).click();
      await page.waitForURL(/\?id=/);
      await page.getByLabel("Choose photos").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("x") });
      await expect(page.getByText("notes.txt was not accepted")).toBeVisible();
      await check("draft-editor", page.getByRole("region", { name: "Required before publishing" }));

      // ── US2: the photo grid, the analysis card on the manual path, the declaration dialog ──
      await page.getByLabel("Choose photos").setInputFiles({ name: "front.png", mimeType: "image/png", buffer: png(64, 48, randomColour()) });
      await expect(page.getByLabel("Room type for photo 1")).toBeVisible();
      await page.getByRole("button", { name: "Analyze photos" }).click();
      await expect(page.getByText("Photo analysis isn't available right now")).toBeVisible();
      await check("photo-grid-and-analysis", page.getByRole("list", { name: "Listing photos" }));

      await page.getByRole("button", { name: "Declare alteration for photo 1" }).click();
      await check("altered-dialog", page.getByRole("dialog", { name: "Declare an alteration" }));
      await page.keyboard.press("Escape");

      // ── US3: the content card, on the manual path, with a draft written by hand ──
      await page.getByRole("button", { name: "Generate Headline", exact: true }).click();
      await expect(page.getByText("Generating content isn't available right now")).toBeVisible();
      await page.getByRole("button", { name: "Write Headline yourself" }).click();
      await page.getByLabel("Headline text").fill("Light-filled townhouse near the park");
      await page.getByRole("button", { name: "Save" }).click();
      await expect(page.getByText("Light-filled townhouse near the park")).toBeVisible();
      await check("listing-content", page.getByRole("region", { name: "Listing content" }));

      // ── US4: the compliance review with findings, and the acknowledge dialog ──
      await page.getByRole("button", { name: "Edit Headline" }).click();
      await page.getByLabel("Headline text").fill("Walking distance to church, perfect for young families");
      await page.getByRole("button", { name: "Save" }).click();
      const review = page.getByRole("region", { name: "Compliance review" });
      await review.getByRole("button", { name: "Check compliance" }).click();
      await expect(review.getByRole("list", { name: "Findings" }).getByRole("listitem").first()).toBeVisible({ timeout: 20_000 });
      await check("compliance-review", review.getByRole("list", { name: "Findings" }));

      await review.getByRole("listitem").filter({ hasText: "Walking distance to church" }).getByRole("button", { name: "Acknowledge" }).click();
      await check("acknowledge-dialog", page.getByRole("dialog", { name: "Acknowledge a warning" }));
      await page.keyboard.press("Escape");

      await page.goto("/dashboard/profile/complete");
      await check("profile-completion", page.getByRole("heading", { name: "Complete your profile" }));

      await page.goto("/dashboard/listings");
      page.once("dialog", (dialog) => dialog.accept());
      await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
      await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
    });
  });
}
