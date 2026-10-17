-- Feature 002-ai-listing-launch — US5 (T147)
-- What the approval gate needs that the schema did not yet record. See research R37.
--
-- FR-022/SC-011: nothing unchecked can be approved. A finding-free item and an item never
--         checked look the same in compliance_findings, so each check records the exact content
--         it checked, by hash. An approval requires a check of the content being approved.
-- FR-025: an approval of the listing covers the listing as it was. Whenever the listing leaves
--         `approved` — any edit, by any path — its listing-level approvals are revoked, here in
--         the database, so no code path can forget to.

-- ───────────────────────────── compliance_checks ─────────────────────────────

create table public.compliance_checks (
    listing_id uuid not null references public.listings (id),
    target_type text not null,
    target_id uuid not null,
    -- SHA-256 of the content the check read: a draft's body, or the listing's review hash.
    content_hash text not null,
    checked_at timestamptz not null default timezone('utc'::text, now()),

    primary key (target_type, target_id),

    constraint compliance_checks_target_type_known check (
        target_type in ('listing', 'content_draft', 'photo')
    )
);

comment on table public.compliance_checks is
    'The last compliance check of each item and the exact content it read. Approval requires a '
    'check of the content being approved (SC-011).';

create index compliance_checks_listing_idx on public.compliance_checks (listing_id);

alter table public.compliance_checks enable row level security;

revoke all on table public.compliance_checks from anon, authenticated;

-- ───────────────────────────── listing approvals follow the listing ─────────────────────────────

create or replace function public.revoke_listing_approvals_on_status_change()
returns trigger
language plpgsql
as $$
begin
    if old.status = 'approved' and new.status <> 'approved' then
        update public.approvals
        set revoked_at = timezone('utc', now()),
            revoked_reason = 'listing_changed'
        where listing_id = new.id
          and target_type = 'listing'
          and revoked_at is null;
    end if;

    return new;
end;
$$;

comment on function public.revoke_listing_approvals_on_status_change() is
    'FR-025: a listing that leaves approved loses its listing-level approvals, whatever moved it.';

create trigger listings_revoke_approvals_on_status_change
    after update of status on public.listings
    for each row
    execute function public.revoke_listing_approvals_on_status_change();
