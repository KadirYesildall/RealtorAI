-- Feature 002-ai-listing-launch — T016
-- Brokerages, brokerage memberships (self-declared + broker-admin approved), and settings.
-- See specs/002-ai-listing-launch/data-model.md "Identity & brokerage".
--
-- FR-060: a membership is created `pending` and confers NO brokerage-scoped access in either
--         direction. Only `approved` exposes an agent's listings/audit to brokerage admins.
-- FR-061: a `pending` agent may still draft, analyze, generate, compliance-check, and
--         self-approve LISTING content. Marketing approval and MLS submission are blocked
--         (enforced in the API via capability predicates, not here).
-- FR-062: approval/rejection is audited; the first registrant of a brokerage becomes its
--         initial broker_admin.

-- ───────────────────────────── Enumerations ─────────────────────────────

create type public.membership_status as enum ('pending', 'approved', 'rejected', 'revoked');
create type public.membership_role as enum ('agent', 'broker_admin');

-- Defined here because brokerage_settings.default_tone needs it; content tables (0007) reuse it.
create type public.tone as enum ('professional', 'warm', 'luxury', 'concise');

-- ───────────────────────────── Tables ─────────────────────────────

create table public.brokerages (
    id uuid primary key default gen_random_uuid(),
    name text not null,
    license_number text not null,
    state char(2) not null,
    logo_storage_key text,
    -- State-required license text surfaced on marketing outputs (FR-021, FR-057).
    required_disclaimers jsonb not null default '[]'::jsonb,
    -- Weekends/holidays for the Slice B Clear Cooperation clock; defined now because it
    -- belongs to the brokerage identity record.
    business_calendar jsonb not null default '{}'::jsonb,
    created_at timestamptz not null default timezone('utc'::text, now())
);

comment on table public.brokerages is
    'A brokerage or team. Its name and license info appear in advertising disclosures.';

create table public.brokerage_memberships (
    id uuid primary key default gen_random_uuid(),
    agent_id uuid not null references public.profiles (id) on delete cascade,
    brokerage_id uuid not null references public.brokerages (id) on delete cascade,
    status public.membership_status not null default 'pending',
    role public.membership_role not null default 'agent',
    requested_at timestamptz not null default timezone('utc'::text, now()),
    decided_by uuid references public.profiles (id),
    decided_at timestamptz,
    -- A decision is either fully recorded or not recorded at all.
    constraint membership_decision_complete check (
        (decided_by is null and decided_at is null)
        or (decided_by is not null and decided_at is not null)
    )
);

comment on table public.brokerage_memberships is
    'Links an agent to a brokerage. Only status=approved confers brokerage-scoped access '
    'in either direction (FR-060). Replaces the brokerage_id column that never existed.';

-- An agent belongs to at most one live brokerage; rejected/revoked rows are history.
create unique index brokerage_memberships_one_live_per_agent
    on public.brokerage_memberships (agent_id)
    where status in ('pending', 'approved');

create index brokerage_memberships_agent_idx
    on public.brokerage_memberships (agent_id);
create index brokerage_memberships_brokerage_status_idx
    on public.brokerage_memberships (brokerage_id, status);

create table public.brokerage_settings (
    brokerage_id uuid primary key references public.brokerages (id) on delete cascade,
    -- 'CRMLS' for launch. Resolves the active MLS rule profile (0010).
    target_mls_id text,
    default_tone public.tone not null default 'professional',
    -- FR-055: per-listing AI budget. Enforced in token/image units until provider pricing
    -- is verified (research.md R16).
    ai_budget_tokens int not null default 200000,
    ai_budget_images int not null default 120
);

comment on table public.brokerage_settings is
    'Per-brokerage configuration, split from the identity row so settings churn is isolated.';

-- ──────────────── Access-control helpers (SECURITY DEFINER) ────────────────
-- These exist to break RLS recursion: a policy on brokerage_memberships that needs to ask
-- "is the caller an approved broker admin here?" would otherwise re-enter the same table's
-- policies. SECURITY DEFINER bypasses RLS inside the function body, so the check terminates.
-- Each is STABLE and takes no caller-supplied identity — it always resolves auth.uid().

