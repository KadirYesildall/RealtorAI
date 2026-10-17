"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState, useTransition } from "react";
import { acknowledgeAction, applyFixAction, findingsAction, runComplianceAction } from "@/app/actions/compliance";
import { pipelineStatusAction } from "@/app/actions/photos";
import type { ComplianceFinding, ContentDraft, Photo, PipelineStatus } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { focusSpan } from "@/lib/compliance/focus";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { BlockIcon, CheckIcon, WarningIcon } from "@/components/ui/icons";
import { AcknowledgeWarningDialog } from "@/components/compliance/AcknowledgeWarningDialog";

type ComplianceDict = Dictionary["listings"]["compliance"];

/** How often the check's status is re-read while it runs. */
const POLL_MS = 2000;

function checking(status: PipelineStatus | null): boolean {
  return (status?.steps ?? []).some((step) => step.name === "compliance" && (step.state === "queued" || step.state === "running"));
}

/**
 * T136 — the compliance review: every finding with its severity, the layer that saw it, the
 * offending words, why, and what to write instead (FR-017–FR-023; Story 4).
 *
 * <b>Severity is never colour alone.</b> Each finding states "Blocking" or "Warning" in words,
 * with a distinct icon, and the count of each is stated above the list.
 *
 * <b>Acting on a finding.</b> "Show in text" marks the span in the content card. "Use the
 * suggested wording" applies the rewrite as the agent's edit and re-checks that draft. A warning
 * may be acknowledged with a reason. A blocking finding offers neither: the only way past it is to
 * change the content (FR-022).
 *
 * Findings are re-read after a check settles, and come with the page after any edit, since an
 * edit re-checks the edited item at once (FR-025).
 */
export function FindingList({
  listingId,
  findings,
  drafts,
  photos,
  pipeline,
  dict,
}: {
  listingId: string;
  findings: ComplianceFinding[];
  drafts: ContentDraft[];
  photos: Photo[];
  pipeline: PipelineStatus | null;
  dict: Dictionary["listings"];
}) {
  const t = dict.compliance;
  const router = useRouter();
  const [, startTransition] = useTransition();

  const [local, setLocal] = useState(findings);
  const [source, setSource] = useState(findings);

  if (source !== findings) {
    setSource(findings);
    setLocal(findings);
  }

  const [running, setRunning] = useState(checking(pipeline));
  const [error, setError] = useState<string | null>(null);
  const [acknowledging, setAcknowledging] = useState<ComplianceFinding | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const errorText = t.errors as Record<string, string>;

  const reload = useCallback(async () => {
    const latest = await findingsAction(listingId);
    if (latest) setLocal(latest);
    startTransition(() => router.refresh());
  }, [listingId, router]);

  useEffect(() => {
    if (!running) return;

    const timer = setInterval(async () => {
      const status = await pipelineStatusAction(listingId);

      if (status && !checking(status)) {
        setRunning(false);
        await reload();
      }
    }, POLL_MS);

    return () => clearInterval(timer);
  }, [running, listingId, reload]);

  const run = async () => {
    setError(null);
    const result = await runComplianceAction(listingId);

    if (result.status === "started") {
      setRunning(true);
    } else {
      setError(errorText[result.code] ?? t.errors.unexpected);
    }
  };

  const fix = async (finding: ComplianceFinding) => {
    setError(null);
    setBusyId(finding.id ?? null);
    const result = await applyFixAction(listingId, finding.id ?? "");
    setBusyId(null);

    if (result.status === "saved") {
      await reload();
    } else {
      setError(errorText[result.code] ?? t.errors.unexpected);
    }
  };

  // Resolved findings have been dealt with; the list is what is still open or was acknowledged.
  const visible = local.filter((finding) => finding.status !== "resolved");
  const open = visible.filter((finding) => finding.status === "open");
  const blocking = open.filter((finding) => finding.severity === "blocking").length;
  const warnings = open.length - blocking;

  const target = (finding: ComplianceFinding) => {
    if (finding.targetType === "listing") return t.targets.listing;

    if (finding.targetType === "photo") {
      const index = photos.findIndex((photo) => photo.id === finding.targetId);
      return format(t.targets.photo, { n: String(index + 1) });
    }

    const draft = drafts.find((candidate) => candidate.id === finding.targetId);
    const kind = draft?.kind ? dict.content.kinds[draft.kind] : t.targets.content;
    const siblings = drafts.filter((candidate) => candidate.kind === draft?.kind);

    return siblings.length > 1 ? format(t.targets.version, { kind, n: String((draft?.variantIndex ?? 0) + 1) }) : kind;
  };

  return (
    <Card id="compliance">
      <section aria-labelledby="compliance-title" className="space-y-4">
        <div>
          <h2 id="compliance-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" loading={running} onClick={() => void run()}>
            {local.length > 0 ? t.runAgain : t.run}
          </Button>
          {running && <span role="status" className="text-sm text-[var(--color-text-muted)]">{t.running}</span>}
        </div>

        {error && <Alert>{error}</Alert>}

        {local.length > 0 && (
          <p role="status" className="text-sm text-[var(--color-text)]">
            {open.length === 0 ? t.allClear : format(t.summary, { blocking: String(blocking), warnings: String(warnings) })}
          </p>
        )}

        {visible.length > 0 && (
          <ul aria-label={t.listLabel} className="space-y-3">
            {visible.map((finding) => (
              <FindingItem
                key={finding.id}
                finding={finding}
                target={target(finding)}
                busy={busyId === finding.id}
                dict={t}
                onShow={
                  finding.targetType === "content_draft" && finding.spanStart != null && finding.spanEnd != null && finding.status === "open"
                    ? () => focusSpan({ draftId: finding.targetId ?? "", start: finding.spanStart!, end: finding.spanEnd! })
                    : null
                }
                onFix={finding.targetType === "content_draft" && finding.suggestedFix && finding.status === "open" ? () => void fix(finding) : null}
                onAcknowledge={finding.canAcknowledge ? () => setAcknowledging(finding) : null}
              />
            ))}
          </ul>
        )}

        {acknowledging && (
          <AcknowledgeWarningDialog
            finding={acknowledging}
            dict={t}
            onClose={() => setAcknowledging(null)}
            onSubmit={async (reason) => {
              const result = await acknowledgeAction(listingId, acknowledging.id ?? "", reason);

              if (result.status === "saved") {
                setLocal((current) => current.map((candidate) => (candidate.id === result.finding.id ? result.finding : candidate)));
                return null;
              }

              return errorText[result.code] ?? t.errors.unexpected;
            }}
          />
        )}
      </section>
    </Card>
  );
}

