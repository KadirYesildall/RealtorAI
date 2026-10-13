-- Feature 002-ai-listing-launch — T023
-- MLS rules as DATA, not code, so adding an MLS or reacting to a rule change needs no
-- deployment. See data-model.md "MLS rule profile" and research.md R2.
--
-- FAIL CLOSED: the CRMLS row below is inserted with its numeric limits NULL. The compliance
-- engine must raise a blocking "rule profile incomplete" finding when a required value is
-- missing, rather than generating against or checking against a guessed limit. CRMLS's
-- actual published values were NOT available in this environment and were deliberately not
-- invented (Principle III) — a plausible-but-wrong character limit produces content that
-- looks shippable and fails only at MLS submission.

create table public.mls_rule_profiles (
    id text primary key,
    display_name text not null,

    -- ⚠ All NULL until populated from CRMLS's published rules (task T160).
    public_remarks_max_chars int,
    private_remarks_max_chars int,
    photo_min_width int,
    photo_min_height int,
    photo_min_count int,
    photo_max_count int,

    -- Detector patterns are code; which detectors are on, and at what severity, is data.
    forbidden_in_public_remarks jsonb not null default '[]'::jsonb,
    -- RESO field keys that must be present AND confirmed before approval.
    required_fields jsonb not null default '[]'::jsonb,
    -- CRMLS permits only virtual staging to be presented as an alteration; anything else is
    -- a blocking finding (FR-020).
    allowed_alteration_types jsonb not null default '[]'::jsonb,
    -- ⚠ Exact approved label strings pending the AB 723 legal review (task T161).
    disclosure_labels jsonb not null default '[]'::jsonb,
    -- CRMLS restrictions on standard RESO lookups.
    enumerated_field_lookups jsonb not null default '{}'::jsonb,

    rulebook_version text,
    verified_at timestamptz,

    constraint mls_profile_counts_sane check (
        (photo_min_count is null or photo_min_count >= 0)
        and (photo_max_count is null or photo_max_count >= 1)
        and (photo_min_count is null or photo_max_count is null
             or photo_max_count >= photo_min_count)
    )
);

comment on table public.mls_rule_profiles is
    'Per-MLS rule configuration. A NULL required value means "not yet verified" and MUST '
    'produce a blocking finding — never a silent pass (research.md R2).';
comment on column public.mls_rule_profiles.verified_at is
    'When these values were last confirmed against the published rulebook. NULL means the '
    'profile is incomplete and the engine fails closed.';

-- Readable by any authenticated user (the UI shows limits and labels); writes are platform
-- administration only, like the AI registry.
alter table public.mls_rule_profiles enable row level security;

create policy "mls_rule_profiles_select_authenticated"
    on public.mls_rule_profiles for select
    to authenticated
    using (true);

create policy "mls_rule_profiles_write_platform_admin"
    on public.mls_rule_profiles for all
    using (public.is_platform_admin())
    with check (public.is_platform_admin());

grant select, insert, update on public.mls_rule_profiles to authenticated;

-- ───────────────────────── CRMLS: deliberately incomplete ─────────────────────────
-- allowed_alteration_types and forbidden_in_public_remarks are safe to seed because they
-- come from the feature spec itself (FR-019, FR-020), not from CRMLS's numeric rulebook.
-- Everything numeric stays NULL, and verified_at stays NULL, so the engine fails closed.

insert into public.mls_rule_profiles (
    id,
    display_name,
    forbidden_in_public_remarks,
    allowed_alteration_types,
    required_fields,
    rulebook_version,
    verified_at
)
values (
    'CRMLS',
    'California Regional MLS',
    '[
       {"detector": "phone_number", "severity": "blocking"},
       {"detector": "email_address", "severity": "blocking"},
       {"detector": "url",           "severity": "blocking"},
       {"detector": "agent_name",    "severity": "blocking"},
       {"detector": "brokerage_name","severity": "blocking"}
     ]'::jsonb,
    '["virtual_staging"]'::jsonb,
    '[]'::jsonb,
    null,
    null
);
