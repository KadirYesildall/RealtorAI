"use client";

import { useEffect, useRef, useState } from "react";
import type { ComplianceFinding } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { AcknowledgeReasonSchema, MIN_ACKNOWLEDGE_REASON } from "@/lib/validation/listing";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { Textarea } from "@/components/ui/Textarea";
import { WarningIcon } from "@/components/ui/icons";

/**
 * T137 — acknowledging a warning, with a reason (FR-023; Story 4 scenario 9).
 *
 * Only ever opened for an open warning: the panel offers no way to open it for a blocking
 * finding, and the API refuses one anyway (FR-022). The reason is kept in the audit trail with
 * the agent's identity and the time, and the dialog says so before it is written.
 *
 * A native `<dialog>` opened modally, like the altered-photo dialog: focus is trapped and
 * Escape closes it.
 */
export function AcknowledgeWarningDialog({
  finding,
  dict,
  onClose,
  onSubmit,
}: {
  finding: ComplianceFinding;
  dict: Dictionary["listings"]["compliance"];
  onClose: () => void;
  /** Resolves to an error to show, or null when acknowledged. */
  onSubmit: (reason: string) => Promise<string | null>;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const valid = AcknowledgeReasonSchema.safeParse(reason).success;

  useEffect(() => {
    // No cleanup that closes it: see AlteredPhotoDialog — under StrictMode a cleanup close would
    // dismiss it the moment it opened. Removing the element is what closes it.
    if (ref.current && !ref.current.open) {
      ref.current.showModal();
    }
  }, []);

  return (
    <dialog
      ref={ref}
      aria-labelledby="acknowledge-title"
      onClose={onClose}
      className="m-auto w-[min(32rem,calc(100vw-2rem))] rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-surface-elevated)] p-6 text-[var(--color-text)] backdrop:bg-[color-mix(in_srgb,var(--color-text)_45%,transparent)]"
    >
      <h2 id="acknowledge-title" className="flex items-center gap-2 text-lg font-semibold">
        <WarningIcon className="size-5 shrink-0" />
        {dict.acknowledge.title}
      </h2>
      <p className="mt-2 text-sm text-[var(--color-text-muted)]">{dict.acknowledge.body}</p>

      {finding.phrase && (
        <blockquote className="mt-4 border-l-2 border-[var(--color-border-strong)] pl-3 text-sm">“{finding.phrase}”</blockquote>
      )}
      <p className="mt-2 text-sm">{finding.reason}</p>

      <form
        className="mt-5 space-y-4"
        onSubmit={async (event) => {
          event.preventDefault();
          if (!valid) return;

          setBusy(true);
          const failure = await onSubmit(reason);
          setBusy(false);

          if (failure) {
            setError(failure);
          } else {
            ref.current?.close();
          }
        }}
      >
        <FormField
          id="acknowledge-reason"
          label={dict.acknowledge.reason}
          hint={format(dict.acknowledge.reasonHint, { min: String(MIN_ACKNOWLEDGE_REASON) })}
          error={error ?? undefined}
        >
          {(describedBy) => (
            <Textarea
              id="acknowledge-reason"
              value={reason}
              rows={4}
              required
              invalid={error !== null}
              aria-describedby={describedBy}
              onChange={(event) => setReason(event.currentTarget.value)}
            />
          )}
        </FormField>

        <div className="flex flex-wrap gap-3">
          <Button type="submit" loading={busy} disabled={!valid}>
            {dict.acknowledge.submit}
          </Button>
          <Button type="button" variant="ghost" disabled={busy} onClick={() => ref.current?.close()}>
            {dict.acknowledge.cancel}
          </Button>
        </div>
      </form>
    </dialog>
  );
}
