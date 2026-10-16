import { deflateSync } from "node:zlib";
import { test, expect, type Page } from "@playwright/test";

/**
 * T072 — Story 1 end to end: enter the wizard, create a draft, upload photos, autosave an edit,
 * navigate away, and resume to find everything as it was left. Runs against the local stack:
 * Next.js → .NET API (`dotnet run`, with SUPABASE_SECRET_KEY set) → Supabase.
 *
 * Signs in as the seeded Agent C (approved, profile complete). Every listing it creates is
 * archived at the end, so the persona's working view stays empty between runs — archived
 * listings are kept by design (FR-067) but hidden from that view.
 */

async function signIn(page: Page, email: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

/** A solid-colour PNG, encoded here so every run uploads bytes no earlier run has. */
function png(width: number, height: number, rgb: [number, number, number]): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc32 = (bytes: Buffer) => {
    let c = 0xffffffff;
    for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };

  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // truecolour RGB

  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: width }, () => rgb).flat())]);
  const pixels = Buffer.concat(Array.from({ length: height }, () => row));

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(pixels)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

function randomColour(): [number, number, number] {
  return [0, 0, 0].map(() => Math.floor(Math.random() * 256)) as [number, number, number];
}

test.describe("listing wizard", () => {
  test("creates a draft, autosaves, and resumes it intact after navigating away", async ({ page }) => {
    const street = `${Math.floor(1000 + Math.random() * 8999)} Crestline Drive`;

    await signIn(page, "agent.c@example.com");

    // ── Entry ──
    await page.getByRole("link", { name: "Listings" }).click();
    await page.waitForURL("**/dashboard/listings");
    await page.getByRole("link", { name: "New listing" }).click();
    await page.waitForURL("**/dashboard/listings/new");

    // ── Basics ──
    await page.getByLabel("Street address").fill(street);
    await page.getByLabel("City").fill("Irvine");
    await page.getByLabel("State", { exact: true }).fill("CA");
    await page.getByLabel("ZIP code").fill("92618");
    await page.getByLabel("Property type").selectOption("SingleFamilyResidence");
    await page.getByLabel("List price").fill("1485000");
    await page.getByLabel("Bedrooms").fill("4");
    await page.getByLabel("Bathrooms").fill("3.5");
    await page.getByRole("button", { name: "Create draft" }).click();

    await page.waitForURL(/\/dashboard\/listings\/new\?id=[0-9a-f-]{36}/);
    const draftUrl = page.url();

    // FR-008: with no public-record source, these are empty and visibly required.
    const required = page.getByRole("region", { name: "Required before publishing" });
    await expect(required.getByLabel("Living area (sq ft)")).toHaveValue("");
    await expect(required.getByLabel("Year built")).toHaveValue("");

    // ── Upload ──
    await page.getByLabel("Choose photos").setInputFiles([
      { name: "front.png", mimeType: "image/png", buffer: png(64, 48, randomColour()) },
      { name: "kitchen.png", mimeType: "image/png", buffer: png(64, 48, randomColour()) },
      { name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("not a photo") },
    ]);

    const uploads = page.getByRole("region", { name: "Photos" });
    await expect(uploads.getByText("front.png")).toBeVisible();
    await expect(uploads.getByText("kitchen.png")).toBeVisible();
    // Story 1 scenario 2: the bad file is named with its reason; the others still land.
    await expect(uploads.getByRole("listitem").filter({ hasText: "notes.txt" })).toContainText("not accepted");
    await expect(page.getByText("2 of 100 photos")).toBeVisible();

    // ── Autosave: no save button, the edit persists on its own ──
    const price = page.getByLabel("List price");
    await price.fill("1499000");
    await page.getByLabel("Year built").fill("1998");
    await expect(page.getByText("All changes saved")).toBeVisible({ timeout: 10_000 });

    // ── Navigate away ──
    await page.getByRole("link", { name: "Dashboard" }).click();
    await page.waitForURL("**/dashboard");

    // ── Resume from the listing index ──
    await page.getByRole("link", { name: "Listings" }).click();
    const row = page.getByRole("row").filter({ hasText: street });
    await expect(row).toContainText("Draft");
    await row.getByRole("link", { name: "Continue" }).click();

    await page.waitForURL(draftUrl);
    await expect(page.getByLabel("Street address")).toHaveValue(street);
    await expect(page.getByLabel("List price")).toHaveValue("1499000");
    await expect(page.getByLabel("Year built")).toHaveValue("1998");
    await expect(page.getByText("2 of 100 photos")).toBeVisible();

    // ── Archive, so the persona's working view is left as it was found ──
    await page.goto("/dashboard/listings");
    const created = page.getByRole("row").filter({ hasText: street });
    page.once("dialog", (dialog) => dialog.accept());
    await created.getByRole("button", { name: "Archive" }).click();
    await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);

    await page.getByLabel("Show archived").check();
    await expect(page.getByRole("row").filter({ hasText: street })).toContainText("Archived");
  });

  test("an agent without a license is sent to profile completion first", async ({ page }) => {
    await page.goto("/register");
    const email = `pw-wizard-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`;
    const { getConfirmationLink } = await import("./helpers");

    await page.getByLabel("Full name").fill("Wizard Newcomer");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password", { exact: true }).fill("password123");
    await page.getByLabel(/Terms of Service/).check();
    await page.getByRole("button", { name: "Create account" }).click();
    await page.waitForURL(/\/register\/check-email/);
    await page.goto(await getConfirmationLink(email));

    await signIn(page, email);

    // Story 1 scenario 7: routed through profile completion, then returned to creation.
    await page.goto("/dashboard/listings/new");
    await page.waitForURL(/\/dashboard\/profile\/complete/);

    await page.getByLabel("License number", { exact: true }).fill("DRE-09876543");
    await page.getByLabel("License state", { exact: true }).fill("CA");
    await page.getByLabel("Brokerage name").fill(`Newcomer Realty ${Date.now()}`);
    await page.getByLabel("Brokerage license number").fill("CA-DRE-01234567");
    await page.getByLabel("Brokerage state").fill("CA");
    await page.getByRole("button", { name: "Save and continue" }).click();

    await page.waitForURL("**/dashboard/listings/new");
    await expect(page.getByRole("heading", { name: "New listing" })).toBeVisible();
  });
});
