"use client";

import { usePathname } from "next/navigation";
import { signOutAction } from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { cn } from "@/lib/utils/cn";

const NAV_ITEMS = [
  { href: "/dashboard", key: "home" as const, enabled: true },
  { href: "/dashboard/properties", key: "newProperty" as const, enabled: false },
  { href: "/dashboard/settings", key: "settings" as const, enabled: false },
  { href: "/dashboard/billing", key: "billing" as const, enabled: false },
];

export function Sidebar({ dict }: { dict: Dictionary }) {
  const pathname = usePathname();
  const t = dict.dashboard.sidebar;

  return (
    <aside className="flex h-full w-64 flex-col justify-between border-r border-[var(--color-border)] bg-[var(--color-surface)] p-4">
      <nav className="space-y-1">
        {NAV_ITEMS.map((item) => {
          const active = pathname === item.href;
          const label = t[item.key];

          if (!item.enabled) {
            return (
              <div
                key={item.href}
                aria-disabled="true"
                className="flex items-center justify-between rounded-[var(--radius-sm)] px-3 py-2 text-sm text-[var(--color-text-muted)] opacity-60"
              >
                <span>{label}</span>
                <span className="text-xs">{t.comingSoon}</span>
              </div>
            );
          }

          return (
            <a
              key={item.href}
              href={item.href}
              className={cn(
                "block rounded-[var(--radius-sm)] px-3 py-2 text-sm font-medium",
                active
                  ? "bg-[var(--color-accent)] text-[var(--color-accent-foreground)]"
                  : "text-[var(--color-text)] hover:bg-[var(--color-surface-muted)]"
              )}
            >
              {label}
            </a>
          );
        })}
      </nav>

      <form
        action={async () => {
          await signOutAction();
        }}
      >
        <button
          type="submit"
          className="w-full rounded-[var(--radius-sm)] px-3 py-2 text-left text-sm text-[var(--color-text-muted)] hover:bg-[var(--color-surface-muted)]"
        >
          {t.signOut}
        </button>
      </form>
    </aside>
  );
}
