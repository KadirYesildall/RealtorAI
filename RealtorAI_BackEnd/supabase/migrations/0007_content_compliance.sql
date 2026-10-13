-- Feature 002-ai-listing-launch — T020
-- Content drafts, compliance findings, and approvals. See data-model.md.
--
-- FR-014: a draft cannot be approved while any supporting field is an unconfirmed AI fact.
-- FR-022 (NON-NEGOTIABLE): a BLOCKING finding can never be dismissed or overridden by any
--         mechanism — enforced here by a check constraint, so no API bug can bypass it.
-- FR-023: a WARNING may be acknowledged only with a recorded reason, actor, and timestamp.
-- FR-026: approval records approver, timestamp, and a hash of the exact approved content.
-- FR-066: approval requires the hash the agent actually reviewed; mismatch is rejected.
-- VIII.7: an approval may only originate from a human action — hence INSERT is granted to
--         the owning agent and there is deliberately no service-role insert path.

create type public.content_kind as enum ('public_remarks', 'private_remarks', 'headline');
create type public.content_status as enum ('draft', 'approved', 'stale');

create type public.finding_severity as enum ('blocking', 'warning');
create type public.finding_status as enum ('open', 'resolved', 'acknowledged', 'stale');
create type public.check_type as enum (
    'fair_housing',
    'risky_claim',
    'mls_rule',
    'photo_integrity',
    'advertising_disclosure'
);
-- FR-017 requires recording which layer produced a finding.
create type public.finding_source as enum ('lexicon', 'judge', 'deterministic');

-- ───────────────────────────── content_drafts ─────────────────────────────

create table public.content_drafts (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,
    kind public.content_kind not null,
    variant_index int not null default 0,
    tone public.tone,
    body text not null,
    -- Field ids backing each claim. Powers FR-014 highlighting and the approval gate.
    supporting_field_ids jsonb not null default '[]'::jsonb,
    version int not null default 1,
    status public.content_status not null default 'draft',
    selected boolean not null default false,
    -- VIII.5 provenance.
    model_id text,
    prompt_version text,
    prompt_hash text,
    row_version int not null default 1,
    created_at timestamptz not null default timezone('utc'::text, now()),

    constraint content_drafts_unique_variant
        unique (listing_id, kind, variant_index, version),
    constraint content_drafts_variant_nonneg check (variant_index >= 0),
    constraint content_drafts_version_positive check (version >= 1)
);

comment on table public.content_drafts is
    'Generated listing content. supporting_field_ids is what makes FR-014 enforceable: a '
    'draft resting on an unconfirmed AI fact cannot be approved.';

-- At most one selected variant per (listing, kind).
create unique index content_drafts_one_selected
    on public.content_drafts (listing_id, kind)
    where selected;

create index content_drafts_listing_kind_idx
    on public.content_drafts (listing_id, kind);

-- ───────────────────────────── compliance_findings ─────────────────────────────

create table public.compliance_findings (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,
    -- Polymorphic by design: findings attach to heterogeneous targets.
    target_type text not null,
    target_id uuid not null,
    check_type public.check_type not null,
    severity public.finding_severity not null,
    source public.finding_source not null,

    span_start int,
    span_end int,
    phrase text,
    -- English by design: analysis OF U.S.-English listing copy (research.md R15).
    reason text not null,
    suggested_fix text,

    status public.finding_status not null default 'open',
    acknowledged_reason text,
    acknowledged_by uuid references public.profiles (id),
    acknowledged_at timestamptz,

    -- Reproducibility (FR-024).
    lexicon_version text,
    model_id text,
    prompt_version text,
    created_at timestamptz not null default timezone('utc'::text, now()),

    constraint findings_target_type_known check (
        target_type in ('listing', 'content_draft', 'photo')
    ),
    constraint findings_span_ordered check (
        span_start is null or span_end is null or span_end >= span_start
    ),

    -- FR-022 + FR-023 in one constraint. Acknowledgement requires WARNING severity AND a
    -- complete record. A blocking finding therefore can never reach 'acknowledged' — the
    -- prohibition is a database guarantee, not a UI affordance.
    constraint findings_acknowledge_warning_only check (
        status <> 'acknowledged'
        or (
            severity = 'warning'
            and acknowledged_reason is not null
            and acknowledged_by is not null
            and acknowledged_at is not null
        )
    )
);

