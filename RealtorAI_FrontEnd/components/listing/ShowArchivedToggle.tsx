"use client";

import { usePathname, useRouter } from "next/navigation";
import { useOptimistic, useTransition } from "react";
import { Checkbox } from "@/components/ui/Checkbox";

/**
 * The index's archived filter (FR-067). State lives in the URL (`?archived=1`) rather than in the
 * component, so the filtered view is linkable and survives a reload, and the list itself stays a
 * Server Component.
 */
export function ShowArchivedToggle({ label, checked }: { label: string; checked: boolean }) {
  const router = useRouter();
  const pathname = usePathname();
  const [pending, startTransition] = useTransition();
  // Shows the new state at once, while the filtered list loads; settles on the URL's afterwards.
  const [shown, setShown] = useOptimistic(checked);

  return (
    <Checkbox
      id="listings-show-archived"
      label={label}
      checked={shown}
      disabled={pending}
      onChange={(event) => {
        const next = event.currentTarget.checked;

        startTransition(() => {
          setShown(next);
          router.replace(next ? `${pathname}?archived=1` : pathname);
        });
      }}
    />
  );
}
