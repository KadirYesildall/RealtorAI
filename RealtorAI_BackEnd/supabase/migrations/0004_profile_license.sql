-- Feature 002-ai-listing-launch — T017
-- Extends feature 001's profiles table with licensing and the platform-administrator flag.
-- See specs/002-ai-listing-launch/data-model.md "profiles (EXISTING — extended)".
--
-- Purely additive: profiles has no brokerage_id column and never had one, so there is no
-- data migration (research.md R6). Brokerage linkage lives in brokerage_memberships (0003).
--
-- FR-058: license_number + license_state are required before a listing can be created.
-- FR-064: platform_admin alone may write the AI model registry and prompt versions, and it
--         MUST NOT be settable through any API path, Server Action, or user-facing surface.
--         A self-grantable admin flag would be a privilege-escalation path straight to the
--         model registry — i.e. to which AI model runs for every tenant.

alter table public.profiles
    add column license_number text,
    add column license_state char(2),
    add column profile_completed_at timestamptz,
    add column platform_admin boolean not null default false;

comment on column public.profiles.license_number is
    'Real estate license number. Required before listing creation (FR-058).';
comment on column public.profiles.license_state is
    'Two-letter licensing state. CA for the CRMLS launch.';
comment on column public.profiles.profile_completed_at is
    'Set once license_number and license_state are supplied. Null blocks listing creation.';
comment on column public.profiles.platform_admin is
    'FR-064. Gates AI registry and prompt writes. NOT writable by any application role — '
    'set only by direct database access. Enforced by the trigger below, not by convention.';

-- ──────────── FR-064: platform_admin is not self-writable ────────────
-- Two layers, because either alone is insufficient:
--   1. Column-level UPDATE grants — the `authenticated` role simply cannot name this column.
--   2. A trigger — belt and braces for any path that acquires broader grants later, and it
--      produces a clear error instead of a silently ignored write.
--
-- Feature 001 granted table-wide UPDATE on profiles. That grant would include
-- platform_admin, so it is revoked and re-issued per column.

revoke update on public.profiles from authenticated;

grant update (
    full_name,
    company_name,
    preferred_language,
    license_number,
    license_state,
    profile_completed_at
) on public.profiles to authenticated;

-- Columns deliberately omitted from the grant above: id, updated_at, subscription_tier,
-- api_usage_count, accepted_terms_at, platform_admin. Billing/usage counters and the admin
-- flag are server-managed; accepted_terms_at is write-once at registration.

create or replace function public.reject_platform_admin_change()
returns trigger
language plpgsql
as $$
begin
    if new.platform_admin is distinct from old.platform_admin then
        raise exception
            'platform_admin cannot be changed through the application (FR-064). '
            'Set it by direct database access only.';
    end if;
    return new;
end;
$$;

comment on function public.reject_platform_admin_change() is
    'FR-064 guard. Blocks privilege escalation to the AI model registry via profile update.';

-- Fires for any caller whose role is not a superuser/owner. A direct psql session as the
-- postgres superuser bypasses triggers only if the trigger is disabled, so operators set the
-- flag with `alter table ... disable trigger` or by a session that owns the table.
create trigger profiles_reject_platform_admin_change
    before update on public.profiles
    for each row
    when (pg_has_role(current_user, 'authenticated', 'member'))
    execute function public.reject_platform_admin_change();

-- Partial index: the admin set is tiny, so only index the rows that matter.
create index profiles_platform_admin_idx
    on public.profiles (id)
    where platform_admin;
