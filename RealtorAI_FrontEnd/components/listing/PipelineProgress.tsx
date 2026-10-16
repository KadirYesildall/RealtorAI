"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState, useTransition } from "react";
import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";
import { pipelineStatusAction, startAnalysisAction } from "@/app/actions/photos";
import type { PipelineStatus } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { createClient } from "@/lib/supabase/client";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";

/** How often status is re-read when the live channel is unavailable. */
const POLL_MS = 3000;

type StepName = keyof Dictionary["listings"]["analysis"]["steps"];
type StepState = keyof Dictionary["listings"]["analysis"]["stepState"];
type PhotoState = keyof Dictionary["listings"]["analysis"]["photoState"];

function running(status: PipelineStatus | null): boolean {
  return (status?.steps ?? []).some((step) => step.state === "queued" || step.state === "running");
}

/**
 * T101 — the live view of photo analysis (FR-053, SC-010).
 *
 * Progress arrives per photo over the pipeline hub, which only the listing's owner may join. A
 * message is a hint, not the record: on each one this re-reads the persisted status, and when the
 * hub cannot be reached it polls the same status instead (GET /pipeline is the source of truth).
 * The hub is the one API path the browser calls directly; it presents the session's token as the
 * contract requires, and sends no cookie.
 *
 * When analysis is unavailable — no enabled model, or the budget is spent — it says so and points
 * at the manual path below; nothing on the page waits on it.
 */
export function PipelineProgress({
  listingId,
  initialStatus,
  photoCount,
  dict,
}: {
  listingId: string;
  initialStatus: PipelineStatus | null;
  photoCount: number;
  dict: Dictionary["listings"];
}) {
  const t = dict.analysis;
  const router = useRouter();
  const [, startTransition] = useTransition();
  const [status, setStatus] = useState<PipelineStatus | null>(initialStatus);
  const [manual, setManual] = useState<"noModel" | "budget" | "noPhotos" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const wasRunning = useRef(running(initialStatus));

  const refresh = useCallback(async () => {
    const latest = await pipelineStatusAction(listingId);

    if (latest) {
      setStatus(latest);

      // The run just settled: re-read the page, so the grid shows what analysis concluded.
      if (wasRunning.current && !running(latest)) {
        startTransition(() => router.refresh());
      }

      wasRunning.current = running(latest);
    }
  }, [listingId, router]);

  // ── Live channel ──
  useEffect(() => {
    const base = process.env.NEXT_PUBLIC_API_BASE_URL;
    if (!base) return;

    const supabase = createClient();
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${base.replace(/\/+$/, "")}/hubs/pipeline`, {
        accessTokenFactory: async () => (await supabase.auth.getSession()).data.session?.access_token ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      // Silent: a failed connection is handled here (the poll takes over), and under StrictMode
      // the first connection is stopped mid-negotiation by design — neither is an error to log.
      .configureLogging(LogLevel.None)
      .build();

    connection.on("StepProgress", () => void refresh());
    connection.on("PhotoProgress", () => void refresh());
    connection.onreconnected(async () => {
      await connection.invoke("SubscribeToListing", listingId);
      await refresh();
    });

    let active = true;
    void connection
      .start()
      .then(() => (active ? connection.invoke("SubscribeToListing", listingId) : undefined))
      .catch(() => {
        // The poll below covers it.
      });

    return () => {
      active = false;
      void connection.stop();
    };
  }, [listingId, refresh]);

  // ── Poll while a run is in progress — always correct, whether or not the hub is up ──
  useEffect(() => {
    if (!running(status)) return;

    const timer = setInterval(() => void refresh(), POLL_MS);
    return () => clearInterval(timer);
  }, [status, refresh]);

  const start = async () => {
    setStarting(true);
    setError(null);
    setManual(null);

    const result = await startAnalysisAction(listingId);

    setStarting(false);

    if (result.status === "started") {
      wasRunning.current = true;
      setStatus(result.pipeline);
    } else if (result.status === "manual") {
      setManual(result.reason);
    } else {
      setError((t.errors as Record<string, string>)[result.code] ?? t.errors.unexpected);
    }
  };

  const busy = running(status);
  const failedPhotos = (status?.photoProgress ?? []).filter((photo) => photo.state === "failed").length;

  return (
    <Card id="analysis">
      <section aria-labelledby="analysis-title" className="space-y-4">
        <div>
          <h2 id="analysis-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        {manual && <Alert role="status">{t.manual[manual]}</Alert>}
        {error && <Alert>{error}</Alert>}

        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" loading={starting} disabled={busy || photoCount === 0} onClick={() => void start()}>
            {status && status.steps && status.steps.length > 0 ? t.restart : t.start}
          </Button>
          {failedPhotos > 0 && !busy && <span className="text-sm text-[var(--color-text-muted)]">{t.retryHint}</span>}
        </div>

        {status && (status.steps ?? []).length > 0 && (
          <ol aria-label={t.stepsLabel} aria-live="polite" className="space-y-1.5 text-sm">
            {(status.steps ?? []).map((step) => (
              <li key={step.name} className="flex flex-wrap items-center justify-between gap-2">
                <span className="text-[var(--color-text)]">{t.steps[step.name as StepName]}</span>
                <span
                  className={cn(
                    "rounded-full px-2 py-0.5 text-xs font-medium",
                    step.state === "failed"
                      ? "bg-[var(--color-danger-soft)] text-[var(--color-danger)]"
                      : step.state === "done"
                        ? "bg-[var(--color-accent-soft)] text-[var(--color-accent)]"
                        : "bg-[var(--color-surface-muted)] text-[var(--color-text)]"
                  )}
                >
                  {t.stepState[step.state as StepState]}
                </span>
              </li>
            ))}
          </ol>
        )}

        {status && (status.photoProgress ?? []).length > 0 && (
          <ol className="grid gap-1.5 text-sm sm:grid-cols-2">
            {(status.photoProgress ?? []).map((photo, index) => (
              <li key={photo.photoId} className="flex items-center justify-between gap-2 text-[var(--color-text)]">
                <span>{format(dict.photos.photoAlt, { n: String(index + 1) })}</span>
                <span className={photo.state === "failed" ? "text-[var(--color-danger)]" : "text-[var(--color-text-muted)]"}>
                  {t.photoState[photo.state as PhotoState]}
                </span>
              </li>
            ))}
          </ol>
        )}
      </section>
    </Card>
  );
}
