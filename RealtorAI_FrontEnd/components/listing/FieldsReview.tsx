"use client";

import { useRouter } from "next/navigation";
import { useState, useTransition } from "react";
import { confirmFieldAction } from "@/app/actions/approval";
import type { ListingField } from "@/lib/api/listings";
import type { Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";

/**
 * T150 — every fact of the listing with where it came from, and a confirm action for the ones
 * an AI suggested (FR-005, FR-006, FR-027; Story 5 scenario 1).
 *
 * Provenance is stated in words on every row: entered by the agent, from a public record, seen in
 * the photos, or suggested by AI. An AI-derived fact stays unconfirmed — and content resting on it
 * unapprovable (FR-014) — until the agent confirms it here.
 */
export function FieldsReview({ listingId, fields, dict }: { listingId: string; fields: ListingField[]; dict: Dictionary["listings"] }) {
  const t = dict.facts;
  const router = useRouter();
  const [, startTransition] = useTransition();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const labels = { ...(dict.content.facts as Record<string, string>), ...(dict.required.fields as Record<string, string>) };

  const confirm = async (field: ListingField) => {
    setBusy(field.id ?? null);
    setError(null);
    const result = await confirmFieldAction(listingId, field.id ?? "");
    setBusy(null);

    if (result.status === "error") {
      setError((dict.approval.errors as Record<string, string>)[result.code] ?? dict.approval.errors.unexpected);
    }

    startTransition(() => router.refresh());
  };

  return (
    <Card id="facts">
      <section aria-labelledby="facts-title" className="space-y-4">
        <div>
          <h2 id="facts-title" className="text-lg font-semibold text-[var(--color-text)]">
            {t.title}
          </h2>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.body}</p>
        </div>

        {error && <Alert>{error}</Alert>}

        {fields.length === 0 ? (
          <p className="text-sm text-[var(--color-text-muted)]">{t.empty}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="text-[var(--color-text-muted)]">
                <tr>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.columns.field}</th>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.columns.value}</th>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.columns.source}</th>
                  <th scope="col" className="py-2 font-medium">
                    <span className="sr-only">{t.columns.actions}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {fields.map((field) => {
                  const ai = field.provenance === "ai_detected_from_photo" || field.provenance === "ai_suggested";
                  const label = labels[field.resoFieldKey ?? ""] ?? field.resoFieldKey;

                  return (
                    <tr key={field.id} className="border-t border-[var(--color-border)] text-[var(--color-text)]">
                      <th scope="row" className="py-2 pr-4 font-medium">{label}</th>
                      <td className="py-2 pr-4">{display(field.value)}</td>
                      <td className="py-2 pr-4">
                        <span className="rounded-full border border-[var(--color-border-strong)] px-2 py-0.5 text-xs">
                          {t.provenance[(field.provenance ?? "agent_entered") as keyof typeof t.provenance]}
                        </span>
                        {ai && <span className="ml-2 text-xs">{field.confirmed ? t.confirmed : t.unconfirmed}</span>}
                      </td>
                      <td className="py-2 text-right">
                        {ai && !field.confirmed && (
                          <Button type="button" size="sm" variant="secondary" loading={busy === field.id} onClick={() => void confirm(field)}>
                            {format(t.confirm, { field: label ?? "" })}
                          </Button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </Card>
  );
}

function display(value: unknown): string {
  if (Array.isArray(value)) return value.map(String).join(", ");
  if (value === null || value === undefined) return "";
  return String(value);
}
