"use client";

import { useEffect, useRef, useState } from "react";
import type { PatchPhotoRequest, Photo } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { Select } from "@/components/ui/Select";

/**
 * T100 — the altered-photo declaration (FR-020, California AB 723).
 *
 * Records what the agent did to a photo elsewhere; this system never edits a pixel. All three
 * parts or none: the type of alteration, a disclosure label from the MLS's approved list — never
 * free text — and the unaltered original, shown beside it. With no approved label recorded for
 * the MLS (CRMLS until legal review, T161) there is nothing to choose, and the dialog says so
 * rather than offering a form the API would refuse.
 *
 * A native `<dialog>` opened modally: focus is trapped and Escape closes it, with no script of
 * ours to get wrong.
 */
export function AlteredPhotoDialog({
  photo,
  candidates,
  approvedLabels,
  dict,
  onClose,
  onSubmit,
}: {
  photo: Photo;
  /** Photos that may be the unaltered original: on this listing, not this one, not altered. */
  candidates: Photo[];
  approvedLabels: string[];
  dict: Dictionary["listings"];
  onClose: () => void;
  onSubmit: (change: PatchPhotoRequest) => Promise<void>;
}) {
  const t = dict.altered;
  const ref = useRef<HTMLDialogElement>(null);
  const [type, setType] = useState<"virtual_staging" | "other">(photo.alterationType ?? "virtual_staging");
  const [label, setLabel] = useState(photo.disclosureLabel ?? approvedLabels[0] ?? "");
  const [paired, setPaired] = useState(photo.pairedOriginalId ?? "");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    // No cleanup that closes it: closing fires onClose, which unmounts the dialog — and under
    // StrictMode's mount, unmount, mount, a cleanup close would dismiss it the moment it opened.
    // Removing the element is what closes it when the parent unmounts it.
    if (ref.current && !ref.current.open) {
      ref.current.showModal();
    }
  }, []);

  const submit = async (change: PatchPhotoRequest) => {
    setBusy(true);
    await onSubmit(change);
    setBusy(false);
  };

  return (
    <dialog
      ref={ref}
      aria-labelledby="altered-title"
      onClose={onClose}
      className="m-auto w-[min(32rem,calc(100vw-2rem))] rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-surface-elevated)] p-6 text-[var(--color-text)] backdrop:bg-[color-mix(in_srgb,var(--color-text)_45%,transparent)]"
    >
      <h2 id="altered-title" className="text-lg font-semibold">
        {t.title}
      </h2>
      <p className="mt-2 text-sm text-[var(--color-text-muted)]">{t.body}</p>

      {approvedLabels.length === 0 ? (
        <div className="mt-5 space-y-4">
          <Alert role="status">{t.noLabels}</Alert>
          <Button type="button" variant="secondary" onClick={() => ref.current?.close()}>
            {t.cancel}
          </Button>
        </div>
      ) : (
        <form
          className="mt-5 space-y-5"
          onSubmit={(event) => {
            event.preventDefault();
            void submit({ altered: true, alterationType: type, disclosureLabel: label, pairedOriginalId: paired });
          }}
        >
          <FormField id="altered-type" label={t.type}>
            {(describedBy) => (
              <Select id="altered-type" value={type} aria-describedby={describedBy} onChange={(event) => setType(event.currentTarget.value as typeof type)}>
                <option value="virtual_staging">{t.types.virtual_staging}</option>
                <option value="other">{t.types.other}</option>
              </Select>
            )}
          </FormField>

          <FormField id="altered-label" label={t.label}>
            {(describedBy) => (
              <Select id="altered-label" value={label} aria-describedby={describedBy} onChange={(event) => setLabel(event.currentTarget.value)}>
                {approvedLabels.map((approved) => (
                  <option key={approved} value={approved}>
                    {approved}
                  </option>
                ))}
              </Select>
            )}
          </FormField>

          <FormField id="altered-paired" label={t.paired} hint={candidates.length === 0 ? t.noOriginal : undefined}>
            {(describedBy) => (
              <Select
                id="altered-paired"
                value={paired}
                required
                aria-describedby={describedBy}
                onChange={(event) => setPaired(event.currentTarget.value)}
              >
                <option value="" disabled>
                  {t.pairedNone}
                </option>
                {candidates.map((candidate) => (
                  <option key={candidate.id} value={candidate.id}>
                    {candidate.caption ?? format(dict.photos.photoAlt, { n: String((candidate.orderIndex ?? 0) + 1) })}
                  </option>
                ))}
              </Select>
            )}
          </FormField>

          <div className="flex flex-wrap gap-3">
            <Button type="submit" loading={busy} disabled={paired === ""}>
              {t.save}
            </Button>
            {photo.altered && (
              <Button type="button" variant="secondary" disabled={busy} onClick={() => void submit({ altered: false })}>
                {t.withdraw}
              </Button>
            )}
            <Button type="button" variant="ghost" onClick={() => ref.current?.close()}>
              {t.cancel}
            </Button>
          </div>
        </form>
      )}
    </dialog>
  );
}
