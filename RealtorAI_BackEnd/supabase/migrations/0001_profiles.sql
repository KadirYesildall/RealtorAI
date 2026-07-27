-- Feature 001-auth-base-dashboard
-- Workspace Profile table: one row per authenticated user (email/password or Google),
-- auto-provisioned by the handle_new_user trigger. See specs/001-auth-base-dashboard/data-model.md.

create table public.profiles (
    id uuid references auth.users on delete cascade primary key,
    updated_at timestamp with time zone default timezone('utc'::text, now()) not null,
    full_name text,
    company_name text,
    preferred_language varchar(5) default 'en' not null,
    subscription_tier varchar(20) default 'free' not null,
    api_usage_count int default 0 not null,
    accepted_terms_at timestamp with time zone default timezone('utc'::text, now()) not null
);

comment on table public.profiles is
    'One row per authenticated RealtorAI agent. Created automatically by handle_new_user.';

-- Row Level Security: default-deny until explicit policies are added below.
alter table public.profiles enable row level security;

-- A user may read only their own profile.
create policy "profiles_select_own"
    on public.profiles for select
    using (auth.uid() = id);

-- A user may update only their own profile.
create policy "profiles_update_own"
    on public.profiles for update
    using (auth.uid() = id);

-- No INSERT/DELETE policies: profile rows are created only by the SECURITY DEFINER
-- trigger below and removed only via the ON DELETE CASCADE from auth.users.

-- Recent Supabase CLI versions no longer auto-expose new public-schema tables to the
-- Data API roles (see RealtorAI_BackEnd/supabase/config.toml, `auto_expose_new_tables`).
-- Table-level privileges must be granted explicitly, on top of the RLS policies above —
-- RLS restricts *which rows*, GRANT restricts *whether the role can touch the table at all*.
grant select, update on public.profiles to authenticated;

-- Auto-create a profile row whenever a new auth.users row is created, regardless of
-- signup method (email/password or Google OAuth). Runs as SECURITY DEFINER so it can
-- insert into public.profiles despite the caller having no INSERT grant/policy.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.profiles (id, full_name, preferred_language)
    values (
        new.id,
        new.raw_user_meta_data ->> 'full_name',
        'en'
    );
    return new;
end;
$$;

create trigger on_auth_user_created
    after insert on auth.users
    for each row execute procedure public.handle_new_user();
