"use client";

import { useRouter } from "next/navigation";
import { useId, useState, useTransition, type ReactNode } from "react";
import type { Photo, PhotoUploadResult } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { MAX_PHOTO_BYTES, MAX_PHOTOS_PER_LISTING, PHOTO_ACCEPT } from "@/lib/validation/listing";
import { Alert } from "@/components/ui/Alert";
import { Card } from "@/components/ui/Card";

/** Photos in flight at once. Each is its own request, so one failure costs one file. */
const CONCURRENCY = 2;

type RejectionCode = NonNullable<PhotoUploadResult["rejectionCode"]>;

type Upload = {
  key: string;
  fileName: string;
  state: "queued" | "uploading" | "uploaded" | "rejected";
  rejection?: RejectionCode | "unknown";
};

/**
 * T080 step 2 — photo upload (FR-001–FR-003; Story 1 scenarios 1–3).
 *
 * Each file is sent on its own to the upload Route Handler, which streams it to the API, so the
 * agent sees every file's outcome and an interrupted upload resumes by adding what did not land
 * (research R33). Two checks happen here first, because they need no server: the 100-photo bound
 * — a selection that would pass it is refused whole, before anything is sent, and nothing
 * already uploaded is touched — and the 50 MB limit. Whether a file is actually a photo is the
 * API's call, made from its bytes.
 */
export function PhotoUploader({
  listingId,
  photos,
  dict,
  children,
}: {
  listingId: string;
  photos: Photo[];
  dict: Dictionary["listings"];
  /** What shows the uploaded photos — the grid (T099). */
  children?: ReactNode;
}) {
  const t = dict.photos;
  const inputId = useId();
  const router = useRouter();
  const [, startTransition] = useTransition();
  const [uploads, setUploads] = useState<Upload[]>([]);
  const [overLimit, setOverLimit] = useState<{ chosen: number; room: number } | null>(null);
  const [busy, setBusy] = useState(false);

  const room = Math.max(0, MAX_PHOTOS_PER_LISTING - photos.length);

  const setUpload = (key: string, patch: Partial<Upload>) =>
    setUploads((current) => current.map((upload) => (upload.key === key ? { ...upload, ...patch } : upload)));

  const send = async (key: string, file: File) => {
    if (file.size > MAX_PHOTO_BYTES) {
      setUpload(key, { state: "rejected", rejection: "too_large" });
      return;
    }

    setUpload(key, { state: "uploading" });

    const body = new FormData();
    body.append("files", file, file.name);

    try {
      const response = await fetch(`/api/listings/${encodeURIComponent(listingId)}/photos`, { method: "POST", body });

      if (response.status === 207) {
        const [result] = (await response.json()) as PhotoUploadResult[];

        setUpload(
          key,
          result?.accepted
            ? { state: "uploaded" }
            : { state: "rejected", rejection: result?.rejectionCode ?? "unknown" }
        );
        return;
      }

      setUpload(key, { state: "rejected", rejection: response.status === 413 ? "too_large" : "unknown" });
    } catch {
      setUpload(key, { state: "rejected", rejection: "unknown" });
    }
  };

  const choose = async (files: File[]) => {
    if (files.length === 0) {
      return;
    }

    // Scenario 3: refused whole, before anything is sent. The API applies the same bound.
    if (files.length > room) {
      setOverLimit({ chosen: files.length, room });
      return;
    }

    setOverLimit(null);
    setBusy(true);

    const batch = files.map((file, index) => ({ key: `${Date.now()}-${index}-${file.name}`, file }));
    setUploads((current) => [
      ...current,
      ...batch.map(({ key, file }) => ({ key, fileName: file.name, state: "queued" as const })),
    ]);

    const queue = [...batch];
    await Promise.all(
      Array.from({ length: Math.min(CONCURRENCY, queue.length) }, async () => {
        for (let next = queue.shift(); next; next = queue.shift()) {
          await send(next.key, next.file);
        }
      })
    );

    setBusy(false);

    // The listing as stored now: its photos, with fresh signed URLs for the normalized copies.
    startTransition(() => router.refresh());
  };

  return (
    <Card id="photos">
      <section aria-labelledby="photos-title" className="space-y-4">
        <div>
          <h2 id="photos-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        <p className="text-sm font-medium text-[var(--color-text)]">{format(t.count, { count: String(photos.length) })}</p>

        {overLimit && (
          <Alert>{format(t.overLimit, { chosen: String(overLimit.chosen), room: String(overLimit.room) })}</Alert>
        )}

        {room === 0 ? (
          <p className="text-sm text-[var(--color-text-muted)]">{t.full}</p>
        ) : (
          <div className="space-y-1.5">
            <label
              htmlFor={inputId}
              className="inline-flex cursor-pointer items-center rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-surface)] px-4 py-2.5 text-sm font-medium text-[var(--color-text)] hover:bg-[var(--color-surface-muted)] has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-[var(--color-accent)]"
            >
              {t.choose}
              <input
                id={inputId}
                type="file"
                multiple
                accept={PHOTO_ACCEPT}
                disabled={busy}
                className="sr-only"
                aria-describedby={`${inputId}-formats`}
                onChange={(event) => {
                  const files = Array.from(event.currentTarget.files ?? []);
                  // Cleared so choosing the same file again (to retry it) fires a change.
                  event.currentTarget.value = "";
                  void choose(files);
                }}
              />
            </label>
            <p id={`${inputId}-formats`} className="text-sm text-[var(--color-text-muted)]">
              {t.formats}
            </p>
          </div>
        )}

        {uploads.length > 0 && (
          <ul aria-label={t.uploadsLabel} className="divide-y divide-[var(--color-border)] text-sm">
            {uploads.map((upload) => (
              <li key={upload.key} className="flex flex-wrap items-baseline justify-between gap-2 py-2">
                {upload.state === "rejected" ? (
                  <span className="text-[var(--color-danger)]">
                    {format(t.rejected[upload.rejection ?? "unknown"], { file: upload.fileName })}
                  </span>
                ) : (
                  <>
                    <span className="min-w-0 truncate text-[var(--color-text)]">{upload.fileName}</span>
                    <span className="text-[var(--color-text-muted)]">{t.state[upload.state]}</span>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}

        {photos.length === 0 ? <p className="text-sm text-[var(--color-text-muted)]">{t.empty}</p> : children}
      </section>
    </Card>
  );
}
