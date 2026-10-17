-- Feature 002-ai-listing-launch — T154
-- The FR-065 retention purge: the one legitimate deletion of an original. See research R38.
--
-- FR-065: originals are kept for a configurable period (3 years) after their listing is deleted,
--         then purged by an out-of-band operations job with its own credential, and the purge is
--         logged. No application role may delete an original at any point (FR-001).
--
-- How the guard opens, and for whom:
--   * Deleting an original's bytes goes through the Storage API, which acts as service_role — the
--     credential the API holds. So the write-once trigger (0014) cannot tell the purge job from
--     the API by role. It asks instead whether the object has a purge authorization AND its
--     listing has been deleted for longer than the retention period, both at the moment of the
--     delete.
--   * Only retention_purger can write an authorization. Neither service_role nor authenticated nor
--     anon holds any privilege on the ops schema, so the API's credentials alone can never open
--     the guard, and even the purge credential cannot open it for an original still under
--     retention.
--   * retention_purger is created WITHOUT a login. Each environment grants it one out of band
--     (`alter role retention_purger login password ...`), so no password is ever in a migration.

create schema if not exists ops;

revoke all on schema ops from public, anon, authenticated, service_role;

-- ───────────────────────────── settings ─────────────────────────────

create table ops.retention_settings (
    key text primary key,
    value interval not null,
    constraint retention_settings_positive check (value > interval '0')
);

comment on table ops.retention_settings is
    'FR-065: how long originals are kept after their listing is deleted. The legally required '
    'value is confirmed in the AB 723 review (research R4).';

insert into ops.retention_settings (key, value)
values ('originals_after_listing_deleted', interval '3 years');

-- ───────────────────────────── authorizations ─────────────────────────────

create table ops.purge_authorizations (
    object_name text primary key,
    listing_id uuid not null,
    authorized_by name not null default current_user,
    authorized_at timestamptz not null default timezone('utc'::text, now()),
    purged_at timestamptz
);

comment on table ops.purge_authorizations is
    'Originals the retention job has been cleared to delete. Written only by retention_purger; '
    'the write-once guard also re-checks retention at delete time.';

-- Whoever inserts is recorded as such; the column cannot be filled in on someone else's behalf.
create or replace function ops.stamp_purge_authorization()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.authorized_by := current_user;
    new.authorized_at := timezone('utc', now());
    return new;
end;
$$;

create trigger purge_authorizations_stamp
    before insert on ops.purge_authorizations
    for each row
    execute function ops.stamp_purge_authorization();

revoke all on all tables in schema ops from public, anon, authenticated, service_role;

-- ───────────────────────────── eligibility ─────────────────────────────

-- An original is eligible when its key's listing (keys are {listing_id}/{photo_id}.{ext}) was
-- deleted longer ago than the retention period.
create or replace function ops.original_is_expired(object_name text)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.listings l
        cross join ops.retention_settings s
        where s.key = 'originals_after_listing_deleted'
          and object_name ~ '^[0-9a-f-]{36}/'
          and l.id = split_part(object_name, '/', 1)::uuid
          and l.deleted_at is not null
          and l.deleted_at < timezone('utc', now()) - s.value
    );
$$;

-- What the write-once guard asks: authorized by the purge credential, and still expired.
create or replace function ops.original_purge_authorized(object_name text)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from ops.purge_authorizations a
        where a.object_name = original_purge_authorized.object_name
          and a.authorized_by = 'retention_purger'
          and a.purged_at is null
    )
    and ops.original_is_expired(object_name);
$$;

comment on function ops.original_purge_authorized(text) is
    'FR-065: whether the write-once guard may let this original be deleted.';

revoke all on function ops.original_is_expired(text) from public;
revoke all on function ops.original_purge_authorized(text) from public;

-- ───────────────────────────── the guard, amended ─────────────────────────────

create or replace function public.reject_original_photo_mutation()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    -- The one legitimate deletion (FR-065). An update — overwrite, move, rename — never is.
    if tg_op = 'DELETE' and ops.original_purge_authorized(old.name) then
        return old;
    end if;

    raise exception
        'listing-photos-original is write-once: % of "%" refused (FR-001, FR-065)',
        lower(tg_op), old.name
        using errcode = '42501',
              hint = 'Originals can never be overwritten, moved or deleted through any '
                     'application credential. Retention purging is an out-of-band job.';
end;
$$;

-- The trigger runs as whoever deletes (the Storage API's role); it needs only to ask.
-- The Storage API may act as service_role or as its own admin role, depending on the request.
grant usage on schema ops to service_role, supabase_storage_admin;
grant execute on function ops.original_purge_authorized(text) to service_role, supabase_storage_admin;

-- ───────────────────────────── the purge credential ─────────────────────────────

do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'retention_purger') then
        create role retention_purger nologin;
    end if;
end;
$$;

comment on role retention_purger is
    'FR-065 retention purge job. No login until an environment grants one out of band.';

grant usage on schema ops to retention_purger;
grant select on ops.retention_settings to retention_purger;
grant select, insert, update on ops.purge_authorizations to retention_purger;
grant execute on function ops.original_is_expired(text) to retention_purger;

grant usage on schema public to retention_purger;
grant select (id, deleted_at) on public.listings to retention_purger;
grant insert on public.audit_events to retention_purger;

grant usage on schema storage to retention_purger;
grant select (bucket_id, name) on storage.objects to retention_purger;

-- storage.objects is row-level secured; the purge credential sees originals, and only originals.
create policy "retention_purger_reads_originals"
    on storage.objects for select
    to retention_purger
    using (bucket_id = 'listing-photos-original');

-- audit_events is row-level secured too: the purge credential may append its own events and
-- nothing else — originals_purged, with no actor, since the purge is system-initiated.
create policy "retention_purger_appends_purge_events"
    on public.audit_events for insert
    to retention_purger
    with check (action = 'originals_purged' and actor_id is null);