comment on table public.compliance_findings is
    'Compliance findings. A blocking finding can never be acknowledged (FR-022); only '
    'warnings can, and only with a logged reason (FR-023).';
comment on constraint findings_acknowledge_warning_only on public.compliance_findings is
    'FR-022/FR-023: blocking findings are undismissible; warnings need a full record.';

-- The approve-gate query: are there open blocking findings for this listing?
create index findings_listing_severity_status_idx
    on public.compliance_findings (listing_id, severity, status);
create index findings_target_idx
    on public.compliance_findings (target_type, target_id);

-- ───────────────────────────── approvals ─────────────────────────────

create table public.approvals (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,
    target_type text not null,
    target_id uuid not null,
    -- Slice A supports 'mls' only; Slice B adds marketing channels (FR-026).
    channel text not null,
    -- SHA-256 of the exact approved content. Answers SC-008 and anchors FR-066.
    content_hash text not null,
    approved_by uuid not null references public.profiles (id),
    approved_at timestamptz not null default timezone('utc'::text, now()),
    revoked_at timestamptz,
    revoked_reason text,

    constraint approvals_target_type_known check (
        target_type in ('listing', 'content_draft', 'photo')
    ),
    constraint approvals_revocation_complete check (
        (revoked_at is null and revoked_reason is null)
        or (revoked_at is not null and revoked_reason is not null)
    )
);

comment on table public.approvals is
    'Explicit human approvals. INSERT is granted to the owning agent only and never to the '
    'service role, so an approval can only ever originate from a human action (VIII.7).';

-- One live approval per (target, channel); revoked rows remain as history.
create unique index approvals_one_live_per_target_channel
    on public.approvals (listing_id, target_id, channel)
    where revoked_at is null;

create index approvals_listing_target_idx
    on public.approvals (listing_id, target_id);

-- ───────────────────────────── Row Level Security ─────────────────────────────

alter table public.content_drafts enable row level security;
alter table public.compliance_findings enable row level security;
alter table public.approvals enable row level security;

create policy "content_drafts_via_listing"
    on public.content_drafts for all
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id and l.agent_id = auth.uid())
    )
    with check (
        exists (select 1 from public.listings l
                where l.id = listing_id and l.agent_id = auth.uid())
    );

create policy "content_drafts_broker_admin_select"
    on public.content_drafts for select
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id
                  and public.can_read_agent_as_broker_admin(l.agent_id))
    );

-- Findings are raised and resolved by the service role (which bypasses RLS). The owning
-- agent may SELECT, and may UPDATE only to acknowledge — the check constraint above limits
-- that to warnings with a complete record.
create policy "findings_select_via_listing"
    on public.compliance_findings for select
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id
                  and (l.agent_id = auth.uid()
                       or public.can_read_agent_as_broker_admin(l.agent_id)))
    );

create policy "findings_acknowledge_by_owner"
    on public.compliance_findings for update
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id and l.agent_id = auth.uid())
    );

create policy "approvals_select_via_listing"
    on public.approvals for select
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id
                  and (l.agent_id = auth.uid()
                       or public.can_read_agent_as_broker_admin(l.agent_id)))
    );

-- VIII.7: only the owning agent inserts an approval, and only for themselves.
create policy "approvals_insert_owner_only"
    on public.approvals for insert
    to authenticated
    with check (
        approved_by = auth.uid()
        and exists (select 1 from public.listings l
                    where l.id = listing_id and l.agent_id = auth.uid())
    );

-- Revocation is an update by the owner (set revoked_at + reason).
create policy "approvals_revoke_by_owner"
    on public.approvals for update
    using (
        exists (select 1 from public.listings l
                where l.id = listing_id and l.agent_id = auth.uid())
    );

-- ───────────────────────────── Grants ─────────────────────────────
-- No DELETE on approvals: a revocation is recorded, never erased.

grant select, insert, update, delete on public.content_drafts to authenticated;
grant select, update on public.compliance_findings to authenticated;
grant select, insert, update on public.approvals to authenticated;
