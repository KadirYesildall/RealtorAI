"use client";

import { useActionState } from "react";
import { archiveListingAction, type ArchiveState } from "@/app/actions/listing";
import { Button } from "@/components/ui/Button";

const INITIAL: ArchiveState = { status: "idle" };

/**
 * Soft delete from the index (FR-067). Asks first, because the listing leaves the working view;
 * nothing is destroyed — the API archives it and keeps its history.
 */
export function ArchiveListingButton({
  listingId,
  label,
  accessibleLabel,
  confirmMessage,
  errorMessage,
}: {
  listingId: string;
  label: string;
  accessibleLabel: string;
  confirmMessage: string;
  errorMessage: string;
}) {
  const [state, formAction, pending] = useActionState(archiveListingAction.bind(null, listingId), INITIAL);

  return (
    <form
      action={formAction}
      onSubmit={(event) => {
        if (!window.confirm(confirmMessage)) {
          event.preventDefault();
        }
      }}
    >
      <Button type="submit" variant="ghost" size="sm" loading={pending} aria-label={accessibleLabel}>
        {label}
      </Button>
      {state.status === "error" && (
        <p role="alert" className="mt-1 text-xs text-[var(--color-danger)]">
          {errorMessage}
        </p>
      )}
    </form>
  );
}
