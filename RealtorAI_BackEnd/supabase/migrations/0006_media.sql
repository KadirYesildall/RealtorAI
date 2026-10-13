-- Feature 002-ai-listing-launch — T019
-- Photos, their AI analysis, and embeddings. See data-model.md "Media".
--
-- FR-001 (NON-NEGOTIABLE): originals are immutable. The storage-side guarantee lives in
--         0011 (no UPDATE/DELETE policy on the originals bucket); here we keep the checksum
--         that proves it.
-- FR-003: 1–100 photos per listing, JPEG/PNG/HEIC/WebP.
-- FR-020: an agent-declared alteration requires ALL THREE of alteration_type, a disclosure
--         label, and the paired unaltered original — enforced by a check constraint. Active
--         in this release because CRMLS is a California MLS (AB 723).
-- VIII.6: the system never generates or generatively alters pixels. Nothing here writes
--         image data; `altered` records what the AGENT did elsewhere.

create type public.room_type as enum (
    'kitchen',
    'living_room',
    'primary_bedroom',
    'bedroom',
    'bathroom',
    'exterior_front',
    'backyard',
    'pool',
    'garage',
    'view',
    'floor_plan',
    'other'
);

-- Only virtual staging may be presented as an alteration under the CRMLS profile; anything
-- else is a blocking compliance finding (checked in Libs.Compliance, not here, because the
-- allowed set is per-MLS configuration in 0010).
create type public.alteration_type as enum ('virtual_staging', 'other');

create table public.photos (
    id uuid primary key default gen_random_uuid(),
    listing_id uuid not null references public.listings (id) on delete cascade,

    -- Write-once bucket; never updated or deleted by any application role (FR-001).
    original_storage_key text not null,
    -- Long edge ~1280px, EXIF/GPS stripped. This is the only variant sent to a vision model
    -- (VIII.4.c). The original is never transmitted.
    normalized_storage_key text,

    checksum_sha256 text not null,
    width int,
    height int,
    bytes bigint,
    mime_type text not null,

    order_index int not null,
    caption text,
    is_cover boolean not null default false,

    -- Agent declaration about work done OUTSIDE this system (FR-020).
    altered boolean not null default false,
    alteration_type public.alteration_type,
    disclosure_label text,
    paired_original_id uuid references public.photos (id) on delete set null,

    uploaded_at timestamptz not null default timezone('utc'::text, now()),

    constraint photos_supported_mime check (
        mime_type in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp')
    ),
    constraint photos_order_nonneg check (order_index >= 0),

    -- FR-020's three-part rule, enforced in the database so the UI cannot be the only guard.
    constraint photos_altered_requires_disclosure check (
        altered = false
        or (
            alteration_type is not null
            and disclosure_label is not null
            and paired_original_id is not null
        )
    ),
    -- Conversely, disclosure metadata is meaningless without the altered flag.
    constraint photos_unaltered_has_no_disclosure check (
        altered = true
        or (
            alteration_type is null
            and disclosure_label is null
            and paired_original_id is null
        )
    ),
    constraint photos_not_own_pair check (paired_original_id is distinct from id)
);

comment on table public.photos is
    'Uploaded listing photos. Originals are immutable (FR-001); the system never alters '
    'pixels (VIII.6). The altered flag records agent-performed edits made elsewhere.';
comment on constraint photos_altered_requires_disclosure on public.photos is
    'FR-020 / AB 723: an altered photo needs a type, a disclosure label, and its unaltered '
    'original paired for adjacent placement.';

-- Exact duplicates are rejected at upload with a reference to the existing photo.
create unique index photos_unique_checksum_per_listing
    on public.photos (listing_id, checksum_sha256);

-- Deferrable so a whole reorder lands in one transaction without transient collisions.
create unique index photos_unique_order_per_listing
    on public.photos (listing_id, order_index);
alter table public.photos
    add constraint photos_order_unique_deferrable
    unique using index photos_unique_order_per_listing deferrable initially deferred;

-- At most one cover photo per listing.
create unique index photos_one_cover_per_listing
    on public.photos (listing_id)
    where is_cover;

create index photos_listing_order_idx on public.photos (listing_id, order_index);

-- listings.cover_photo_id could not be constrained in 0005 because photos did not exist yet.
alter table public.listings
    add constraint listings_cover_photo_fk
    foreign key (cover_photo_id) references public.photos (id) on delete set null;

-- ───────────────────────────── photo_analyses ─────────────────────────────

