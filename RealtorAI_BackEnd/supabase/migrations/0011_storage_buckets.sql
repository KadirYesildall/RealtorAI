-- Feature 002-ai-listing-launch — T024
-- Storage buckets. See data-model.md "Storage policy" and research.md R10 (Supabase Storage
-- rather than MinIO — the spec already specified these buckets and Supabase Storage is
-- already in the stack with signed URLs and an auth model the frontend shares).
--
-- FR-001 (NON-NEGOTIABLE): `listing-photos-original` is WRITE-ONCE. It gets INSERT and SELECT
--         policies and nothing else — no UPDATE, no DELETE, for any application role.
--         Immutability is a storage-layer guarantee, not an application convention.
-- FR-065: reclamation happens only via the out-of-band retention purge job, which runs with
--         its own credential 3 years after listings.deleted_at. The application identity must
--         never hold delete rights here, which is exactly why that job is NOT a Hangfire job.
-- VIII.6: nothing in this system writes generated image data. Normalized variants are
--         produced by non-generative operations only (resize/compress/convert/crop/rotate).

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values
    (
        'listing-photos-original',
        'listing-photos-original',
        false,
        52428800, -- 50 MiB, matching config.toml's storage file_size_limit
        array['image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp']
    ),
    (
        'listing-photos-normalized',
        'listing-photos-normalized',
        false,
        52428800,
        array['image/jpeg', 'image/png', 'image/webp']
    ),
    (
        'brokerage-assets',
        'brokerage-assets',
        false,
        5242880, -- 5 MiB is ample for a logo
        array['image/jpeg', 'image/png', 'image/webp', 'image/svg+xml']
    )
on conflict (id) do nothing;

-- Slice A does not capture seller consent documents (that is Slice B's delayed-marketing
-- flow), so the `seller-consents` bucket is deliberately not created yet — YAGNI per
-- Principle V. It appears in the spec's bucket list for Slice B.

-- ──────────────────── listing-photos-original: WRITE-ONCE ────────────────────
-- An agent may upload into their own listing's prefix and read it back. There is
-- intentionally no UPDATE or DELETE policy: the absence is the guarantee.
--
-- Object key convention: {listing_id}/{photo_id}.{ext} — so the first path segment is the
-- listing id, which is what the ownership check below joins on.

create policy "originals_insert_own_listing"
    on storage.objects for insert
    to authenticated
    with check (
        bucket_id = 'listing-photos-original'
        and exists (
            select 1 from public.listings l
            where l.id::text = (storage.foldername(name))[1]
              and l.agent_id = auth.uid()
        )
    );

create policy "originals_select_own_or_broker_admin"
    on storage.objects for select
    to authenticated
    using (
        bucket_id = 'listing-photos-original'
        and exists (
            select 1 from public.listings l
            where l.id::text = (storage.foldername(name))[1]
              and (l.agent_id = auth.uid()
                   or public.can_read_agent_as_broker_admin(l.agent_id))
        )
    );

-- NOTE: no "originals_update_*" and no "originals_delete_*" policy exists, by design.

-- ──────────────────── listing-photos-normalized: replaceable ────────────────────
-- Normalized variants are derived data and may be regenerated, so UPDATE and DELETE are
-- permitted here. Losing one costs a re-normalization; losing an original is unrecoverable.

create policy "normalized_all_own_listing"
    on storage.objects for all
    to authenticated
    using (
        bucket_id = 'listing-photos-normalized'
        and exists (
            select 1 from public.listings l
            where l.id::text = (storage.foldername(name))[1]
              and l.agent_id = auth.uid()
        )
    )
    with check (
        bucket_id = 'listing-photos-normalized'
        and exists (
            select 1 from public.listings l
            where l.id::text = (storage.foldername(name))[1]
              and l.agent_id = auth.uid()
        )
    );

create policy "normalized_select_broker_admin"
    on storage.objects for select
    to authenticated
    using (
        bucket_id = 'listing-photos-normalized'
        and exists (
            select 1 from public.listings l
            where l.id::text = (storage.foldername(name))[1]
              and public.can_read_agent_as_broker_admin(l.agent_id)
        )
    );

-- ──────────────────── brokerage-assets: broker admin managed ────────────────────
-- Key convention: {brokerage_id}/{filename}

create policy "brokerage_assets_select_approved_members"
    on storage.objects for select
    to authenticated
    using (
        bucket_id = 'brokerage-assets'
        and public.is_approved_member(((storage.foldername(name))[1])::uuid)
    );

create policy "brokerage_assets_write_broker_admin"
    on storage.objects for all
    to authenticated
    using (
        bucket_id = 'brokerage-assets'
        and public.is_approved_broker_admin(((storage.foldername(name))[1])::uuid)
    )
    with check (
        bucket_id = 'brokerage-assets'
        and public.is_approved_broker_admin(((storage.foldername(name))[1])::uuid)
    );
