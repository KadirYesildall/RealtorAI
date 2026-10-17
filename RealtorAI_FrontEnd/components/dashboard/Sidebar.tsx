"use client";

import { usePathname } from "next/navigation";
import { signOutAction } from "@/app/actions/auth";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { cn } from "@/lib/utils/cn";
import { Logo } from "@/components/ui/Logo";
import {
  BuildingIcon,
  CardIcon,
  GridIcon,
  LogOutIcon,
  SettingsIcon,
  UsersIcon,
} from "@/components/ui/icons";

const NAV_ITEMS = [
  { href: "/dashboard", key: "home" as const, icon: GridIcon, enabled: true },
  { href: "/dashboard/listings", key: "listings" as const, icon: BuildingIcon, enabled: true },
  { href: "/dashboard/brokerage", key: "brokerage" as const, icon: UsersIcon, enabled: true },
  {
    href: "/dashboard/settings",
    key: "settings" as const,
    icon: SettingsIcon,
    enabled: false,
  },
  { href: "/dashboard/billing", key: "billing" as const, icon: CardIcon, enabled: false },
];

export function Sidebar({ dict }: { dict: Dictionary }) {
  const pathname = usePathname();
  const t = dict.dashboard.sidebar;

  return (
    // No `h-full` here: the parent is `min-h-screen`, so `height:100%` would resolve
    // against an auto height and collapse the rail to its content. Flex `align-items:
    // stretch` is what makes it span the viewport.
    //
    // Below md the rail narrows to its icons (T153: every screen usable at phone width). The
    // labels stay in the DOM as screen-reader text, so every link and the sign-out button keep
    // the same accessible name at every width.
    <aside className="flex w-16 shrink-0 flex-col justify-between border-r border-[var(--color-border)] bg-[var(--color-surface)] p-2 md:w-64 md:p-4">
      <div>
        <div className="hidden px-2 pb-6 pt-2 md:block">
          <Logo />
        </div>

        <nav className="space-y-1">
          {NAV_ITEMS.map((item) => {
            // Nested routes (a listing's wizard) keep their section highlighted; the dashboard
            // home matches only itself, or it would be active everywhere.
            const active =
              pathname === item.href || (item.href !== "/dashboard" && pathname.startsWith(`${item.href}/`));
            const label = t[item.key];
            const ItemIcon = item.icon;

            if (!item.enabled) {
              return (
                <div
                  key={item.href}
                  aria-disabled="true"
                  className="flex items-center gap-3 rounded-[var(--radius-md)] px-3 py-2 text-sm text-[var(--color-text-muted)] opacity-60"
                >
                  <ItemIcon className="h-[18px] w-[18px] shrink-0" />
                  <span className="sr-only flex-1 md:not-sr-only">{label}</span>
                  <span className="sr-only text-[0.6875rem] md:not-sr-only">{t.comingSoon}</span>
                </div>
              );
            }

            return (
              <a
                key={item.href}
                href={item.href}
                aria-current={active ? "page" : undefined}
                className={cn(
                  "flex items-center gap-3 rounded-[var(--radius-md)] px-3 py-2 text-sm font-medium transition-colors",
                  active
                    ? "bg-[var(--color-accent-soft)] text-[var(--color-accent)]"
                    : "text-[var(--color-text)] hover:bg-[var(--color-surface-muted)]"
                )}
              >
                <ItemIcon className="h-[18px] w-[18px] shrink-0" />
                <span className="sr-only md:not-sr-only">{label}</span>
              </a>
            );
          })}
        </nav>
      </div>

      <form
        action={async () => {
          await signOutAction();
        }}
      >
        <button
          type="submit"
          className="flex w-full items-center gap-3 rounded-[var(--radius-md)] px-3 py-2 text-left text-sm text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-surface-muted)] hover:text-[var(--color-text)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
        >
          <LogOutIcon className="h-[18px] w-[18px] shrink-0" />
          <span className="sr-only md:not-sr-only">{t.signOut}</span>
        </button>
      </form>
    </aside>
  );
}
