-- Feature 002-ai-listing-launch — US3 (T112)
-- Content regeneration runs as a pipeline run of the content step alone. See research R35.
--
-- FR-013: regenerating one kind of copy, in a tone, is a run like any other: queued, progress
--         visible through GET /pipeline, at most one unfinished per listing. A run records which
--         kind it is for; an analysis run generates every kind and records none.

alter table public.pipeline_runs
    add column content_kind public.content_kind;

comment on column public.pipeline_runs.content_kind is
    'The one kind a regeneration run is for. NULL for an analysis run, which generates every kind.';
