"use client";

import { useRouter } from "next/navigation";
import { useState, useTransition } from "react";
import { approveAction } from "@/app/actions/approval";
import type { ApprovalBlocker, ApprovalOutput, ComplianceFinding, ListingField } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { CheckIcon } from "@/components/ui/icons";

type ApprovalDict = Dictionary["listings"]["approval"];

/**
 * T151 — the approve control: one row per output, each approved on its own for the MLS
 * (FR-026; Story 5 scenarios 3, 5).
 *
 * <b>Disabled with reasons.</b> An output that cannot be approved says why, in the agent's
 * language, one line per machine-readable blocker — never a bare greyed-out button. The listing
 * row is last: it is approved once the copy it carries is.
 *
 * <b>Approves what is shown.</b> Each approve sends the hash the page was rendered with. If the
 * content changed in the meantime, nothing is recorded, the page reloads with the current
 * content, and the agent reviews it again (SC-011).
 */
export function ApprovalBar({
  listingId,
  outputs,
  fields,
  findings,
  dict,
}: {
  listingId: string;
  outputs: ApprovalOutput[];
  fields: ListingField[];
  findings: ComplianceFinding[];
  dict: Dictionary["listings"];
}) {
  const t = dict.approval;
  const router = useRouter();
  const [, startTransition] = useTransition();
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refused, setRefused] = useState<{ targetId: string; blockers: ApprovalBlocker[] } | null>(null);

  const name = (output: ApprovalOutput) =>
    output.targetType === "listing" ? t.listing : dict.content.kinds[(output.kind ?? "public_remarks") as keyof Dictionary["listings"]["content"]["kinds"]];

  const approveOne = async (output: ApprovalOutput) => {
    setBusy(output.targetId ?? null);
    setNotice(null);
    setError(null);
    setRefused(null);

    const result = await approveAction(listingId, output.targetType as "listing" | "content_draft", output.targetId ?? "", output.contentHash ?? "");

    setBusy(null);

    if (result.status === "approved") {
      setNotice(output.targetType === "listing" ? t.listingApproved : format(t.approved, { name: name(output) }));
    } else if (result.status === "blocked") {
      if (result.changed) setNotice(t.changed);
      else setRefused({ targetId: output.targetId ?? "", blockers: result.blockers });
    } else {
      setError((t.errors as Record<string, string>)[result.code] ?? t.errors.unexpected);
    }

    startTransition(() => router.refresh());
  };

  return (
    <Card id="approval">
      <section aria-labelledby="approval-title" className="space-y-4">
        <div>
          <h2 id="approval-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        <div role="status" aria-live="polite" className="text-sm text-[var(--color-text)]">
          {notice}
        </div>
        {error && <Alert>{error}</Alert>}

        <ul className="space-y-3">
          {outputs.map((output) => {
            const blockers = refused && refused.targetId === output.targetId ? refused.blockers : (output.blockers ?? []);
            const label = output.targetType === "listing" ? t.approveListing : format(t.approve, { name: name(output) });

            return (
              <li
                key={output.targetId}
                className={cn(
                  "space-y-2 rounded-[var(--radius-md)] border p-4",
                  output.approved ? "border-[var(--color-accent)]" : "border-[var(--color-border)]"
                )}
              >
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <p className="font-medium text-[var(--color-text)]">{name(output)}</p>
                    <p className="flex items-center gap-1 text-sm text-[var(--color-text-muted)]">
                      {output.approved && <CheckIcon className="size-3.5 text-[var(--color-accent)]" />}
                      <span>{output.approved ? t.state.approved : t.state.notApproved}</span>
                    </p>
                  </div>
                  {!output.approved && (
                    <Button type="button" size="sm" loading={busy === output.targetId} disabled={blockers.length > 0 || busy !== null} onClick={() => void approveOne(output)}>
                      {label}
                    </Button>
                  )}
                </div>

                {!output.approved && blockers.length > 0 && (
                  <div className="text-sm text-[var(--color-text)]">
                    <p className="font-medium">{t.notYet}</p>
                    <ul className="mt-1 list-disc space-y-0.5 pl-5">
                      {blockers.map((blocker, index) => (
                        <li key={`${blocker.code}-${blocker.targetId ?? index}`}>{describe(blocker, t, fields, findings, dict)}</li>
                      ))}
                    </ul>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      </section>
    </Card>
  );
}

/** A blocker in the agent's language, naming the field or finding it concerns when it can. */
function describe(
  blocker: ApprovalBlocker,
  t: ApprovalDict,
  fields: ListingField[],
  findings: ComplianceFinding[],
  dict: Dictionary["listings"]
): string {
  const text = (t.blockers as Record<string, string>)[blocker.code ?? ""] ?? t.blockers.unknown;
  const field = fields.find((candidate) => candidate.id === blocker.targetId);
  const finding = findings.find((candidate) => candidate.id === blocker.targetId);
  const factLabels = dict.content.facts as Record<string, string>;

  const fieldLabels = { ...factLabels, ...(dict.required.fields as Record<string, string>) };
  const key = blocker.fieldKey ?? field?.resoFieldKey ?? "";

  if (blocker.code === "unconfirmed_ai_fact" && key) {
    return format(t.blockers.unconfirmedField, { field: fieldLabels[key] ?? key });
  }

  if (blocker.code === "blocking_finding_open" && finding) {
    return format(t.blockers.finding, { phrase: finding.phrase ?? finding.reason ?? "" });
  }

  if (blocker.code === "required_field_missing" && key) {
    return format(t.blockers.requiredField, { field: fieldLabels[key] ?? key });
  }

  return text;
}
