-- ════════════════════════════════════════════════════════════════════════════════════════════
-- 0012_privilege_hardening.sql — remove privileges Supabase grants by default
-- ════════════════════════════════════════════════════════════════════════════════════════════
--
-- FOUND DURING IMPLEMENTATION, by T026's grant assertion. This is a correction, not a
-- refinement.
--
-- The Supabase CLI ships this in the public schema:
--
--     alter default privileges in schema public
--         grant all on tables to anon, authenticated, service_role;
--
-- so every table created by migrations 0003-0011 silently received ALL privileges for `anon`
-- and `authenticated` — SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER —
-- regardless of the narrower `grant` statements those migrations wrote. The explicit grants
-- added privileges that were already there, so they read as if they were restricting access
-- while restricting nothing. 0006_media.sql states "photo_embeddings: intentionally no grant
-- to authenticated"; that comment was false the moment the table was created.
--
-- Why this mattered rather than being untidy: **TRUNCATE is not subject to row-level
-- security.** RLS filters SELECT, INSERT, UPDATE and DELETE; it has no bearing on TRUNCATE,
-- which needs only the privilege. And the append-only trigger from 0009 fires BEFORE UPDATE OR
-- DELETE, which does not include TRUNCATE either.
--
-- Verified before writing this file, as the `authenticated` role with auth.uid() set to Agent C
-- (an approved agent at an unrelated brokerage who owns no listings):
--
--     select count(*) from public.listings;              -- 0: RLS correctly hid every row
--     truncate public.listings, public.audit_events cascade;
--     -- TRUNCATE TABLE
--     -- cascaded to: listing_fields, photos, content_drafts, compliance_findings,
--     --              approvals, ai_usage, photo_analyses, photo_embeddings
--
-- An ordinary signed-in agent could therefore destroy every listing and the entire audit trail
-- for every brokerage, while RLS was working exactly as designed. FR-049 (immutable audit) and
-- FR-065 (no hard delete) were both reachable this way.
--
-- Not an active breach in the deployed shape — PostgREST does not expose TRUNCATE, so there is
-- no known request that reaches it today. That is an accident of the client in front of the
-- database, not a property of the database, and Principle VII requires the privilege not to
-- exist. The audit-immutability claim was simply not true as written.
--
-- Approach: revoke everything from `anon` and `authenticated` on each table this feature owns,
-- then grant back exactly the commands its policies are written for. Privileges decide which
-- COMMANDS a role may attempt; policies decide which ROWS. Both are needed, and until now only
-- the second was actually configured.
-- ════════════════════════════════════════════════════════════════════════════════════════════

-- ───────────────────────────── Future tables ─────────────────────────────
--
-- Stop new tables inheriting the same grant. This changes only the default privileges owned by
-- the current role, which is the role migrations run as, so tables created by later migrations
-- start with no client privileges and must grant deliberately. Supabase's own internal
-- defaults (owned by supabase_admin) are left alone — they are not ours to change, and hosted
-- projects do not permit it.
--
-- Consequence to be aware of: a future migration that creates a table and forgets to grant will
-- find it unreadable by the API's client roles. That is the intended direction of failure.

alter default privileges in schema public
    revoke all on tables from anon, authenticated;

-- ───────────────────────────── anon gets nothing ─────────────────────────────
--
-- No part of this feature is public. Listing data, photos, compliance findings and audit
-- records are all brokerage-confidential, so the pre-authentication role has no business
-- holding any privilege on them.

revoke all on table
    public.brokerages,
    public.brokerage_memberships,
    public.brokerage_settings,
    public.listings,
    public.listing_fields,
    public.mls_rule_profiles,
    public.photos,
    public.photo_analyses,
    public.photo_embeddings,
    public.content_drafts,
    public.compliance_findings,
    public.approvals,
    public.ai_model_registry,
    public.prompt_versions,
    public.ai_usage,
    public.audit_events
from anon;

-- ───────────────────────────── authenticated: reset ─────────────────────────────

revoke all on table
    public.brokerages,
    public.brokerage_memberships,
    public.brokerage_settings,
    public.listings,
    public.listing_fields,
    public.mls_rule_profiles,
    public.photos,
    public.photo_analyses,
    public.photo_embeddings,
    public.content_drafts,
    public.compliance_findings,
    public.approvals,
    public.ai_model_registry,
    public.prompt_versions,
    public.ai_usage,
    public.audit_events
from authenticated;

-- ───────────────────────────── authenticated: grant back, per table ─────────────────────────────
--
-- TRUNCATE, REFERENCES and TRIGGER are granted to nobody, anywhere below. TRUNCATE because it
-- ignores RLS; REFERENCES because a foreign key pointed at a table is a way to probe for the
-- existence of rows RLS is hiding; TRIGGER because a trigger authored by a client role would
-- run with that role's rights on every write.

-- Brokerage directory: agents create a brokerage, request membership, and broker admins edit.
grant select, insert, update on public.brokerages to authenticated;
grant select, insert, update on public.brokerage_memberships to authenticated;

