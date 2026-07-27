import "server-only";
import en from "@/lib/i18n/dictionaries/en.json";
import tr from "@/lib/i18n/dictionaries/tr.json";

export const locales = ["en", "tr"] as const;
export type Locale = (typeof locales)[number];
export const defaultLocale: Locale = "en";

const dictionaries = { en, tr } satisfies Record<Locale, typeof en>;

export type Dictionary = typeof en;

export function isLocale(value: string | undefined | null): value is Locale {
  return !!value && (locales as readonly string[]).includes(value);
}

/** Server-only: dictionaries are bundled at build time, never fetched at request time. */
export async function getDictionary(locale: Locale): Promise<Dictionary> {
  return dictionaries[locale];
}
