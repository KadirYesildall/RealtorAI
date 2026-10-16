import { getAuthenticatedProfile } from "@/lib/supabase/getProfile";
import { getDictionary, defaultLocale, isLocale, type Dictionary } from "@/lib/i18n/getDictionary";
import { format } from "@/lib/i18n/format";
import {
  getBrokerageSettings,
  getOwnMembership,
  listMemberships,
  type BrokerageSettings,
  type Membership,
} from "@/lib/api/brokerage";
import { Card } from "@/components/ui/Card";
import { BrokerageSettingsForm } from "@/components/brokerage/BrokerageSettingsForm";
import { MembershipDecisionRow } from "@/components/brokerage/MembershipDecisionRow";
import { MembershipStatusBadge } from "@/components/brokerage/MembershipStatusBadge";

/**
 * T063 — the brokerage screen (FR-057, FR-060, FR-062).
 *
 * A Server Component: every read goes to the .NET API on the server with the session's token,
 * and the API decides what this caller may see. The page only chooses which of four states to
 * render — no brokerage, pending, ended, approved — and, for an approved broker admin, adds the
 * settings form and the member list. An ordinary approved member sees the settings read-only.
 */
export default async function BrokeragePage() {
  const profile = await getAuthenticatedProfile();
  const locale = isLocale(profile.preferredLanguage) ? profile.preferredLanguage : defaultLocale;
  const dict = await getDictionary(locale);
  const t = dict.brokerage;

  // A failure here is a real outage and goes to error.tsx; there is nothing useful to show
  // without knowing the caller's membership.
  const membership = await getOwnMembership();

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-[var(--color-text)]">{t.title}</h1>
        <p className="mt-1 text-sm text-[var(--color-text-muted)]">{t.subtitle}</p>
      </div>

      {membership === null ? (
        <StateCard title={t.none.title} body={t.none.body} />
      ) : (
        <MembershipView membership={membership} dict={dict} locale={locale} />
      )}
    </div>
  );
}

async function MembershipView({
  membership,
  dict,
  locale,
}: {
  membership: Membership;
  dict: Dictionary;
  locale: string;
}) {
  const t = dict.brokerage;
  const brokerage = membership.brokerageName ?? "";
  const status = membership.status ?? "pending";

  const summary = (
    <Card>
      <p className="text-sm text-[var(--color-text-muted)]">{t.membershipLabel}</p>
      <div className="mt-1 flex flex-wrap items-center gap-3">
        <p className="text-lg font-semibold text-[var(--color-text)]">{brokerage}</p>
        <MembershipStatusBadge status={status} labels={t.status} />
        {status === "approved" && membership.role && (
          <span className="text-sm text-[var(--color-text-muted)]">{t.role[membership.role]}</span>
        )}
      </div>
    </Card>
  );

  if (status === "pending") {
    return (
      <>
        {summary}
        <StateCard
          title={format(t.pending.title, { brokerage })}
          body={format(t.pending.body, { brokerage })}
        />
      </>
    );
  }

  if (status === "rejected" || status === "revoked") {
    return (
      <>
        {summary}
        <StateCard
          title={format(status === "rejected" ? t.ended.rejectedTitle : t.ended.revokedTitle, { brokerage })}
          body={t.ended.body}
        />
      </>
    );
  }

  const brokerageId = membership.brokerageId ?? "";
  const isAdmin = membership.role === "broker_admin";

  const [settings, members] = await Promise.all([
    getBrokerageSettings(brokerageId),
    isAdmin ? listMemberships(brokerageId) : Promise.resolve<Membership[]>([]),
  ]);

  const waiting = members.filter((member) => member.status === "pending");
  const others = members.filter(
    (member) => member.status !== "pending" && member.agentId !== membership.agentId
  );

  return (
    <>
      {summary}

      <Card>
        <h2 className="text-lg font-semibold text-[var(--color-text)]">{t.settings.title}</h2>
        <div className="mt-5">
          {isAdmin ? (
            <BrokerageSettingsForm brokerageId={brokerageId} settings={settings} dict={t} />
          ) : (
            <SettingsSummary settings={settings} dict={t} />
          )}
        </div>
      </Card>

      {isAdmin && (
        <Card>
          <h2 className="text-lg font-semibold text-[var(--color-text)]">{t.members.title}</h2>

          <section aria-labelledby="members-waiting" className="mt-5">
            <h3 id="members-waiting" className="text-sm font-medium text-[var(--color-text-muted)]">
              {t.members.pendingTitle}
            </h3>
            {waiting.length === 0 ? (
              <p className="mt-2 text-sm text-[var(--color-text-muted)]">{t.members.pendingEmpty}</p>
            ) : (
              <ul className="divide-y divide-[var(--color-border)]">
                {waiting.map((member) => (
                  <MembershipDecisionRow
                    key={member.id}
                    brokerageId={brokerageId}
                    membership={member}
                    dict={t}
                    locale={locale}
                  />
                ))}
              </ul>
            )}
          </section>

          <section aria-labelledby="members-all" className="mt-6">
            <h3 id="members-all" className="text-sm font-medium text-[var(--color-text-muted)]">
              {t.members.othersTitle}
            </h3>
            {others.length === 0 ? (
              <p className="mt-2 text-sm text-[var(--color-text-muted)]">{t.members.othersEmpty}</p>
            ) : (
              <ul className="divide-y divide-[var(--color-border)]">
                {others.map((member) => (
                  <li key={member.id} className="flex flex-wrap items-center justify-between gap-2 py-3">
                    <span className="text-sm text-[var(--color-text)]">
                      {member.agentName?.trim() || t.members.unnamed}
                    </span>
                    <span className="flex items-center gap-2">
                      {member.role && (
                        <span className="text-sm text-[var(--color-text-muted)]">{t.role[member.role]}</span>
                      )}
                      {member.status && <MembershipStatusBadge status={member.status} labels={t.status} />}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </Card>
      )}
    </>
  );
}

/** Read-only view for approved members who are not admins (FR-057). Presentation only. */
function SettingsSummary({
  settings,
  dict,
}: {
  settings: BrokerageSettings;
  dict: Dictionary["brokerage"];
}) {
  const t = dict.settings;
  const rows: [string, string][] = [
    [t.name, settings.name ?? ""],
    [t.licenseNumber, settings.licenseNumber ?? ""],
    [t.state, settings.state ?? ""],
    [t.targetMlsId, settings.targetMlsId || t.none],
    [t.defaultTone, settings.defaultTone ? t.tones[settings.defaultTone] : t.none],
  ];

  return (
    <div className="space-y-4">
      <p className="text-sm text-[var(--color-text-muted)]">{t.readOnlyNote}</p>
      <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
        {rows.map(([label, value]) => (
          <div key={label}>
            <dt className="text-sm text-[var(--color-text-muted)]">{label}</dt>
            <dd className="text-sm font-medium text-[var(--color-text)]">{value}</dd>
          </div>
        ))}
      </dl>
      {(settings.requiredDisclaimers ?? []).length > 0 && (
        <div>
          <p className="text-sm text-[var(--color-text-muted)]">{t.requiredDisclaimers}</p>
          <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-[var(--color-text)]">
            {(settings.requiredDisclaimers ?? []).map((disclaimer) => (
              <li key={disclaimer}>{disclaimer}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function StateCard({ title, body }: { title: string; body: string }) {
  return (
    <Card>
      <h2 className="text-lg font-semibold text-[var(--color-text)]">{title}</h2>
      <p className="mt-2 text-sm text-[var(--color-text-muted)]">{body}</p>
    </Card>
  );
}
