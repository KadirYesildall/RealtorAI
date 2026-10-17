import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import en from "@/lib/i18n/dictionaries/en.json";
import tr from "@/lib/i18n/dictionaries/tr.json";

/**
 * T165 — every user-facing string is a key in both dictionaries, and none is hard-coded.
 *
 * Parity is structural: the same keys, the same nesting, the same `{placeholders}` in each
 * translation, and no empty value. The hard-coded-text check reads every component and page for
 * text between JSX tags and for literal accessible names, placeholders, titles and alt text.
 * The only exception is the product's name in the logo, which is a brand, not copy.
 */

type Tree = { [key: string]: Tree | string | Tree[] | string[] };

function flatten(tree: unknown, prefix = ""): Map<string, string> {
  const entries = new Map<string, string>();

  if (typeof tree === "string") {
    entries.set(prefix, tree);
  } else if (Array.isArray(tree)) {
    tree.forEach((item, index) => flatten(item, `${prefix}[${index}]`).forEach((value, key) => entries.set(key, value)));
  } else if (tree && typeof tree === "object") {
    for (const [key, value] of Object.entries(tree as Tree)) {
      flatten(value, prefix ? `${prefix}.${key}` : key).forEach((leaf, leafKey) => entries.set(leafKey, leaf));
    }
  }

  return entries;
}

const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

const english = flatten(en);
const turkish = flatten(tr);

describe("dictionary parity", () => {
  it("has the same keys in English and Turkish", () => {
    const missingInTurkish = [...english.keys()].filter((key) => !turkish.has(key));
    const missingInEnglish = [...turkish.keys()].filter((key) => !english.has(key));

    expect({ missingInTurkish, missingInEnglish }).toEqual({ missingInTurkish: [], missingInEnglish: [] });
  });

  it("uses the same placeholders in each translation", () => {
    const mismatched = [...english.entries()]
      .filter(([key, value]) => turkish.has(key) && placeholders(value).join(",") !== placeholders(turkish.get(key)!).join(","))
      .map(([key]) => key);

    expect(mismatched).toEqual([]);
  });

  it("has no empty value", () => {
    const empty = [...english.entries(), ...turkish.entries()].filter(([, value]) => value.trim() === "").map(([key]) => key);

    expect(empty).toEqual([]);
  });
});

describe("no hard-coded user-facing text", () => {
  const ROOT = path.resolve(__dirname, "../..");
  const ALLOWED = new Set(["Realtor", "AI"]);

  function sources(directory: string): string[] {
    return readdirSync(directory).flatMap((entry) => {
      const full = path.join(directory, entry);
      if (statSync(full).isDirectory()) return sources(full);
      return full.endsWith(".tsx") ? [full] : [];
    });
  }

  const files = [...sources(path.join(ROOT, "app")), ...sources(path.join(ROOT, "components"))];

  it.each(files.map((file) => [path.relative(ROOT, file)]))("%s", (relative) => {
    const source = readFileSync(path.join(ROOT, relative), "utf8")
      .replace(/\/\*[\s\S]*?\*\//g, "")
      .replace(/(^|[^:"'`])\/\/[^\n]*/g, "$1");

    const textBetweenTags = [...source.matchAll(/>([^<>{}]*[A-Za-z]{2,}[^<>{}]*)</g)]
      .map((match) => match[1].trim())
      // TypeScript generics and arrow bodies look like this too; JSX text has none of these.
      .filter((text) => text.length > 0 && !/[;(){}=]|=>|&&|\|\||\bconst\b|\breturn\b|\bPromise\b|VariantProps/.test(text))
      .filter((text) => !ALLOWED.has(text));

    const literalAttributes = [...source.matchAll(/\b(?:aria-label|placeholder|title|alt)="([^"]*[A-Za-z][^"]*)"/g)].map((match) => match[0]);

    expect([...textBetweenTags, ...literalAttributes]).toEqual([]);
  });
});
