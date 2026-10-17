-- Feature 002-ai-listing-launch — US4 (T135)
-- Compliance checks run as pipeline runs, and a finding records the rule that raised it.
-- See research R36.
--
-- FR-025: a check can be scoped to one item. A run records which; an unscoped run checks the
--         whole listing.
-- FR-023: an acknowledgement survives a re-check of unchanged words. What makes a finding "the
--         same finding" is its rule, span, phrase and reason, so the rule is stored.

alter table public.pipeline_runs
    add column compliance_target_type text,
    add column compliance_target_id uuid,
    add constraint pipeline_runs_compliance_target check (
        (compliance_target_type is null and compliance_target_id is null)
        or (compliance_target_type in ('listing', 'content_draft', 'photo') and compliance_target_id is not null)
    );

comment on column public.pipeline_runs.compliance_target_type is
    'The one item a compliance run checks. NULL for a run over the whole listing.';

alter table public.compliance_findings
    add column rule_id text not null;

comment on column public.compliance_findings.rule_id is
    'Stable identifier of the rule that raised the finding, e.g. mls.forbidden.phone_number.';

create index findings_target_status_idx
    on public.compliance_findings (target_type, target_id, status);
