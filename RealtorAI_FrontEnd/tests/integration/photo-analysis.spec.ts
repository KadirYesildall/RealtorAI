import { test, expect, type Page } from "@playwright/test";
import { png, randomColour } from "./helpers";

/**
 * T092 — Story 2 in the browser: start analysis, override results, reorder by drag, choose the
 * cover. Runs against the local stack (Next.js → .NET API → Supabase).
 *
 * The local stack has no enabled model — every registry entry stays disabled until its license
 * and evaluation are recorded (VIII.4, T159) — so starting analysis here meets the manual path,
 * and this walks Story 2 scenario 8: the agent classifies, captions, orders and picks the cover
 * by hand. Per-photo streaming over SignalR with a model answering is verified at the API by
 * PipelineStatusTests.Run_PushesPhotoProgressToTheSubscribedOwner, on a real hub connection.
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

test("analysis falls back to the manual path, and every result can be set by hand", async ({ page }) => {
  const street = `${Math.floor(1000 + Math.random() * 8999)} Analysis Lane`;
  await signIn(page);

  // ── A draft with two photos ──
  await page.goto("/dashboard/listings/new");
  await page.getByLabel("Street address").fill(street);
  await page.getByLabel("City").fill("Irvine");
  await page.getByLabel("State", { exact: true }).fill("CA");
  await page.getByLabel("ZIP code").fill("92618");
  await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
  await page.getByRole("button", { name: "Create draft" }).click();
  await page.waitForURL(/\?id=/);
  const draftUrl = page.url();

  await page.getByLabel("Choose photos").setInputFiles([
    { name: "first.png", mimeType: "image/png", buffer: png(64, 48, randomColour()) },
    { name: "second.png", mimeType: "image/png", buffer: png(64, 48, randomColour()) },
  ]);
  await expect(page.getByText("2 of 100 photos")).toBeVisible();

  // ── Starting analysis meets the manual path (SC-010) ──
  await page.getByRole("button", { name: "Analyze photos" }).click();
  await expect(page.getByText("Photo analysis isn't available right now")).toBeVisible();

  // ── Override: room type and caption, by hand ──
  await page.getByLabel("Room type for photo 1").selectOption("kitchen");
  await page.getByLabel("Caption for photo 1").fill("Kitchen with a breakfast bar");
  await page.getByLabel("Features for photo 1").fill("Breakfast bar, Pendant lights");
  await page.getByLabel("Features for photo 1").blur();
  await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 10_000 });

  // ── Cover ──
  await page.getByRole("button", { name: "Make photo 2 the cover" }).click();
  await expect(page.getByRole("button", { name: "Make photo 2 the cover" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 10_000 });

  // ── Reorder by drag, with the keyboard (dnd-kit's keyboard sensor) ──
  const handle = page.getByRole("button", { name: "Reorder photo 2" });
  await handle.focus();
  // Each key waits for dnd-kit to confirm the previous one: the handle is pressed while
  // dragging, and its live region announces the move — what a screen reader user hears.
  await page.keyboard.press("Space");
  await expect(handle).toHaveAttribute("aria-pressed", "true");
  await page.keyboard.press("ArrowLeft");
  // dnd-kit also announces the item as "over" itself when it is picked up; wait for the move
  // onto a different photo.
  await expect(page.getByText(/Draggable item (\S+) was moved over droppable area (?!\1\.)/)).toBeAttached();
  await page.keyboard.press("Space");
  // The moved photo is now first, so its handle reads "photo 1" — the order is applied locally
  // and saved.
  await expect(page.getByRole("button", { name: "Make photo 1 the cover" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 10_000 });

  // ── All of it persisted ──
  await page.goto(draftUrl);
  // The photo that was second is now first, and is the cover; the kitchen photo is second.
  await expect(page.getByRole("button", { name: "Make photo 1 the cover" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByLabel("Room type for photo 2")).toHaveValue("kitchen");
  await expect(page.getByLabel("Caption for photo 2")).toHaveValue("Kitchen with a breakfast bar");
  await expect(page.getByLabel("Features for photo 2")).toHaveValue("Breakfast bar, Pendant lights");

  // ── Remove a photo ──
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "Remove photo 1" }).click();
  await expect(page.getByText("1 of 100 photos")).toBeVisible();

  // ── Leave the persona's working view as found ──
  await page.goto("/dashboard/listings");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
  await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
});
