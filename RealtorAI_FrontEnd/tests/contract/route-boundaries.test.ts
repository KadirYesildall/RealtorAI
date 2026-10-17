import { describe, it, expect } from "vitest";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

/**
 * T163 — every route segment under the dashboard has its own loading and error state.
 *
 * A segment without one falls back to the nearest parent's, which shows the wrong words (the
 * listings index's "couldn't load your listings" on a single listing) or, at the top, none at all.
 * A structural check, so a new page cannot ship without them.
 */

const DASHBOARD = path.resolve(__dirname, "../../app/dashboard");

function segmentsWithPages(directory: string): string[] {
  const here = existsSync(path.join(directory, "page.tsx")) ? [directory] : [];

  return readdirSync(directory)
    .map((entry) => path.join(directory, entry))
    .filter((entry) => statSync(entry).isDirectory())
    .flatMap(segmentsWithPages)
    .concat(here);
}

describe("dashboard route segments", () => {
  const segments = segmentsWithPages(DASHBOARD);

  it("are found", () => {
    expect(segments.length).toBeGreaterThan(0);
  });

  it.each(segments.map((segment) => [path.relative(DASHBOARD, segment) || "."]))("%s has loading.tsx and error.tsx", (relative) => {
    const segment = path.join(DASHBOARD, relative);

    expect(existsSync(path.join(segment, "loading.tsx"))).toBe(true);
    expect(existsSync(path.join(segment, "error.tsx"))).toBe(true);
  });

  it.each(segments.map((segment) => [path.relative(DASHBOARD, segment) || "."]))("%s error boundary is a Client Component", (relative) => {
    const source = readFileSync(path.join(DASHBOARD, relative, "error.tsx"), "utf8");

    expect(source.trimStart().startsWith('"use client"')).toBe(true);
  });
});