create or replace function public.is_approved_member(p_brokerage_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
        from public.brokerage_memberships m
        where m.brokerage_id = p_brokerage_id
          and m.agent_id = auth.uid()
          and m.status = 'approved'
    );
$$;

comment on function public.is_approved_member(uuid) is
    'True when the caller holds an approved membership of the given brokerage.';

create or replace function public.is_approved_broker_admin(p_brokerage_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
        from public.brokerage_memberships m
        where m.brokerage_id = p_brokerage_id
          and m.agent_id = auth.uid()
          and m.status = 'approved'
          and m.role = 'broker_admin'
    );
$$;

comment on function public.is_approved_broker_admin(uuid) is
    'True when the caller is an approved broker_admin of the given brokerage. A pending '
    'admin gets nothing.';

-- FR-050 in one predicate: a broker admin may read an agent's data only when BOTH the
-- owner's membership and the admin's own membership are approved in the same brokerage.
create or replace function public.can_read_agent_as_broker_admin(p_agent_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
        from public.brokerage_memberships owner_m
        join public.brokerage_memberships admin_m
            on admin_m.brokerage_id = owner_m.brokerage_id
        where owner_m.agent_id = p_agent_id
          and owner_m.status = 'approved'
          and admin_m.agent_id = auth.uid()
          and admin_m.status = 'approved'
          and admin_m.role = 'broker_admin'
    );
$$;

comment on function public.can_read_agent_as_broker_admin(uuid) is
    'FR-050: broker-admin read access to an agent, requiring BOTH memberships approved. '
    'Returns false while the owner is pending or rejected.';

-- ───────────────────────────── Row Level Security ─────────────────────────────
-- Default-deny, then explicit policies. Note the .NET API connects with a privileged
-- service role that BYPASSES all of this (FR-063) — these policies are defense in depth
-- for any direct client access, and every API endpoint authorizes independently.

alter table public.brokerages enable row level security;
alter table public.brokerage_memberships enable row level security;
alter table public.brokerage_settings enable row level security;

-- brokerages: approved members read; approved broker admins write.
create policy "brokerages_select_approved_members"
    on public.brokerages for select
    using (public.is_approved_member(id));

create policy "brokerages_update_broker_admin"
    on public.brokerages for update
    using (public.is_approved_broker_admin(id));

-- A brand-new brokerage has no members yet, so the first registrant must be able to insert
-- it. The API makes that registrant an approved broker_admin in the same transaction (FR-062).
create policy "brokerages_insert_authenticated"
    on public.brokerages for insert
    to authenticated
    with check (true);

-- brokerage_memberships: an agent sees only their own row; an approved broker admin sees and
-- decides the rows of their own brokerage.
create policy "memberships_select_own"
    on public.brokerage_memberships for select
    using (agent_id = auth.uid());

create policy "memberships_select_as_broker_admin"
    on public.brokerage_memberships for select
    using (public.is_approved_broker_admin(brokerage_id));

create policy "memberships_insert_self_declared"
    on public.brokerage_memberships for insert
    to authenticated
    with check (agent_id = auth.uid());

create policy "memberships_update_as_broker_admin"
    on public.brokerage_memberships for update
    using (public.is_approved_broker_admin(brokerage_id));

-- brokerage_settings: approved members read, approved broker admins write.
create policy "brokerage_settings_select_approved_members"
    on public.brokerage_settings for select
    using (public.is_approved_member(brokerage_id));

create policy "brokerage_settings_write_broker_admin"
    on public.brokerage_settings for all
    using (public.is_approved_broker_admin(brokerage_id))
    with check (public.is_approved_broker_admin(brokerage_id));

-- ───────────────────────────── Grants ─────────────────────────────
-- Recent Supabase CLI versions do not auto-expose new public-schema tables to the Data API
-- roles (see config.toml `auto_expose_new_tables`). RLS restricts WHICH ROWS; GRANT decides
-- whether the role may touch the table at all. Both are required.

grant select, insert, update on public.brokerages to authenticated;
grant select, insert, update on public.brokerage_memberships to authenticated;
grant select, insert, update on public.brokerage_settings to authenticated;

grant execute on function public.is_approved_member(uuid) to authenticated;
grant execute on function public.is_approved_broker_admin(uuid) to authenticated;
grant execute on function public.can_read_agent_as_broker_admin(uuid) to authenticated;