function FindingItem({
  finding,
  target,
  busy,
  dict,
  onShow,
  onFix,
  onAcknowledge,
}: {
  finding: ComplianceFinding;
  target: string;
  busy: boolean;
  dict: ComplianceDict;
  onShow: (() => void) | null;
  onFix: (() => void) | null;
  onAcknowledge: (() => void) | null;
}) {
  const blocking = finding.severity === "blocking";
  const settled = finding.status !== "open";

  return (
    <li
      className={cn(
        "space-y-2 rounded-[var(--radius-md)] border p-4",
        settled
          ? "border-[var(--color-border)]"
          : blocking
            ? "border-[var(--color-danger)] bg-[var(--color-danger-soft)]"
            : "border-[var(--color-border-strong)] bg-[var(--color-surface-muted)]"
      )}
    >
      <div className="flex flex-wrap items-center gap-2 text-xs font-medium text-[var(--color-text)]">
        <span className="inline-flex items-center gap-1 rounded-full border border-current px-2 py-0.5">
          {blocking ? <BlockIcon className="size-3.5" /> : <WarningIcon className="size-3.5" />}
          <span>{blocking ? dict.severity.blocking : dict.severity.warning}</span>
        </span>
        <span>{dict.checkTypes[(finding.checkType ?? "mls_rule") as keyof ComplianceDict["checkTypes"]]}</span>
        <span aria-hidden="true">·</span>
        <span>{dict.sources[(finding.source ?? "deterministic") as keyof ComplianceDict["sources"]]}</span>
        <span aria-hidden="true">·</span>
        <span>{target}</span>
        {settled && (
          <span className="inline-flex items-center gap-1 text-[var(--color-text-muted)]">
            <CheckIcon className="size-3" />
            {dict.status[(finding.status ?? "resolved") as keyof ComplianceDict["status"]]}
          </span>
        )}
      </div>

      {finding.phrase && <p className="text-sm font-medium text-[var(--color-text)]">“{finding.phrase}”</p>}
      <p className="text-sm text-[var(--color-text)]">{finding.reason}</p>
      {finding.suggestedFix && !settled && (
        <p className="text-sm text-[var(--color-text)]">
          <span className="font-medium">{dict.suggested}</span> {finding.suggestedFix}
        </p>
      )}
      {blocking && !settled && <p className="text-xs text-[var(--color-text)]">{dict.blockingNote}</p>}

      {(onShow || onFix || onAcknowledge) && (
        <div className="flex flex-wrap gap-2 pt-1">
          {onShow && (
            <Button type="button" size="sm" variant="ghost" onClick={onShow}>
              {dict.show}
            </Button>
          )}
          {onFix && (
            <Button type="button" size="sm" variant="secondary" loading={busy} onClick={onFix}>
              {dict.applyFix}
            </Button>
          )}
          {onAcknowledge && (
            <Button type="button" size="sm" variant="ghost" onClick={onAcknowledge}>
              {dict.acknowledgeButton}
            </Button>
          )}
        </div>
      )}
    </li>
  );
}
