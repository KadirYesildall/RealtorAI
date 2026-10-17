import Link from "next/link";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale, type Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import { listListings, type Listing } from "@/lib/api/listings";
import { buttonVariants } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { ArchiveListingButton } from "@/components/listing/ArchiveListingButton";
import { ListingStatusBadge } from "@/components/listing/ListingStatusBadge";
import { ShowArchivedToggle } from "@/components/listing/ShowArchivedToggle";

/**
 * T081 — the agent's listing index (FR-067; Story 1 scenario 6's way back in).
 *
 * A Server Component: the list comes from the .NET API with the session's token, scoped by the
 * API to the caller's own listings. Archived listings are left out unless `?archived=1` —
 * FR-067 keeps them, but out of the working view.
 */
export default async function ListingsPage({ searchParams }: PageProps<"/dashboard/listings">) {
  const [profile, query] = await Promise.all([getAuthenticatedProfile(), searchParams]);
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);
  const t = dict.listings;
  const showArchived = query.archived === "1";

  // A failure is a real outage and goes to error.tsx.
  const listings = await listListings(showArchived);

  const updated = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.index.title}</h1>
          <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.index.subtitle}</p>
        </div>
        <Link href="/dashboard/listings/new" className={buttonVariants()}>
          {t.index.newListing}
        </Link>
      </div>

      <ShowArchivedToggle label={t.index.showArchived} checked={showArchived} />

      <Card padding="none">
        {listings.length === 0 ? (
          <p className="p-6 text-sm text-[var(--color-text-muted)]">
            {showArchived ? t.index.emptyArchived : t.index.empty}
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-[var(--color-border)] text-[var(--color-text-muted)]">
                <tr>
                  <th scope="col" className="px-6 py-3 font-medium">{t.index.columns.address}</th>
                  <th scope="col" className="px-6 py-3 font-medium">{t.index.columns.status}</th>
                  <th scope="col" className="px-6 py-3 font-medium">{t.index.columns.updated}</th>
                  <th scope="col" className="px-6 py-3 text-right font-medium">{t.index.columns.actions}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[var(--color-border)]">
                {listings.map((listing) => (
                  <ListingRow
                    key={listing.id}
                    listing={listing}
                    dict={t}
                    updatedLabel={listing.updatedAt ? updated.format(new Date(listing.updatedAt)) : ""}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}

function ListingRow({
  listing,
  dict,
  updatedLabel,
}: {
  listing: Listing;
  dict: Dictionary["listings"];
  updatedLabel: string;
}) {
  const address = `${listing.addressLine1 ?? ""}, ${listing.city ?? ""}`;
  const archived = listing.status === "archived";

  return (
    <tr>
      <td className="px-6 py-4">
        <span className="font-medium text-[var(--color-text)]">{listing.addressLine1}</span>
        <span className="block text-[var(--color-text-muted)]">
          {listing.city}, {listing.state} {listing.postalCode}
        </span>
      </td>
      <td className="px-6 py-4">
        {listing.status && <ListingStatusBadge status={listing.status} labels={dict.status} />}
      </td>
      <td className="px-6 py-4 text-[var(--color-text-muted)]">{updatedLabel}</td>
      <td className="px-6 py-4">
        {!archived && (
          <div className="flex justify-end gap-2">
            <Link
              href={`/dashboard/listings/${encodeURIComponent(listing.id ?? "")}`}
              aria-label={format(dict.index.reviewLabel, { address })}
              className={buttonVariants({ size: "sm" })}
            >
              {dict.index.review}
            </Link>
            <Link
              href={`/dashboard/listings/new?id=${encodeURIComponent(listing.id ?? "")}`}
              aria-label={format(dict.index.continueLabel, { address })}
              className={buttonVariants({ variant: "secondary", size: "sm" })}
            >
              {dict.index.continue}
            </Link>
            <ArchiveListingButton
              listingId={listing.id ?? ""}
              label={dict.index.archive}
              accessibleLabel={format(dict.index.archiveLabel, { address })}
              confirmMessage={format(dict.index.archiveConfirm, { address })}
              errorMessage={dict.errors.archiveFailed}
            />
          </div>
        )}
      </td>
    </tr>
  );
}
