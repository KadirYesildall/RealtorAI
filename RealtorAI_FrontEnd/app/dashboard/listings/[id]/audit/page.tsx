import Link from "next/link";
import { notFound } from "next/navigation";
import { z } from "zod";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { ApiError } from "@/lib/api/client";
import { getListing, listAudit, type AuditEvent } from "@/lib/api/listings";
import { Card } from "@/components/ui/Card";

/**
 * T152 — the listing's audit trail (FR-049, FR-050; SC-008): what happened, by whom, when, newest
 * first, one page at a time. It answers "what exactly did I approve, and when?" — each approval
 * row carries the hash of the content it approved.
 */
export default async function AuditPage({ params, searchParams }: PageProps<"/dashboard/listings/[id]/audit">) {
  const [{ id }, query, profile] = await Promise.all([params, searchParams, getAuthenticatedProfile()]);
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const t = (await getDictionary(locale)).listings;

  if (!z.uuid().safeParse(id).success) {
    notFound();
  }

  const cursor = typeof query.cursor === "string" ? query.cursor : undefined;
  let address: string;
  let page: { events: AuditEvent[]; next: string | null };

  try {
    [address, page] = await Promise.all([getListing(id).then((listing) => listing.addressLine1 ?? ""), listAudit(id, cursor)]);
  } catch (error) {
    if (error instanceof ApiError && (error.status === 404 || error.status === 403)) {
      notFound();
    }

    throw error;
  }

  const actions = t.audit.actions as Record<string, string>;
  const targets = t.audit.targets as Record<string, string>;
  const when = new Intl.DateTimeFormat(locale === "tr" ? "tr-TR" : "en-US", { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.audit.title}</h1>
        <p className="mt-1 text-sm text-[var(--color-text-muted)]">{address}</p>
        <Link href={`/dashboard/listings/${encodeURIComponent(id)}`} className="mt-2 inline-block text-sm font-medium text-[var(--color-accent)] underline">
          {t.audit.back}
        </Link>
      </div>

      <Card>
        {page.events.length === 0 ? (
          <p className="text-sm text-[var(--color-text-muted)]">{t.audit.empty}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <caption className="sr-only">{t.audit.title}</caption>
              <thead className="text-[var(--color-text-muted)]">
                <tr>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.audit.columns.when}</th>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.audit.columns.action}</th>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.audit.columns.by}</th>
                  <th scope="col" className="py-2 pr-4 font-medium">{t.audit.columns.item}</th>
                  <th scope="col" className="py-2 font-medium">{t.audit.columns.hash}</th>
                </tr>
              </thead>
              <tbody>
                {page.events.map((event) => {
                  const after = event.afterValue as { contentHash?: string } | null | undefined;

                  return (
                    <tr key={event.id} className="border-t border-[var(--color-border)] text-[var(--color-text)]">
                      <td className="whitespace-nowrap py-2 pr-4">{event.createdAt ? when.format(new Date(event.createdAt)) : ""}</td>
                      <td className="py-2 pr-4">{actions[event.action ?? ""] ?? event.action}</td>
                      <td className="py-2 pr-4">{event.actorId === profile.id ? t.audit.you : event.actorId ? t.audit.someoneElse : t.audit.system}</td>
                      <td className="py-2 pr-4">{targets[event.targetType ?? ""] ?? event.targetType ?? ""}</td>
                      <td className="py-2 font-mono text-xs">{after?.contentHash ? after.contentHash.slice(0, 12) : ""}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {page.next && (
        <Link
          href={`/dashboard/listings/${encodeURIComponent(id)}/audit?cursor=${encodeURIComponent(page.next)}`}
          className="text-sm font-medium text-[var(--color-accent)] underline"
        >
          {t.audit.older}
        </Link>
      )}
    </div>
  );
}
