import Link from "next/link";
import { notFound } from "next/navigation";
import { z } from "zod";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { ApiError } from "@/lib/api/client";
import { getListing, getPipeline, type ListingDetail } from "@/lib/api/listings";
import { Alert } from "@/components/ui/Alert";
import { ApprovalBar } from "@/components/approval/ApprovalBar";
import { FieldsReview } from "@/components/listing/FieldsReview";
import { ListingDraftEditor } from "@/components/listing/ListingDraftEditor";
import { ListingStatusBadge } from "@/components/listing/ListingStatusBadge";

/**
 * T150 — the single review-and-approve screen (FR-027; Story 5).
 *
 * Everything the agent is responsible for, on one page and from one read of the listing: the
 * facts with their provenance and confirm actions, the basics, photos and their analysis, the
 * content, the compliance review, and the approve control. Every edit saves in place, without a
 * navigation, and is audited (scenario 2). The audit trail is one link away.
 */
export default async function ReviewPage({ params }: PageProps<"/dashboard/listings/[id]">) {
  const [{ id }, profile] = await Promise.all([params, getAuthenticatedProfile()]);
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const t = (await getDictionary(locale)).listings;

  if (!z.uuid().safeParse(id).success) {
    notFound();
  }

  let listing: ListingDetail;

  try {
    listing = await getListing(id);
  } catch (error) {
    // Not found and not permitted read the same: the API does not reveal which (FR-063).
    if (error instanceof ApiError && (error.status === 404 || error.status === 403)) {
      notFound();
    }

    throw error;
  }

  const pipeline = await getPipeline(id).catch(() => null);

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="break-words text-2xl font-semibold text-[var(--color-text)]">{listing.addressLine1}</h1>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">
            {format(t.review.place, { city: listing.city ?? "", state: listing.state ?? "", postalCode: listing.postalCode ?? "" })}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {listing.status && <ListingStatusBadge status={listing.status} labels={t.status} />}
          <Link href={`/dashboard/listings/${encodeURIComponent(id)}/audit`} className="text-sm font-medium text-[var(--color-accent)] underline">
            {t.review.audit}
          </Link>
        </div>
      </header>

      {listing.status === "archived" ? (
        <Alert role="status">{t.wizard.archived}</Alert>
      ) : (
        <>
          <FieldsReview listingId={id} fields={listing.fields ?? []} dict={t} />
          <ListingDraftEditor listing={listing} pipeline={pipeline} content={listing.contentDrafts ?? []} findings={listing.findings ?? []} dict={t} />
          <ApprovalBar listingId={id} outputs={listing.outputs ?? []} fields={listing.fields ?? []} findings={listing.findings ?? []} dict={t} />
        </>
      )}
    </div>
  );
}
