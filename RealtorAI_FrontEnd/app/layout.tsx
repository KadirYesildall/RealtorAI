import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";
import { getLocale } from "@/lib/i18n/getLocale";
import { getThemePreference } from "@/lib/theme/getThemePreference";
import { themeAttribute } from "@/lib/theme/theme";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "RealtorAI",
  description:
    "Generate listing copy, Reels scripts, and LinkedIn posts from your property photos.",
};

export default async function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  const [locale, themePreference] = await Promise.all([
    getLocale(),
    getThemePreference(),
  ]);

  // "system" intentionally renders no attribute: globals.css then resolves the palette from
  // prefers-color-scheme, so the first paint is already correct with no theme-flash.
  const theme = themeAttribute(themePreference);

  return (
    <html
      lang={locale}
      data-theme={theme}
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col bg-[var(--color-surface-muted)] text-[var(--color-text)]">
        {children}
      </body>
    </html>
  );
}