-- Settings are created and edited, never deleted — a brokerage without settings would have no
-- budget or disclaimer configuration, and the ALL policy would otherwise permit removing them.
grant select, insert, update on public.brokerage_settings to authenticated;

-- Listings: NO DELETE. FR-065 allows only soft delete, and until now that rested on
-- audit_events.listing_id happening to be a NO ACTION foreign key — a listing with no audit
-- rows yet would have been hard-deletable. Withholding the privilege makes it unconditional.
grant select, insert, update on public.listings to authenticated;

-- Fields and photos can genuinely be removed from a listing while it is being prepared; both
-- removals are audited actions (audit_action includes photo_delete).
grant select, insert, update, delete on public.listing_fields to authenticated;
grant select, insert, update, delete on public.photos to authenticated;

-- Model output about photos is written by the pipeline under the service role, and is read-only
-- to clients: an agent overrides a conclusion by recording an override, not by editing the
-- analysis that produced it (FR-049 keeps the original visible).
grant select on public.photo_analyses to authenticated;

-- photo_embeddings: nothing, which is now actually true. Embeddings are an internal artefact
-- for near-duplicate detection and a raw vector is a fingerprint of the image it came from.
-- The table has RLS enabled and no policy; it now also has no privilege.

-- Drafts are generated, edited, selected and discarded by the owning agent.
grant select, insert, update, delete on public.content_drafts to authenticated;

-- Findings are raised by the compliance engine, not by clients. UPDATE is granted only because
-- acknowledging a warning is an update; the check constraint still forbids acknowledging a
-- blocking finding, and no INSERT or DELETE means an agent cannot manufacture a clean result or
-- erase an inconvenient one.
grant select, update on public.compliance_findings to authenticated;

-- Approvals are inserted by the owning agent (that INSERT is the human-in-the-loop act under
-- VIII.7) and updated only to revoke. No DELETE: an approval that was granted stays on record
-- even after revocation.
grant select, insert, update on public.approvals to authenticated;

-- Registry and prompts are platform-admin surfaces. The privileges are needed because a
-- platform admin is an ordinary authenticated user with a flag; the RLS policies restrict the
-- rows to those admins, and the VIII.4 check constraint still governs what may be enabled.
grant select, insert, update, delete on public.ai_model_registry to authenticated;
grant select, insert, update, delete on public.prompt_versions to authenticated;

-- Rule profiles: everyone reads them, platform admins maintain them.
grant select, insert, update, delete on public.mls_rule_profiles to authenticated;

-- Usage is written by the pipeline and read by the agent whose listing incurred it.
grant select on public.ai_usage to authenticated;

-- Audit: read what the policies expose, append own events, and nothing else. UPDATE and DELETE
-- are withheld in addition to the trigger, so immutability no longer depends on the trigger
-- alone.
grant select, insert on public.audit_events to authenticated;

-- ───────────────────────────── TRUNCATE guard on the audit trail ─────────────────────────────
--
-- Defence in depth for the one NON-NEGOTIABLE guarantee in this schema. The privilege is now
-- withheld from client roles, but the API's own connection is privileged and a future migration
-- could re-grant by accident; this makes truncating the audit trail fail regardless of who
-- attempts it, including a superuser.
--
-- A separate trigger is required because BEFORE TRUNCATE cannot be combined with BEFORE UPDATE
-- OR DELETE in one definition: TRUNCATE triggers must be FOR EACH STATEMENT and may not share a
-- trigger with row-level events.

create or replace function public.reject_audit_truncate()
returns trigger language plpgsql as $$
begin
    raise exception
        'audit_events is append-only (FR-049): TRUNCATE is not permitted on this table. '
        'Note that TRUNCATE bypasses row-level security entirely, which is why this guard '
        'exists separately from the UPDATE/DELETE trigger.';
end;
$$;

create trigger audit_events_reject_truncate
    before truncate on public.audit_events
    for each statement
    execute function public.reject_audit_truncate();

-- The same reasoning applies to listings: it is the parent of the cascade that reached eight
-- other tables in the verification above, and FR-065 says a listing is never removed.

create or replace function public.reject_listings_truncate()
returns trigger language plpgsql as $$
begin
    raise exception
        'listings cannot be truncated (FR-065). Soft-delete sets deleted_at and the archived '
        'status; photo originals are purged out of band after the retention period.';
end;
$$;

create trigger listings_reject_truncate
    before truncate on public.listings
    for each statement
    execute function public.reject_listings_truncate();

comment on function public.reject_audit_truncate() is
    'Blocks TRUNCATE on audit_events. TRUNCATE is not subject to RLS and is not covered by '
    'the BEFORE UPDATE OR DELETE trigger, so it needs its own guard (see 0012).';

comment on function public.reject_listings_truncate() is
    'Blocks TRUNCATE on listings, which would otherwise cascade to every child table (see 0012).';
