-- Feature 002-ai-listing-launch — T018
-- Listings and per-field provenance. See data-model.md "Listings".
--
-- FR-005: every field carries provenance.
-- FR-006: AI-derived facts require explicit confirmation before publishing.
-- FR-007: measurements and legal facts are NEVER AI-inferred — enforced by a check
--         constraint here, not only in application code.
-- FR-061: brokerage_id is nullable so a pending-membership agent can still create listings.
-- FR-066: row_version supports optimistic concurrency.
-- FR-067: soft delete only (deleted_at + archived status); no hard-delete path exists,
--         because removing a listing row would either destroy immutable audit records or be
--         blocked by their foreign key (0009).

create type public.provenance as enum (
    'agent_entered',
    'public_record',
    'ai_detected_from_photo',
    'ai_suggested'
);

create type public.listing_status as enum (
    'draft',
    'analyzing',
    'ready_for_review',
    'approved',
    'archived'
);

create table public.listings (
    id uuid primary key default gen_random_uuid(),
    agent_id uuid not null references public.profiles (id) on delete cascade,
    -- Nullable by design: FR-061 allows a pending-membership agent to draft listings.
    brokerage_id uuid references public.brokerages (id) on delete set null,
    status public.listing_status not null default 'draft',

    address_line1 text not null,
    city text not null,
    state char(2) not null,
    postal_code text not null,
    property_type text not null,
    list_price numeric(12, 2),
    bedrooms int,
    -- numeric so half-baths are representable.
    bathrooms numeric(3, 1),
    -- Never copied into public remarks; the MLS-rule check blocks contact info there (FR-019).
    agent_notes text,

    -- Slice B seam, consumed in Slice A to resolve the active MLS rule profile (0010).
    target_mls_id text,
    cover_photo_id uuid,

    row_version int not null default 1,
    deleted_at timestamptz,

    created_at timestamptz not null default timezone('utc'::text, now()),
    updated_at timestamptz not null default timezone('utc'::text, now()),

    constraint listings_bedrooms_nonneg check (bedrooms is null or bedrooms >= 0),
    constraint listings_bathrooms_nonneg check (bathrooms is null or bathrooms >= 0),
    constraint listings_price_nonneg check (list_price is null or list_price >= 0),
    -- An archived listing must carry its deletion timestamp and vice versa (FR-067).
    constraint listings_archive_consistent check (
        (status = 'archived' and deleted_at is not null)
        or (status <> 'archived' and deleted_at is null)
    )
);

comment on table public.listings is
    'A property listing. Soft-deleted only (FR-067) so the append-only audit trail survives.';
comment on column public.listings.row_version is
    'FR-066 optimistic concurrency. Incremented on every write; a conditional update against '
    'a stale value affects 0 rows and surfaces as HTTP 409 with current state.';
comment on column public.listings.deleted_at is
    'FR-067 soft delete. Also starts the FR-065 3-year original-photo retention clock.';

create index listings_agent_idx on public.listings (agent_id);
create index listings_brokerage_idx on public.listings (brokerage_id);
create index listings_status_idx on public.listings (status);
-- Working views filter out soft-deleted rows; index the live set only.
create index listings_live_idx on public.listings (agent_id) where deleted_at is null;

-- ───────────────────────────── listing_fields ─────────────────────────────

create table public.listing_fields (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,
    -- RESO Data Dictionary field name, e.g. InteriorFeatures. Validated against Libs.Reso.
    reso_field_key text not null,
    -- Scalar or array, so multi-value enumerated fields fit without a side table.
    value jsonb not null,
    provenance public.provenance not null,
    confirmed boolean not null default false,
    confirmed_by uuid references public.profiles (id),
    confirmed_at timestamptz,
    -- Public-record source + retrieval date (FR-008).
    source_note text,
    row_version int not null default 1,

    constraint listing_fields_unique_key unique (listing_id, reso_field_key),

    -- A confirmation is either fully recorded or absent (FR-006).
    constraint listing_fields_confirmation_complete check (
        (confirmed = false and confirmed_by is null and confirmed_at is null)
        or (confirmed = true and confirmed_by is not null and confirmed_at is not null)
    ),

    -- FR-007 (NON-NEGOTIABLE, enforced in the database): measurements, year built, taxes,
    -- HOA fees, school district, and zoning may only ever be agent-entered or sourced from
    -- public records. AI must never infer them, so AI provenance is rejected outright.
    constraint listing_fields_no_ai_measurements check (
        reso_field_key not in (
            'LivingArea',
            'LivingAreaSquareFeet',
            'LotSizeSquareFeet',
            'LotSizeAcres',
            'YearBuilt',
            'TaxAnnualAmount',
            'TaxYear',
            'AssociationFee',
            'AssociationFeeFrequency',
            'HighSchoolDistrict',
            'ElementarySchoolDistrict',
            'MiddleOrJuniorSchoolDistrict',
            'Zoning'
        )
        or provenance in ('agent_entered', 'public_record')
    )
);

comment on table public.listing_fields is
    'One RESO field per row with its provenance. Per-field provenance is what makes FR-006 '
    'enforceable: content cannot be approved while it rests on an unconfirmed AI fact.';
comment on constraint listing_fields_no_ai_measurements on public.listing_fields is
    'FR-007: measurement and legal facts are never AI-inferred.';

create index listing_fields_listing_idx on public.listing_fields (listing_id);
-- Fast lookup of the unconfirmed AI facts that block approval (FR-014).
create index listing_fields_unconfirmed_ai_idx
    on public.listing_fields (listing_id)
    where confirmed = false
      and provenance in ('ai_detected_from_photo', 'ai_suggested');

-- ───────────────────────────── Row Level Security ─────────────────────────────

alter table public.listings enable row level security;
alter table public.listing_fields enable row level security;

-- Owner has full access to their own listings.
create policy "listings_owner_all"
    on public.listings for all
    using (agent_id = auth.uid())
    with check (agent_id = auth.uid());

-- Broker admin read-only, and only where the owner's membership is approved (FR-050).
create policy "listings_broker_admin_select"
    on public.listings for select
    using (public.can_read_agent_as_broker_admin(agent_id));

-- listing_fields inherit access from the parent listing.
create policy "listing_fields_via_listing"
    on public.listing_fields for all
    using (
        exists (
            select 1 from public.listings l
            where l.id = listing_id and l.agent_id = auth.uid()
        )
    )
    with check (
        exists (
            select 1 from public.listings l
            where l.id = listing_id and l.agent_id = auth.uid()
        )
    );

create policy "listing_fields_broker_admin_select"
    on public.listing_fields for select
    using (
        exists (
            select 1 from public.listings l
            where l.id = listing_id
              and public.can_read_agent_as_broker_admin(l.agent_id)
        )
    );

-- ───────────────────────────── Grants ─────────────────────────────
-- No DELETE grant on listings: FR-067 permits soft delete only.

grant select, insert, update on public.listings to authenticated;
grant select, insert, update, delete on public.listing_fields to authenticated;