create table public.photo_analyses (
    id uuid primary key default gen_random_uuid(),
    photo_id uuid not null references public.photos (id) on delete cascade,

    room_type public.room_type,
    room_type_confidence numeric(4, 3),
    -- Candidate tags with provenance ai_detected_from_photo; unconfirmed until the agent says so.
    feature_tags jsonb not null default '[]'::jsonb,
    -- blurry | too_dark | overexposed | tilted_horizon | below_min_resolution | near_duplicate
    quality_flags jsonb not null default '[]'::jsonb,
    -- people_visible | personal_documents | house_number | license_plate |
    -- watermark_or_branding | possible_prior_alteration
    content_flags jsonb not null default '[]'::jsonb,
    alt_text text,

    cover_recommended boolean not null default false,
    order_recommendation int,
    -- FR-010 requires a stated reason for each suggestion.
    recommendation_reason text,

    -- VIII.5 provenance: which model and which prompt version produced this.
    model_id text,
    prompt_version text,
    prompt_hash text,
    raw_output jsonb,

    -- Per-photo failure isolation (Story 2 scenario 8).
    failed boolean not null default false,
    failure_reason text,

    created_at timestamptz not null default timezone('utc'::text, now()),

    constraint photo_analyses_one_per_photo unique (photo_id),
    constraint photo_analyses_confidence_range check (
        room_type_confidence is null
        or (room_type_confidence >= 0 and room_type_confidence <= 1)
    ),
    constraint photo_analyses_failure_has_reason check (
        failed = false or failure_reason is not null
    )
);

comment on table public.photo_analyses is
    'AI result per photo. Agent overrides are recorded in audit_events; this row keeps the '
    'original model output for reproducibility.';

create index photo_analyses_photo_idx on public.photo_analyses (photo_id);

-- ───────────────────────────── photo_embeddings ─────────────────────────────
--
-- ⚠ DIMENSION IS PROVISIONAL. 768 is the expected hidden size of
-- google/siglip2-base-patch16-224, but the model was NOT loaded to confirm it (Python is not
-- installed on this machine, and the registry entry is disabled pending verification —
-- research.md R1). The API MUST reject an embedding whose reported dimension differs from
-- this column rather than truncating or padding, and T094 asserts the match. Changing this
-- number is a one-line migration while no data exists; a silent mismatch is not acceptable.

create table public.photo_embeddings (
    photo_id uuid primary key references public.photos (id) on delete cascade,
    embedding vector(768) not null,
    model_id text not null
);

comment on table public.photo_embeddings is
    'Image embeddings for near-duplicate detection and zero-shot room pre-classification. '
    'Service-role only — never client-readable. Dimension is provisional pending model '
    'verification (research.md R1).';

-- Cosine distance matches how SigLIP-family embeddings are compared.
create index photo_embeddings_hnsw_idx
    on public.photo_embeddings
    using hnsw (embedding vector_cosine_ops);

-- ───────────────────────────── Row Level Security ─────────────────────────────

alter table public.photos enable row level security;
alter table public.photo_analyses enable row level security;
alter table public.photo_embeddings enable row level security;

create policy "photos_via_listing"
    on public.photos for all
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

create policy "photos_broker_admin_select"
    on public.photos for select
    using (
        exists (
            select 1 from public.listings l
            where l.id = listing_id
              and public.can_read_agent_as_broker_admin(l.agent_id)
        )
    );

-- Analyses are readable through the parent listing but written only by the service role,
-- which bypasses RLS — so there is deliberately no INSERT/UPDATE policy here.
create policy "photo_analyses_select_via_listing"
    on public.photo_analyses for select
    using (
        exists (
            select 1
            from public.photos p
            join public.listings l on l.id = p.listing_id
            where p.id = photo_id
              and (l.agent_id = auth.uid()
                   or public.can_read_agent_as_broker_admin(l.agent_id))
        )
    );

-- photo_embeddings: no policy at all. RLS is enabled, so with no policy nothing is visible
-- to a client role and only the service role can touch it.
--
-- CORRECTION (see 0012_privilege_hardening.sql): the "nothing is granted" half of this was
-- false. Supabase's default privileges on the public schema grant ALL on new tables to anon
-- and authenticated, so this table received every privilege including TRUNCATE the moment it
-- was created. RLS-with-no-policy still denied reads and writes, but TRUNCATE is not subject
-- to RLS. 0012 revokes the inherited privileges and makes the statement above true.

-- ───────────────────────────── Grants ─────────────────────────────

grant select, insert, update, delete on public.photos to authenticated;
grant select on public.photo_analyses to authenticated;
-- photo_embeddings: intentionally no grant to authenticated.
--
-- Writing no `grant` is NOT the same as granting nothing here — see the correction above and
-- 0012_privilege_hardening.sql, which revokes what the schema default had already granted.
