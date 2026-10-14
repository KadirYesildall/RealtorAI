"use client";

import { useActionState } from "react";
import { decideMembershipAction, type DecisionActionState } from "@/app/actions/brokerage";
import type { Membership } from "@/lib/api/brokerage";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { Button } from "@/components/ui/Button";

const INITIAL: DecisionActionState = { status: "idle" };

/**
 * T063 — one pending request, with approve and reject (FR-060, FR-062).
 *
 * The agent's name and license are shown because they are what an admin is deciding on. Both
 * buttons submit the same form with a different `decision`, and each carries an accessible
 * name that includes the agent, so a screen-reader user tabbing through a list of requests
 * hears whom each button acts on rather than "Approve, Approve, Approve".
 */
export function MembershipDecisionRow({
  brokerageId,
  membership,
  dict,
  locale,
}: {
  brokerageId: string;
  membership: Membership;
  dict: Dictionary["brokerage"];
  locale: string;
}) {
  const [state, formAction, pending] = useActionState(
    decideMembershipAction.bind(null, brokerageId, membership.id ?? ""),
    INITIAL
  );

  const t = dict.members;
  const errors = dict.errors as Record<string, string>;
  const name = membership.agentName?.trim() || t.unnamed;

  const license =
    membership.agentLicenseNumber && membership.agentLicenseState
      ? format(t.license, { number: membership.agentLicenseNumber, state: membership.agentLicenseState })
      : t.noLicense;

  const requested = membership.requestedAt
    ? format(t.requested, {
        date: new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(membership.requestedAt)),
      })
    : null;

  return (
    <li className="flex flex-col gap-3 py-4 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <p className="truncate font-medium text-[var(--color-text)]">{name}</p>
        <p className="text-sm text-[var(--color-text-muted)]">{license}</p>
        {requested && <p className="text-sm text-[var(--color-text-muted)]">{requested}</p>}
      </div>

      {state.status === "decided" ? (
        <p role="status" className="text-sm font-medium text-[var(--color-text)]">
          {state.decision === "approved" ? t.decidedApproved : t.decidedRejected}
        </p>
      ) : (
        <form action={formAction} className="flex shrink-0 flex-col items-start gap-2 sm:items-end">
          <div className="flex gap-2">
            <Button
              type="submit"
              name="decision"
              value="approved"
              size="sm"
              disabled={pending}
              aria-label={format(t.approveLabel, { name })}
            >
              {t.approve}
            </Button>
            <Button
              type="submit"
              name="decision"
              value="rejected"
              variant="secondary"
              size="sm"
              disabled={pending}
              aria-label={format(t.rejectLabel, { name })}
            >
              {t.reject}
            </Button>
          </div>
          {state.status === "error" && (
            <p role="alert" className="text-sm text-[var(--color-danger)]">
              {errors[state.code] ?? errors.unexpected}
            </p>
          )}
        </form>
      )}
    </li>
  );
}
