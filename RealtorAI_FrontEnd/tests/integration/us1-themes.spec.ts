import { test, expect, type Page } from "@playwright/test";

/**
 * T083 — every US1 screen in both themes (constitution: dark mode is baseline, WCAG AA in both).
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

/** Text elements whose contrast against their effective background is below WCAG AA. */
async function contrastFailures(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const parse = (value: string): [number, number, number, number] | null => {
      const match = value.match(/rgba?\(([^)]+)\)/);
      if (!match) return null;
      const parts = match[1].split(/[\s,/]+/).filter(Boolean).map(Number);
      return [parts[0], parts[1], parts[2], parts[3] ?? 1];
    };

    const luminance = ([r, g, b]: number[]) => {
      const channel = (c: number) => {
        const s = c / 255;
        return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
      };
      return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    };

    const background = (element: Element | null): [number, number, number] => {
      const layers: [number, number, number, number][] = [];
      for (let node = element; node; node = node.parentElement) {
        const color = parse(getComputedStyle(node).backgroundColor);
        if (color && color[3] > 0) {
          layers.push(color);
          if (color[3] >= 1) break;
        }
      }
      let result: [number, number, number] = [255, 255, 255];
      for (const [r, g, b, a] of layers.reverse()) {
        result = [r * a + result[0] * (1 - a), g * a + result[1] * (1 - a), b * a + result[2] * (1 - a)];
      }
      return result;
    };

    const failures: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);

    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim();
      const element = node.parentElement;
      if (!text || !element) continue;

      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      if (style.visibility === "hidden" || style.display === "none" || rect.width === 0 || rect.height === 0) continue;
      if (element.closest("[aria-hidden='true'], .sr-only, button:disabled, [aria-disabled='true']")) continue;

      const foreground = parse(style.color);
      if (!foreground) continue;

      const back = background(element);
      const front: [number, number, number] = [
        foreground[0] * foreground[3] + back[0] * (1 - foreground[3]),
        foreground[1] * foreground[3] + back[1] * (1 - foreground[3]),
        foreground[2] * foreground[3] + back[2] * (1 - foreground[3]),
      ];

      const [light, dark] = [luminance(front), luminance(back)].sort((a, b) => b - a);
      const ratio = (light + 0.05) / (dark + 0.05);
      const size = parseFloat(style.fontSize);
      const large = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700);

      if (ratio < (large ? 3 : 4.5)) {
        failures.push(`${ratio.toFixed(2)}:1 "${text.slice(0, 40)}"`);
      }
    }

    return failures;
  });
}

for (const theme of THEMES) {
  test.describe(`US1 screens — ${theme} theme`, () => {
    test.beforeEach(async ({ context }) => {
      await context.addCookies([{ name: "theme", value: theme, url: "http://localhost:3000" }]);
    });

    test("listing index, wizard, draft editor and profile completion meet AA contrast", async ({ page }, testInfo) => {
      await signIn(page);

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

      await page.getByLabel("Street address").fill(`${theme} theme check`);
      await page.getByLabel("City").fill("Irvine");
      await page.getByLabel("State", { exact: true }).fill("CA");
      await page.getByLabel("ZIP code").fill("92618");
      await page.getByLabel("Property type").selectOption("Townhouse");
      await page.getByRole("button", { name: "Create draft" }).click();
      await page.waitForURL(/\?id=/);
      await page.getByLabel("Choose photos").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("x") });
      await expect(page.getByText("notes.txt was not accepted")).toBeVisible();
      await check("draft-editor", page.getByRole("region", { name: "Required before publishing" }));

      await page.goto("/dashboard/profile/complete");
      await check("profile-completion", page.getByRole("heading", { name: "Complete your profile" }));

      await page.goto("/dashboard/listings");
      page.once("dialog", (dialog) => dialog.accept());
      await page.getByRole("row").filter({ hasText: `${theme} theme check` }).getByRole("button", { name: "Archive" }).click();
      await expect(page.getByRole("row").filter({ hasText: `${theme} theme check` })).toHaveCount(0);
    });
  });
}
