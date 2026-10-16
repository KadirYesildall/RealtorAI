import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale } from "@/lib/i18n/getDictionary";
import { getOwnMembership } from "@/lib/api/brokerage";
import { safeNextPath } from "@/lib/validation/listing";
import { Card } from "@/components/ui/Card";
import { ProfileCompletionForm } from "@/components/profile/ProfileCompletionForm";

/**
 * T079 — profile completion: license number, license state, and brokerage self-declaration
 * (FR-058, FR-060; Story 1 scenarios 7 and 8).
 *
 * Reached on its own or on the way to creating a listing, with `?next=` naming where to return.
 * The brokerage section appears only when the agent has no live membership: a pending or
 * approved one is shown instead, because this form does not change memberships — declaring a
 * second one would be refused by the API anyway.
 */
export default async function ProfileCompletePage({ searchParams }: PageProps<"/dashboard/profile/complete">) {
  const [profile, query] = await Promise.all([getAuthenticatedProfile(), searchParams]);
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);
  const t = dict.profileCompletion;

  // A failure here is a real outage and goes to error.tsx: whether to ask for a brokerage
  // depends on it.
  const membership = await getOwnMembership();
  const hasLiveMembership = membership?.status === "pending" || membership?.status === "approved";

  return (
    <div className="mx-auto max-w-2xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.title}</h1>
        <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.subtitle}</p>
      </div>

      <Card>
        <ProfileCompletionForm
          next={safeNextPath(query.next)}
          dict={t}
          statusLabels={dict.brokerage.status}
          defaults={{
            licenseNumber: profile.licenseNumber ?? "",
            licenseState: profile.licenseState ?? "",
          }}
          currentMembership={
            hasLiveMembership && membership
              ? { brokerageName: membership.brokerageName ?? "", status: membership.status ?? "pending" }
              : null
          }
        />
      </Card>
    </div>
  );
}
