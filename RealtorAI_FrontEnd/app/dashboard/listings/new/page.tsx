import type { ReactNode } from "react";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { z } from "zod";
import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale, type Dictionary } from "@/lib/i18n/getDictionary";
import { ApiError } from "@/lib/api/client";
import { getOwnMembership } from "@/lib/api/brokerage";
import { getListing, getPipeline, listContent, listFindings, type ListingDetail } from "@/lib/api/listings";
import { cn } from "@/lib/utils/cn";
import { Alert } from "@/components/ui/Alert";
import { Card } from "@/components/ui/Card";
import { CreateListingForm } from "@/components/listing/CreateListingForm";
import { ListingDraftEditor } from "@/components/listing/ListingDraftEditor";

/**
 * T080 — the new-listing wizard: basics → photos → what is left before publishing
 * (Story 1).
 *
 * Without `?id=` it starts a listing; with one it resumes that draft, which is how Story 1
 * scenario 6's "navigate away and return" comes back here. Starting a listing first requires a
 * completed profile and a declared brokerage (FR-058, scenario 7): an agent missing either is
 * sent to profile completion and returned afterwards. The API enforces the license on its own
 * (403); the brokerage half of FR-058 is this page's to ask for (research R33).
 */
export default async function NewListingPage({ searchParams }: PageProps<"/dashboard/listings/new">) {
  const [profile, query] = await Promise.all([getAuthenticatedProfile(), searchParams]);
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);
  const t = dict.listings;

  const id = typeof query.id === "string" ? query.id : undefined;

  if (id === undefined) {
    const membership = profile.profileCompletedAt ? await getOwnMembership() : null;
    const hasLiveMembership = membership?.status === "pending" || membership?.status === "approved";

    if (!profile.profileCompletedAt || !hasLiveMembership) {
      redirect("/dashboard/profile/complete?next=/dashboard/listings/new");
    }

    return (
      <WizardShell dict={t} step="basics">
        <Card>
          <h2 className="mb-5 text-lg font-semibold text-[var(--color-text)]">{t.basics.title}</h2>
          <CreateListingForm dict={t} />
        </Card>
      </WizardShell>
    );
  }

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

  if (listing.status === "archived") {
    return (
      <WizardShell dict={t} step="basics" address={listing.addressLine1}>
        <Alert role="status">{t.wizard.archived}</Alert>
        <Link href="/dashboard/listings?archived=1" className="text-sm font-medium text-[var(--color-accent)] underline">
          {t.index.title}
        </Link>
      </WizardShell>
    );
  }

  const [pipeline, content, findings] = await Promise.all([
    getPipeline(id).catch(() => null),
    listContent(id).catch(() => []),
    listFindings(id).catch(() => []),
  ]);

  return (
    <WizardShell dict={t} step="photos" address={listing.addressLine1}>
      <ListingDraftEditor listing={listing} pipeline={pipeline} content={content} findings={findings} dict={t} />
    </WizardShell>
  );
}

type Step = "basics" | "photos" | "progress";

function WizardShell({
  dict,
  step,
  address,
  children,
}: {
  dict: Dictionary["listings"];
  step: Step;
  address?: string;
  children: ReactNode;
}) {
  const steps: Step[] = ["basics", "photos", "progress"];
  const current = steps.indexOf(step);

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-[var(--color-text)]">{dict.wizard.title}</h1>
        <p className="mt-1 text-sm text-[var(--color-text-muted)]">{address ?? dict.wizard.subtitle}</p>
      </div>

      <nav aria-label={dict.wizard.stepsLabel}>
        <ol className="flex flex-wrap gap-2 text-sm">
          {steps.map((name, index) => (
            <li
              key={name}
              aria-current={index === current ? "step" : undefined}
              className={cn(
                "rounded-full border px-3 py-1",
                index === current
                  ? "border-[var(--color-accent)] bg-[var(--color-accent-soft)] font-medium text-[var(--color-accent)]"
                  : "border-[var(--color-border)] text-[var(--color-text-muted)]"
              )}
            >
              {index > 0 && current > 0 ? <a href={`#${name}`}>{dict.wizard.steps[name]}</a> : dict.wizard.steps[name]}
            </li>
          ))}
        </ol>
      </nav>

      {children}
    </div>
  );
}
