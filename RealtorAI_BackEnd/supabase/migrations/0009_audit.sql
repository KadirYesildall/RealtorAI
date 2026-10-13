-- Feature 002-ai-listing-launch — T022
-- The append-only audit trail. See data-model.md "Audit".
--
-- FR-049 (the spine of SC-004 and SC-008): every AI call, edit, finding, approval, and
--         publish is logged and the log CANNOT be updated or deleted by any application role.
--         Enforced two ways, because either alone is insufficient:
--           1. No UPDATE/DELETE policy and no UPDATE/DELETE grant.
--           2. A trigger that raises on UPDATE or DELETE — this also stops the service role,
--              which bypasses RLS entirely. Without the trigger, the privileged API identity
--              could rewrite history.
-- FR-050: a broker admin reads an agent's events only where the owner's membership is
--         approved.
-- FR-067: listing_id uses the default NO ACTION, so a listing with audit history CANNOT be
--         hard-deleted. This is precisely why soft delete is the only deletion path.

create type public.audit_action as enum (
    'ai_call',
    'field_edit',
    'field_confirm',
    'photo_override',
    'photo_reorder',
    'photo_delete',
    'finding_raised',
    'finding_acknowledged',
    'content_generated',
    'content_edited',
    'approval_granted',
    'approval_revoked',
    'approval_rejected_hash_mismatch',
    'membership_requested',
    'membership_decided',
    'registry_changed',
    'listing_soft_deleted',
    'originals_purged'
);

create table public.audit_events (
    id uuid primary key default gen_random_uuid(),
    -- Deliberately NO "on delete cascade": cascading would destroy immutable records, and
    -- NO ACTION makes a hard delete of the parent listing fail instead (FR-067).
    listing_id uuid references public.listings (id),
    brokerage_id uuid references public.brokerages (id),
    -- Null for system/service actions.
    actor_id uuid references public.profiles (id),
    action public.audit_action not null,
    target_type text,
    target_id uuid,
    -- FR Story 5.2 requires before/after values on edits.
    before_value jsonb,
    after_value jsonb,
    -- FR-049 for ai_call rows (VIII.5 provenance).
    model_id text,
    prompt_version text,
    prompt_hash text,
    -- References rather than inlined payloads, to keep the log compact.
    input_ref text,
    output_ref text,
    payload_hash text,
    created_at timestamptz not null default timezone('utc'::text, now())
);

comment on table public.audit_events is
    'Append-only audit trail. Insert-only for every role including the service role, which '
    'the trigger below enforces. Answers "what exactly did I approve and when?" (SC-008).';

create index audit_events_listing_created_idx
    on public.audit_events (listing_id, created_at desc);
create index audit_events_brokerage_created_idx
    on public.audit_events (brokerage_id, created_at desc);
create index audit_events_action_idx
    on public.audit_events (action);

-- ──────────────── Append-only enforcement ────────────────

create or replace function public.reject_audit_mutation()
returns trigger
language plpgsql
as $$
begin
    raise exception
        'audit_events is append-only (FR-049): % is not permitted on this table.',
        tg_op;
end;
$$;

comment on function public.reject_audit_mutation() is
    'FR-049. Blocks UPDATE and DELETE on audit_events for every role, including the '
    'privileged service role that bypasses RLS.';

-- Statement-level: fires even for an UPDATE/DELETE matching zero rows, so attempts are
-- unambiguously refused rather than silently succeeding.
create trigger audit_events_no_update
    before update on public.audit_events
    for each statement
    execute function public.reject_audit_mutation();

create trigger audit_events_no_delete
    before delete on public.audit_events
    for each statement
    execute function public.reject_audit_mutation();

-- ───────────────────────────── Row Level Security ─────────────────────────────

alter table public.audit_events enable row level security;

-- The owning agent always reads their own listings' events.
create policy "audit_select_own_listings"
    on public.audit_events for select
    using (
        exists (
            select 1 from public.listings l
            where l.id = listing_id and l.agent_id = auth.uid()
        )
    );

-- FR-050: broker admin reads only where the OWNER's membership is approved. A pending or
-- rejected owner's trail stays private.
create policy "audit_select_as_broker_admin"
    on public.audit_events for select
    using (
        exists (
            select 1 from public.listings l
            where l.id = listing_id
              and public.can_read_agent_as_broker_admin(l.agent_id)
        )
    );

-- Membership and registry events carry no listing_id; scope those by brokerage.
create policy "audit_select_brokerage_events_as_admin"
    on public.audit_events for select
    using (
        listing_id is null
        and brokerage_id is not null
        and public.is_approved_broker_admin(brokerage_id)
    );

-- Agents may append their own events; the service role appends the rest.
create policy "audit_insert_self"
    on public.audit_events for insert
    to authenticated
    with check (actor_id = auth.uid());

-- ───────────────────────────── Grants ─────────────────────────────
-- SELECT and INSERT only. No UPDATE, no DELETE — at the grant layer as well as the trigger.

grant select, insert on public.audit_events to authenticated;
