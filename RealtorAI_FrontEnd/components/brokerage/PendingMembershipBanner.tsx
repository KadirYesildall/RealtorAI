import { Alert } from "@/components/ui/Alert";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";

/**
 * T063 — shown across the dashboard while the agent's brokerage membership is pending.
 *
 * FR-061 draws a line an agent should not have to discover by running into it: private
 * preparation works now, while anything that publishes the brokerage's name waits for
 * approval. Saying so up front is the difference between "I'm blocked" and "this one step is
 * blocked, and here is why". Presentation only, so it has no test of its own; the state that
 * decides whether it renders comes from the API and is covered by the backend contract suite.
 */
export function PendingMembershipBanner({
  dict,
  brokerageName,
}: {
  dict: Dictionary["brokerage"]["banner"];
  brokerageName: string;
}) {
  return (
    // role="status": present on first paint and nothing was interrupted (see Alert).
    <Alert tone="accent" role="status" className="mb-6">
      <p className="font-medium">{format(dict.title, { brokerage: brokerageName })}</p>
      <p className="mt-1 text-[var(--color-text)]">{dict.body}</p>
      <a
        href="/dashboard/brokerage"
        className="mt-2 inline-block font-medium underline underline-offset-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-accent)]"
      >
        {dict.link}
      </a>
    </Alert>
  );
}
