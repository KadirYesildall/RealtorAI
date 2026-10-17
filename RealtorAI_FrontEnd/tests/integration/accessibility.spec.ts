import { test, expect, type Page } from "@playwright/test";
import { contrastFailures, png, randomColour } from "./helpers";

/**
 * T164 — the accessibility suite over every screen this feature added: every control labelled,
 * every image described, ids unique, one page heading, every control reachable by keyboard, and
 * WCAG AA contrast in both themes (constitution Section E).
 *
 * No accessibility engine is installed and none is added for this: the checks run in the page,
 * against the rendered DOM, with the same contrast measurement the theme suites use. The
 * accessible names the existing e2e suites pin are the ones used here, so they stay pinned.
 *
 * Signs in as the seeded Agent E (broker admin of a brokerage on the verified test-only MLS).
 * Archives the listing it creates.
 */

const THEMES = ["light", "dark"] as const;

type Audit = { unnamed: string[]; images: string[]; duplicateIds: string[]; h1: number; unreachable: string[] };

/** Structural checks, in the page. */
async function audit(page: Page): Promise<Omit<Audit, "unreachable">> {
  return page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = (element as HTMLElement).getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    };

    const nameOf = (element: Element): string => {
      const labelledBy = element.getAttribute("aria-labelledby");
      if (labelledBy) return labelledBy.split(/\s+/).map((id) => document.getElementById(id)?.textContent ?? "").join(" ").trim();
      const aria = element.getAttribute("aria-label");
      if (aria) return aria.trim();
      if (element.id) {
        const label = document.querySelector(`label[for="${CSS.escape(element.id)}"]`);
        if (label?.textContent?.trim()) return label.textContent.trim();
      }
      const wrapping = element.closest("label");
      if (wrapping?.textContent?.trim()) return wrapping.textContent.trim();
      return ((element as HTMLElement).innerText ?? element.getAttribute("title") ?? "").trim();
    };

    const controls = [...document.querySelectorAll("button, a[href], input:not([type=hidden]), select, textarea, [role=button]")].filter(visible);
    const unnamed = controls.filter((element) => nameOf(element) === "").map((element) => element.outerHTML.slice(0, 120));

    const images = [...document.querySelectorAll("img")].filter((image) => !image.hasAttribute("alt")).map((image) => image.outerHTML.slice(0, 120));

    const ids = [...document.querySelectorAll("[id]")].map((element) => element.id);
    const duplicateIds = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))];

    return { unnamed, images, duplicateIds, h1: document.querySelectorAll("h1").length };
  });
}

/** Tabs through the page and returns the visible, enabled controls focus never reached. */
async function unreachable(page: Page): Promise<string[]> {
  await page.evaluate(() => {
    let index = 0;
    for (const element of document.querySelectorAll("button, a[href], input:not([type=hidden]), select, textarea")) {
      element.setAttribute("data-a11y-index", String(index++));
    }
    (document.activeElement as HTMLElement | null)?.blur();
    window.scrollTo(0, 0);
  });

  const total = await page.locator("[data-a11y-index]").count();
  const reached = new Set<string>();

  for (let step = 0; step < total * 2 + 20; step++) {
    await page.keyboard.press("Tab");
    const index = await page.evaluate(() => document.activeElement?.getAttribute("data-a11y-index") ?? null);
    if (index !== null) reached.add(index);
  }

  return page.evaluate((seen) => {
    const visited = new Set(seen);
    return [...document.querySelectorAll("[data-a11y-index]")]
      .filter((element) => {
        const rect = (element as HTMLElement).getBoundingClientRect();
        const disabled = (element as HTMLButtonElement).disabled || element.getAttribute("aria-disabled") === "true";
        // Radio groups take one tab stop for the group; arrow keys move within it.
        const radio = element instanceof HTMLInputElement && element.type === "radio";
        return rect.width > 0 && rect.height > 0 && !disabled && !radio && !visited.has(element.getAttribute("data-a11y-index") ?? "");
      })
      .map((element) => (element as HTMLElement).outerHTML.slice(0, 120));
  }, [...reached]);
}

async function signIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill("agent.e@example.com");
  await page.getByLabel("Password", { exact: true }).fill("password123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL("**/dashboard");
}

for (const theme of THEMES) {
  test(`every new screen is labelled, keyboard-reachable and AA in the ${theme} theme`, async ({ page, context }, testInfo) => {
    test.setTimeout(120_000);
    await context.addCookies([{ name: "theme", value: theme, url: "http://localhost:3000" }]);
    await signIn(page);
    const street = `${theme} a11y check ${Date.now()}`;

    const check = async (name: string, ready: ReturnType<Page["getByRole"]>) => {
      await expect(ready).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
      await page.screenshot({ path: testInfo.outputPath(`${name}-${theme}.png`), fullPage: true });

      const structure = await audit(page);
      expect(structure.unnamed, `${name}: unnamed controls`).toEqual([]);
      expect(structure.images, `${name}: images without alt`).toEqual([]);
      expect(structure.duplicateIds, `${name}: duplicate ids`).toEqual([]);
      expect(structure.h1, `${name}: one page heading`).toBe(1);
      expect(await contrastFailures(page), `${name}: contrast`).toEqual([]);
      expect(await unreachable(page), `${name}: keyboard`).toEqual([]);
    };

    await page.goto("/dashboard/listings");
    await check("listings", page.getByRole("heading", { name: "Listings" }));

    await page.goto("/dashboard/listings/new");
    await check("new-listing", page.getByRole("button", { name: "Create draft" }));

    await page.getByLabel("Street address").fill(street);
    await page.getByLabel("City").fill("Irvine");
    await page.getByLabel("State", { exact: true }).fill("CA");
    await page.getByLabel("ZIP code").fill("92618");
    await page.getByLabel("Property type").selectOption("Townhouse");
    await page.getByRole("button", { name: "Create draft" }).click();
    await page.waitForURL(/\?id=/);
    const id = new URL(page.url()).searchParams.get("id");

    await page.getByLabel("Choose photos").setInputFiles({ name: "front.png", mimeType: "image/png", buffer: png(800, 600, randomColour()) });
    await expect(page.getByLabel("Room type for photo 1")).toBeVisible();
    await check("draft-editor", page.getByRole("list", { name: "Listing photos" }));

    await page.goto(`/dashboard/listings/${id}`);
    await check("review", page.getByRole("region", { name: "Approval" }));

    await page.goto(`/dashboard/listings/${id}/audit`);
    await check("audit", page.getByRole("heading", { name: "Audit trail" }));

    await page.goto("/dashboard/brokerage");
    await check("brokerage", page.getByRole("heading", { name: "Brokerage settings" }));

    await page.goto("/dashboard/listings");
    page.once("dialog", (dialog) => dialog.accept());
    await page.getByRole("row").filter({ hasText: street }).getByRole("button", { name: "Archive" }).click();
    await expect(page.getByRole("row").filter({ hasText: street })).toHaveCount(0);
  });
}
